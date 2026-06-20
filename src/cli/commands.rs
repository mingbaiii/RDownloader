use std::path::Path;

use super::parser::{CliConfig, Command, generate_id, show_help, show_help_json};
use super::state::{self, DownloadInfo, DownloadStatus, EndpointInfo};
use crate::endpoint_test::EndpointList;
use crate::remote_file::RemoteFile;

/// 执行命令
pub async fn execute_command(command: Command, config: &CliConfig) {
    match command {
        Command::Download { url, output } => {
            cmd_download(&url, output.as_deref(), config).await;
        }
        Command::List => {
            cmd_list(config);
        }
        Command::Info { id } => {
            cmd_info(&id, config).await;
        }
        Command::InfoAll { id } => {
            cmd_info_all(&id, config).await;
        }
        Command::Monitor { id } => {
            cmd_monitor(&id, config).await;
        }
        Command::Pause { id } => {
            cmd_pause(&id, config);
        }
        Command::Resume { id } => {
            cmd_resume(&id, config).await;
        }
        Command::Cancel { id } => {
            cmd_cancel(&id, config).await;
        }
        Command::AddEndpoint { id, ip, proxy } => {
            cmd_add_endpoint(&id, &ip, proxy.as_deref(), config).await;
        }
        Command::DisableEndpoint { id, ip } => {
            cmd_disable_endpoint(&id, &ip, config).await;
        }
        Command::EnableEndpoint { id, ip } => {
            cmd_enable_endpoint(&id, &ip, config).await;
        }
        Command::SetConfig { key, value } => {
            cmd_set_config(&key, &value, config).await;
        }
        Command::Loop => {
            cmd_loop(config).await;
        }
        Command::Help => {
            if config.json_mode {
                show_help_json(config.config_path.as_ref().map(|p| p.to_str().unwrap_or("")));
            } else {
                show_help(config.config_path.as_ref().map(|p| p.to_str().unwrap_or("")));
            }
        }
        Command::Exit => {
            // 暂停所有下载
            cmd_pause_all(config);
            std::process::exit(0);
        }
    }
}

/// 下载命令
async fn cmd_download(url: &str, output: Option<&str>, config: &CliConfig) {
    // 确定输出文件名
    let user_specified_output = output.is_some();
    let mut output_name = match output {
        Some(name) => name.to_string(),
        None => {
            // 从 URL 提取文件名（默认值，后续会被 Content-Disposition 覆盖）
            match crate::utils::parse_url(url) {
                Ok(url_info) => {
                    // 排除 query string（? 之后的部分）
                    let path = url_info.path.split('?').next().unwrap_or(&url_info.path);
                    let filename = path.split('/').last().unwrap_or("index.html");
                    if filename.is_empty() {
                        "index.html".to_string()
                    } else {
                        filename.to_string()
                    }
                }
                Err(_) => "download".to_string(),
            }
        }
    };

    // 生成唯一 ID
    let id = loop {
        let id = generate_id();
        if !state::id_exists(&id) {
            break id;
        }
    };

    // 创建下载信息
    let mut info = DownloadInfo::new(id.clone(), url.to_string(), output_name.clone());

    // 获取 endpoint 信息
    let mut remote_file = crate::remote_file::RemoteFile::new(url);

    // 添加自定义 headers 和代理配置
    let global_config = crate::config::get_global_config().await;
    for (name, value) in &global_config.headers {
        remote_file.request_headers.insert(name.clone(), value.clone());
    }
    // 添加代理配置
    for (key, proxy_url) in &global_config.proxy {
        remote_file.proxy_map.insert(key.clone(), proxy_url.clone());
    }
    // 使用全局配置的连接数（而非默认值 4）
    info.connections = global_config.total_connections;
    drop(global_config);

    let mut endpoints_perf_json = String::new();

    if config.json_mode {
        // 程序模式，静默获取信息
        match remote_file.get_information().await {
            Ok(()) => {
                if let Some(endpoint_list) = &remote_file.endpoint_list {
                    for (i, ep) in endpoint_list.endpoints.iter().enumerate() {
                        // 获取该 endpoint 使用的代理
                        let proxy = remote_file.get_endpoint_proxy(&ep.ip).cloned();
                        // 测试后不可用的 endpoint 自动禁用
                        info.endpoints.push(EndpointInfo {
                            id: i,
                            ip: ep.ip,
                            enabled: ep.is_available(),
                            proxy,
                        });
                    }

                    // 从响应头获取文件大小（大小写不敏感）
                    info.file_size = remote_file.response_headers
                        .iter()
                        .find(|(k, _)| k.eq_ignore_ascii_case("content-length"))
                        .and_then(|(_, v)| v.parse::<u64>().ok())
                        .unwrap_or(0);

                    // 无 Content-Length 或不支持 Range 时强制单连接
                    let accept_ranges = remote_file.response_headers
                        .iter()
                        .find(|(k, _)| k.eq_ignore_ascii_case("accept-ranges"))
                        .map(|(_, v)| v.as_str())
                        .unwrap_or("none");
                    if info.file_size == 0 || accept_ranges == "none" {
                        info.connections = 1;
                    }

                    // 保存响应头（用于 resume 时检测 Range 支持等）
                    info.response_headers = remote_file.response_headers.clone();

                    // 如果用户未指定输出名，优先使用 Content-Disposition 中的文件名
                    if !user_specified_output {
                        let cd_value = remote_file.response_headers.iter()
                            .find(|(k, _)| k.eq_ignore_ascii_case("content-disposition"))
                            .map(|(_, v)| v.as_str());
                        if let Some(cd_value) = cd_value {
                            if let Some(cd_filename) = crate::utils::extract_filename_from_content_disposition(cd_value) {
                                output_name = cd_filename;
                                info.output = output_name.clone();
                            }
                        }
                    }

                    // 保存实际 URL（重定向后）
                    info.actual_url = remote_file.url.clone();

                    // 构建 endpoint 性能测试结果 JSON
                    let endpoint_jsons: Vec<String> = endpoint_list.endpoints.iter().enumerate().map(|(i, ep)| {
                        let best_latency = ep.best_latency().map(|d| d.as_secs_f64() * 1000.0);

                        let icmp_json = ep.icmp_ping.as_ref().map(|p| {
                            format!(r#"{{"success": {}, "rtt_ms": {}}}"#,
                                p.success,
                                p.rtt.map(|d| format!("{:.1}", d.as_secs_f64() * 1000.0)).unwrap_or_else(|| "null".to_string()))
                        }).unwrap_or_else(|| "null".to_string());

                        let tcp_json = ep.tcp_ping.as_ref().map(|p| {
                            format!(r#"{{"success": {}, "port": {}, "latency_ms": {}}}"#,
                                p.success,
                                p.port.map(|port| port.to_string()).unwrap_or_else(|| "null".to_string()),
                                p.latency.map(|d| format!("{:.1}", d.as_secs_f64() * 1000.0)).unwrap_or_else(|| "null".to_string()))
                        }).unwrap_or_else(|| "null".to_string());

                        let speed_json = ep.speed.as_ref().map(|s| {
                            format!(r#"{{"success": {}, "status_code": {}, "header_size": {}, "latency_ms": {}, "speed_bps": {}, "final_url": {}}}"#,
                                s.success,
                                s.status_code.map(|c| c.to_string()).unwrap_or_else(|| "null".to_string()),
                                s.header_size.map(|h| h.to_string()).unwrap_or_else(|| "null".to_string()),
                                s.latency.map(|d| format!("{:.1}", d.as_secs_f64() * 1000.0)).unwrap_or_else(|| "null".to_string()),
                                s.speed.map(|sp| format!("{:.1}", sp)).unwrap_or_else(|| "null".to_string()),
                                s.final_url.as_ref().map(|u| format!("\"{}\"", u.replace('\\', "\\\\").replace('"', "\\\""))).unwrap_or_else(|| "null".to_string()))
                        }).unwrap_or_else(|| "null".to_string());

                        format!(r#"{{"id": {}, "ip": "{}", "icmp_ping": {}, "tcp_ping": {}, "speed_test": {}, "best_latency_ms": {}, "is_available": {}, "enabled": {}}}"#,
                            i, ep.ip, icmp_json, tcp_json, speed_json,
                            best_latency.map(|l| format!("{:.1}", l)).unwrap_or_else(|| "null".to_string()),
                            ep.is_available(),
                            ep.is_available())
                    }).collect();
                    endpoints_perf_json = endpoint_jsons.join(", ");
                }
            }
            Err(e) => {
                println!(r#"{{"status": "error", "message": "{}"}}"#, e);
                return;
            }
        }
    } else {
        // 用户模式，显示进度
        println!("获取信息...");
        match remote_file.get_information().await {
            Ok(()) => {
                if let Some(endpoint_list) = &remote_file.endpoint_list {
                    for (i, ep) in endpoint_list.endpoints.iter().enumerate() {
                        // 获取该 endpoint 使用的代理
                        let proxy = remote_file.get_endpoint_proxy(&ep.ip).cloned();
                        // 测试后不可用的 endpoint 自动禁用
                        info.endpoints.push(EndpointInfo {
                            id: i,
                            ip: ep.ip,
                            enabled: ep.is_available(),
                            proxy,
                        });
                    }

                    info.file_size = remote_file.response_headers
                        .iter()
                        .find(|(k, _)| k.eq_ignore_ascii_case("content-length"))
                        .and_then(|(_, v)| v.parse::<u64>().ok())
                        .unwrap_or(0);

                    // 无 Content-Length 或不支持 Range 时强制单连接
                    let accept_ranges = remote_file.response_headers
                        .iter()
                        .find(|(k, _)| k.eq_ignore_ascii_case("accept-ranges"))
                        .map(|(_, v)| v.as_str())
                        .unwrap_or("none");
                    if info.file_size == 0 || accept_ranges == "none" {
                        info.connections = 1;
                    }

                    // 保存响应头（用于 resume 时检测 Range 支持等）
                    info.response_headers = remote_file.response_headers.clone();

                    // 如果用户未指定输出名，优先使用 Content-Disposition 中的文件名
                    if !user_specified_output {
                        let cd_value = remote_file.response_headers.iter()
                            .find(|(k, _)| k.eq_ignore_ascii_case("content-disposition"))
                            .map(|(_, v)| v.as_str());
                        if let Some(cd_value) = cd_value {
                            if let Some(cd_filename) = crate::utils::extract_filename_from_content_disposition(cd_value) {
                                output_name = cd_filename;
                                info.output = output_name.clone();
                            }
                        }
                    }

                    // 保存实际 URL（重定向后）
                    info.actual_url = remote_file.url.clone();

                    // 显示最终 URL
                    if remote_file.url != url {
                        println!("重定向到: {}", remote_file.url);
                    }

                    // 显示 endpoint 测试结果
                    println!("\nEndpoint 测试结果:");
                    println!("{:-<70}", "");
                    println!("{:<20} {:<12} {:<10} {:<15} {:<8}", "IP", "延迟", "状态码", "速度", "可用");
                    println!("{:-<70}", "");

                    for ep in &endpoint_list.endpoints {
                        let latency_str = ep.best_latency()
                            .map(|l| format!("{:?}", l))
                            .unwrap_or_else(|| "-".to_string());

                        let (status_str, speed_str) = if let Some(speed) = &ep.speed {
                            let status = speed.status_code
                                .map(|s| s.to_string())
                                .unwrap_or_else(|| "-".to_string());
                            let spd = speed.speed
                                .map(|s| format_speed(s))
                                .unwrap_or_else(|| "-".to_string());
                            (status, spd)
                        } else {
                            ("-".to_string(), "-".to_string())
                        };

                        let available_str = if ep.is_available() { "✓" } else { "✗" };

                        println!("{:<20} {:<12} {:<10} {:<15} {:<8}",
                            ep.ip, latency_str, status_str, speed_str, available_str);
                    }

                    println!("{:-<70}", "");
                    println!("找到 {} 个 endpoint", info.endpoints.len());
                }
            }
            Err(e) => {
                eprintln!("Error: {}", e);
                return;
            }
        }
    }

    // 保存下载信息
    match state::save_download(&info) {
        Ok(()) => {
            if config.json_mode {
                // 构建 response_headers JSON
                let headers_json: String = {
                    let entries: Vec<String> = remote_file.response_headers.iter()
                        .map(|(k, v)| format!(r#""{}": "{}""#, k.replace('\\', "\\\\").replace('"', "\\\""), v.replace('\\', "\\\\").replace('"', "\\\"")))
                        .collect();
                    format!("{{{}}}", entries.join(", "))
                };
                println!(r#"{{"status": "ok", "id": "{}", "output": "{}", "response_headers": {}, "endpoints": [{}]}}"#, id, output_name, headers_json, endpoints_perf_json);
            } else {
                println!("Download {} created: {}", output_name, id);
            }

            // 在非 JSON 模式下（交互式/命令行），立即启动下载
            if !config.json_mode {
                // 使用 remote_file 中已测试的 endpoint list
                let endpoint_list = match &remote_file.endpoint_list {
                    Some(list) => list.clone(),
                    None => {
                        eprintln!("Error: No endpoints available");
                        // 标记为失败
                        if let Some((_, mut download_info)) = state::find_download_by_id(&id) {
                            download_info.status = DownloadStatus::Failed;
                            let _ = state::save_download(&download_info);
                        }
                        return;
                    }
                };

                // 检查是否有可用的 endpoint
                let available_count = endpoint_list.available().len();
                if available_count == 0 {
                    eprintln!("Error: All endpoints are unavailable");
                    // 标记为失败
                    if let Some((_, mut download_info)) = state::find_download_by_id(&id) {
                        download_info.status = DownloadStatus::Failed;
                        let _ = state::save_download(&download_info);
                    }
                    return;
                }

                // 启动下载（传入已保存的连接数用于断点续传）
                // saved_connections 始终从 task JSON 读取，不依赖 segments 是否为空
                let saved_connections = if info.connections > 0 {
                    Some(info.connections)
                } else {
                    None
                };

                match crate::downloader::start_download(
                    info.file_size,
                    &output_name,
                    &remote_file,
                    &endpoint_list,
                    &id,
                    if info.segments.is_empty() { None } else { Some(info.segments.clone()) },
                    saved_connections,
                    Some(info.endpoints.len()),
                    url,
                    &remote_file.url,
                ).await {
                    Ok(handle) => {
                        // 更新状态为下载中，并从 handle 中读取实际的连接数
                        if let Some((_, mut download_info)) = state::find_download_by_id(&id) {
                            download_info.status = DownloadStatus::Downloading;
                            // 无 Content-Length 时不能断点续传，重置 downloaded 为 0
                            if download_info.file_size == 0 {
                                download_info.downloaded = 0;
                                for seg in &mut download_info.segments {
                                    seg.downloaded = 0;
                                }
                            }
                            // 从 handle 中读取实际分配的连接数
                            let actual_connections = {
                                let state = handle.state.lock().await;
                                state.connections.len()
                            };
                            download_info.connections = actual_connections;
                            let _ = state::save_download(&download_info);
                        }

                        // 注册到下载管理器
                        let mut manager = super::manager::DOWNLOAD_MANAGER.lock().await;
                        manager.register(id.clone(), handle);
                        drop(manager);

                        // 启动后台任务监控下载完成状态
                        let download_id = id.clone();
                        tokio::spawn(async move {
                            loop {
                                tokio::time::sleep(std::time::Duration::from_secs(1)).await;

                                let manager = super::manager::DOWNLOAD_MANAGER.lock().await;
                                if let Some(handle_arc) = manager.get(&download_id) {
                                    let handle = handle_arc.lock().await;
                                    let progress = handle.get_progress().await;
                                    // 获取当前 segment 进度 + completed 标志
                                    let (current_segments, stream_completed) = {
                                        let state = handle.state.lock().await;
                                        (state.get_segment_progress(), state.completed.load(std::sync::atomic::Ordering::Relaxed))
                                    };
                                    drop(handle);
                                    drop(manager);

                                    // 更新进度到文件（保留 segments）
                                    if let Some((_, mut download_info)) = state::find_download_by_id(&download_id) {
                                        download_info.downloaded = progress.downloaded_bytes;
                                        // 更新 segments
                                        download_info.segments = current_segments.iter().map(|seg| {
                                            super::state::SegmentProgress {
                                                start: seg.start,
                                                end: seg.end,
                                                downloaded: seg.downloaded,
                                                completed: seg.downloaded >= (seg.end - seg.start),
                                                endpoint_id: seg.endpoint_id,
                                            }
                                        }).collect();
                                        // 同步 connections 为实际分片数
                                        download_info.connections = download_info.segments.len();
                                        let _ = state::save_download(&download_info);
                                    }

                                    // 检查是否完成
                                    // 对于未知大小的下载（total_bytes=0），通过 completed 标志判断
                                    if progress.percentage >= 100.0 || (progress.total_bytes == 0 && stream_completed) {
                                        // 标记为完成：所有字段置 100% 再删除 .rdown.json
                                        if let Some((path, mut download_info)) = state::find_download_by_id(&download_id) {
                                            download_info.status = DownloadStatus::Completed;
                                            if download_info.file_size == 0 {
                                                download_info.file_size = progress.downloaded_bytes;
                                            }
                                            download_info.downloaded = download_info.file_size;
                                            let _ = state::save_download(&download_info);
                                            let _ = std::fs::remove_file(&path);
                                        }

                                        println!("Download completed: {}", download_id);
                                        let mut manager = super::manager::DOWNLOAD_MANAGER.lock().await;
                                        manager.remove(&download_id);
                                        break;
                                    }

                                    // 检查是否被取消（所有 endpoint 都被禁用）
                                    let is_cancelled = {
                                        let mgr = super::manager::DOWNLOAD_MANAGER.lock().await;
                                        if let Some(h) = mgr.get(&download_id) {
                                            let handle = h.lock().await;
                                            let s = handle.state.lock().await;
                                            s.cancelled.load(std::sync::atomic::Ordering::Relaxed)
                                        } else {
                                            false
                                        }
                                    };

                                    if is_cancelled {
                                        // 标记为失败
                                        if let Some((_, mut di)) = state::find_download_by_id(&download_id) {
                                            di.status = DownloadStatus::Failed;
                                            let _ = state::save_download(&di);
                                        }
                                        println!("Download failed: {} (all endpoints disabled)", download_id);
                                        let mut mgr = super::manager::DOWNLOAD_MANAGER.lock().await;
                                        mgr.remove(&download_id);
                                        break;
                                    }
                                } else {
                                    break;
                                }
                            }
                        });

                        println!("Download started. Use 'monitor {}' to watch progress.", id);
                    }
                    Err(e) => {
                        if let Some((_, mut download_info)) = state::find_download_by_id(&id) {
                            download_info.status = DownloadStatus::Failed;
                            let _ = state::save_download(&download_info);
                        }
                        eprintln!("Download error: {}", e);
                    }
                }
            }
        }
        Err(e) => {
            if config.json_mode {
                println!(r#"{{"status": "error", "message": "{}"}}"#, e);
            } else {
                eprintln!("Error: {}", e);
            }
        }
    }
}

/// 列出所有下载
fn cmd_list(config: &CliConfig) {
    let downloads = state::load_all_downloads();

    if config.json_mode {
        let items: Vec<String> = downloads.iter().map(|d| {
            format!(
                r#"{{"id": "{}", "url": "{}", "output": "{}", "status": "{}", "progress": {:.1}, "file_size": {}, "downloaded": {}}}"#,
                d.id, d.url, d.output, d.status, d.progress(), d.file_size, d.downloaded
            )
        }).collect();
        println!(r#"{{"status": "ok", "downloads": [{}]}}"#, items.join(", "));
    } else {
        if downloads.is_empty() {
            println!("No downloads found");
            return;
        }

        println!("{:<12} {:<40} {:<12} {:<10} {:<10}", "ID", "URL", "Status", "Progress", "Size");
        println!("{}", "-".repeat(84));

        for d in &downloads {
            let url_display = if d.url.len() > 38 {
                format!("{}...", &d.url[..35])
            } else {
                d.url.clone()
            };

            let size_display = if d.file_size > 0 {
                format_bytes(d.file_size)
            } else {
                "-".to_string()
            };

            println!(
                "{:<12} {:<40} {:<12} {:<10} {:<10}",
                d.id,
                url_display,
                d.status,
                format!("{:.1}%", d.progress()),
                size_display
            );
        }
    }
}

/// 获取下载详情
async fn cmd_info(id: &str, config: &CliConfig) {
    match state::find_download_by_id(id) {
        Some((_, info)) => {
            if config.json_mode {
                // 尝试从运行中的下载获取实时进度（而非仅从保存的 JSON 获取可能过时的数据）
                let (live_downloaded, live_file_size, live_progress, live_speed) = {
                    let manager = super::manager::DOWNLOAD_MANAGER.lock().await;
                    if let Some(handle_arc) = manager.get(&info.id) {
                        let handle = handle_arc.lock().await;
                        let progress = handle.get_progress().await;
                        // 优先使用 live 数据（对 chunked 下载尤其重要，file_size 动态更新）
                        let file_size = if progress.total_bytes > 0 { progress.total_bytes } else { info.file_size };
                        (progress.downloaded_bytes, file_size, progress.percentage, progress.speed)
                    } else {
                        (info.downloaded, info.file_size, info.progress(), 0.0)
                    }
                };

                let endpoints: Vec<String> = info.endpoints.iter().map(|ep| {
                    format!(
                        r#"{{"ip": "{}", "enabled": {}, "proxy": {}}}"#,
                        ep.ip,
                        ep.enabled,
                        ep.proxy.as_ref().map(|p| format!("\"{}\"", p)).unwrap_or("null".to_string())
                    )
                }).collect();

                println!(
                    r#"{{"status": "ok", "download": {{"id": "{}", "url": "{}", "output": "{}", "file_size": {}, "downloaded": {}, "progress": {:.1}, "status": "{}", "speed": {:.1}, "endpoints": [{}]}}}}"#,
                    info.id, info.url, info.output, live_file_size, live_downloaded,
                    live_progress, info.status, live_speed, endpoints.join(", ")
                );
            } else {
                // 尝试从运行中的下载获取实时进度
                let (live_downloaded, live_file_size, live_percentage) = {
                    let manager = super::manager::DOWNLOAD_MANAGER.lock().await;
                    if let Some(handle_arc) = manager.get(&info.id) {
                        let handle = handle_arc.lock().await;
                        let progress = handle.get_progress().await;
                        let file_size = if progress.total_bytes > 0 { progress.total_bytes } else { info.file_size };
                        (progress.downloaded_bytes, file_size, progress.percentage)
                    } else {
                        (info.downloaded, info.file_size, info.progress())
                    }
                };

                println!("Download: {}", info.id);
                println!("URL: {}", info.url);
                println!("Output: {}", info.output);
                println!("Status: {}", info.status);
                println!("File Size: {}", format_bytes(live_file_size));
                println!("Downloaded: {}", format_bytes(live_downloaded));
                println!("Progress: {:.1}%", live_percentage);
                println!("Created: {}", info.created_at);
                println!("Endpoints:");
                for ep in &info.endpoints {
                    let status = if ep.enabled { "✓ Enabled" } else { "✗ Disabled" };
                    println!("  {} {}", ep.ip, status);
                }
            }
        }
        None => {
            if config.json_mode {
                println!(r#"{{"status": "error", "message": "Download not found"}}"#);
            } else {
                eprintln!("Error: Download '{}' not found", id);
            }
        }
    }
}

/// 获取下载详情（包含 segments）
async fn cmd_info_all(id: &str, config: &CliConfig) {
    match state::find_download_by_id(id) {
        Some((_, info)) => {
            if config.json_mode {
                // 尝试从运行中的下载获取每 segment 的速度
                let segment_speeds: Vec<f64> = {
                    let manager = super::manager::DOWNLOAD_MANAGER.lock().await;
                    if let Some(handle_arc) = manager.get(&info.id) {
                        let handle = handle_arc.lock().await;
                        let state = handle.state.lock().await;
                        let mut speed_map: std::collections::HashMap<usize, f64> = std::collections::HashMap::new();
                        for conn in &state.connections {
                            if let Some(seg_idx) = conn.current_segment {
                                *speed_map.entry(seg_idx).or_insert(0.0) += conn.speed;
                            }
                        }
                        (0..info.segments.len()).map(|i| speed_map.get(&i).copied().unwrap_or(0.0)).collect()
                    } else {
                        vec![0.0; info.segments.len()]
                    }
                };

                // 计算总速度（所有 segment 速度之和）
                let total_speed: f64 = segment_speeds.iter().sum();

                let endpoints: Vec<String> = info.endpoints.iter().map(|ep| {
                    format!(
                        r#"{{"id": {}, "ip": "{}", "enabled": {}, "proxy": {}}}"#,
                        ep.id,
                        ep.ip,
                        ep.enabled,
                        ep.proxy.as_ref().map(|p| format!("\"{}\"", p)).unwrap_or("null".to_string())
                    )
                }).collect();

                let segments: Vec<String> = info.segments.iter().enumerate().map(|(i, seg)| {
                    let speed = segment_speeds.get(i).copied().unwrap_or(0.0);
                    format!(
                        r#"{{"start": {}, "end": {}, "downloaded": {}, "completed": {}, "endpoint_id": {}, "speed": {:.1}}}"#,
                        seg.start,
                        seg.end as i64,
                        seg.downloaded,
                        seg.completed,
                        seg.endpoint_id.map(|id| id.to_string()).unwrap_or("null".to_string()),
                        speed
                    )
                }).collect();

                println!(
                    r#"{{"status": "ok", "download": {{"id": "{}", "url": "{}", "actual_url": "{}", "output": "{}", "file_size": {}, "downloaded": {}, "progress": {:.1}, "status": "{}", "speed": {:.1}, "connections": {}, "endpoints": [{}], "segments": [{}]}}}}"#,
                    info.id, info.url, info.actual_url, info.output, info.file_size, info.downloaded,
                    info.progress(), info.status, total_speed, info.connections, endpoints.join(", "), segments.join(", ")
                );
            } else {
                println!("Download: {}", info.id);
                println!("URL: {}", info.url);
                if !info.actual_url.is_empty() {
                    println!("Actual URL: {}", info.actual_url);
                }
                println!("Output: {}", info.output);
                println!("Status: {}", info.status);
                println!("File Size: {}", format_bytes(info.file_size));
                println!("Downloaded: {}", format_bytes(info.downloaded));
                println!("Progress: {:.1}%", info.progress());
                println!("Connections: {}", info.connections);
                println!("Created: {}", info.created_at);

                println!("\nEndpoints:");
                println!("{:<5} {:<20} {:<10} {:<15}", "ID", "IP", "Enabled", "Proxy");
                println!("{:-<50}", "");
                for ep in &info.endpoints {
                    let enabled = if ep.enabled { "✓" } else { "✗" };
                    let proxy = ep.proxy.as_deref().unwrap_or("-");
                    println!("{:<5} {:<20} {:<10} {:<15}", ep.id, ep.ip, enabled, proxy);
                }

                if !info.segments.is_empty() {
                    println!("\nSegments:");
                    println!("{:<8} {:<15} {:<15} {:<12} {:<10} {:<10}", "ID", "Start", "End", "Downloaded", "Status", "Endpoint");
                    println!("{:-<70}", "");
                    for (i, seg) in info.segments.iter().enumerate() {
                        let status = if seg.completed { "✓ Done" } else { "⏳ Pending" };
                        let endpoint = seg.endpoint_id.map(|id| id.to_string()).unwrap_or("-".to_string());
                        println!("{:<8} {:<15} {:<15} {:<12} {:<10} {:<10}",
                            i,
                            format_bytes(seg.start),
                            format_bytes(seg.end),
                            format_bytes(seg.downloaded),
                            status,
                            endpoint
                        );
                    }
                }
            }
        }
        None => {
            if config.json_mode {
                println!(r#"{{"status": "error", "message": "Download not found"}}"#);
            } else {
                eprintln!("Error: Download '{}' not found", id);
            }
        }
    }
}

/// 监控下载（TUI）
async fn cmd_monitor(id: &str, config: &CliConfig) {
    if config.json_mode {
        println!(r#"{{"status": "error", "message": "Monitor not supported in JSON mode"}}"#);
        return;
    }

    // 查找下载
    let (_, mut info) = match state::find_download_by_id(id) {
        Some(info) => info,
        None => {
            eprintln!("Error: Download '{}' not found", id);
            return;
        }
    };

    // 检查下载管理器中是否已有该下载
    let manager = super::manager::DOWNLOAD_MANAGER.lock().await;
    let existing_handle = manager.get(id);
    drop(manager);

    let handle_arc = if let Some(handle_arc) = existing_handle {
        // 下载已在运行，直接使用
        println!("Monitoring existing download...");
        handle_arc
    } else {
        // 下载未在运行，需要启动新的下载
        // 使用实际 URL（如果有重定向）
        let resume_url = if !info.actual_url.is_empty() {
            &info.actual_url
        } else {
            &info.url
        };

        let mut remote_file = RemoteFile::new(resume_url);

        // 添加自定义 headers 和代理配置
        let global_config = crate::config::get_global_config().await;
        for (name, value) in &global_config.headers {
            remote_file.request_headers.insert(name.clone(), value.clone());
        }
        // 添加代理配置
        for (key, proxy_url) in &global_config.proxy {
            remote_file.proxy_map.insert(key.clone(), proxy_url.clone());
        }
        drop(global_config);

        // 恢复保存的响应头（含 accept-ranges，用于检测 Range 支持）
        remote_file.update_response_headers(info.response_headers.clone());

        let mut endpoint_list = EndpointList::new(resume_url);
        for ep in &info.endpoints {
            if ep.enabled {
                endpoint_list.add_endpoint_with_id(ep.ip, ep.id);
            }
        }

        if endpoint_list.endpoints.is_empty() {
            eprintln!("Error: No enabled endpoints");
            return;
        }


        match crate::downloader::start_download(
            info.file_size,
            &info.output,
            &remote_file,
            &endpoint_list,
            id,
            if info.segments.is_empty() { None } else { Some(info.segments.clone()) },
            if info.connections > 0 { Some(info.connections) } else { None },
            Some(info.endpoints.len()),
            &info.url,
            &remote_file.url,
        ).await {
            Ok(handle) => {
                // 更新状态为下载中，并从 handle 中读取实际的连接数
                if let Some((_, mut download_info)) = state::find_download_by_id(id) {
                    download_info.status = DownloadStatus::Downloading;
                    let actual_connections = {
                        let state = handle.state.lock().await;
                        state.connections.len()
                    };
                    download_info.connections = actual_connections;
                    let _ = state::save_download(&download_info);
                }

                // 注册到管理器
                let mut manager = super::manager::DOWNLOAD_MANAGER.lock().await;
                manager.register(id.to_string(), handle);
                let handle_arc = manager.get(id).unwrap();
                drop(manager);

                // 启动后台任务监控下载完成状态
                let download_id = id.to_string();
                tokio::spawn(async move {
                    loop {
                        tokio::time::sleep(std::time::Duration::from_secs(1)).await;

                        let manager = super::manager::DOWNLOAD_MANAGER.lock().await;
                        if let Some(handle_arc) = manager.get(&download_id) {
                            let handle = handle_arc.lock().await;
                            let progress = handle.get_progress().await;
                            // 获取当前 segment 进度
                            let current_segments = {
                                let state = handle.state.lock().await;
                                state.get_segment_progress()
                            };
                            drop(handle);
                            drop(manager);

                            // 更新进度到文件（保留 segments）
                            if let Some((_, mut download_info)) = state::find_download_by_id(&download_id) {
                                download_info.downloaded = progress.downloaded_bytes;
                                // 更新 segments
                                download_info.segments = current_segments.iter().map(|seg| {
                                    super::state::SegmentProgress {
                                        start: seg.start,
                                        end: seg.end,
                                        downloaded: seg.downloaded,
                                        completed: seg.downloaded >= (seg.end - seg.start),
                                        endpoint_id: seg.endpoint_id,
                                    }
                                }).collect();
                                // 同步 connections 为实际分片数（确保与 segments 一致）
                                download_info.connections = download_info.segments.len();
                                let _ = state::save_download(&download_info);
                            }

                            // 检查是否完成
                            if progress.percentage >= 100.0 {
                                // 标记为完成：所有字段置 100% 再删除 .rdown.json
                                if let Some((path, mut download_info)) = state::find_download_by_id(&download_id) {
                                    download_info.status = DownloadStatus::Completed;
                                    download_info.downloaded = download_info.file_size;
                                    let _ = state::save_download(&download_info);
                                    let _ = std::fs::remove_file(&path);
                                }

                                println!("Download completed: {}", download_id);
                                let mut manager = super::manager::DOWNLOAD_MANAGER.lock().await;
                                manager.remove(&download_id);
                                break;
                            }
                        } else {
                            break;
                        }
                    }
                });

                println!("Download started. Monitoring progress...");
                handle_arc
            }
            Err(e) => {
                eprintln!("Download error: {}", e);
                return;
            }
        }
    };

    println!("Press 'q' to stop monitoring (download continues in background)\n");

    // 启用 crossterm 的 raw mode 来读取单个按键
    use crossterm::terminal::{enable_raw_mode, disable_raw_mode};
    use crossterm::event::{self, Event, KeyCode, poll};
    use std::time::Duration as StdDuration;

    enable_raw_mode().expect("Failed to enable raw mode");

    // 显示进度循环
    loop {
        // 检查是否有按键事件（非阻塞）
        if poll(StdDuration::from_millis(0)).unwrap_or(false) {
            if let Ok(Event::Key(key_event)) = event::read() {
                if key_event.code == KeyCode::Char('q') || key_event.code == KeyCode::Char('Q') {
                    break;
                }
            }
        }
        let handle_guard = handle_arc.lock().await;
        let progress = handle_guard.get_progress().await;
        let endpoints = handle_guard.get_endpoints().await;
        drop(handle_guard);

        // 清屏并显示进度
        // 计算总速度
        let total_speed: f64 = endpoints.iter().map(|ep| ep.speed).sum();

        print!("\x1B[2J\x1B[H"); // Clear screen
        println!("=== Download Monitor: {} ===", id);
        println!("URL: {}", info.url);
        println!("Output: {}", info.output);
        println!();
        println!("Progress: {:.1}% ({}/{})",
            progress.percentage,
            format_bytes(progress.downloaded_bytes),
            format_bytes(progress.total_bytes)
        );

        // 进度条
        let bar_width = 50;
        let filled = (progress.percentage / 100.0 * bar_width as f64) as usize;
        let bar: String = "█".repeat(filled) + &"░".repeat(bar_width - filled);
        println!("[{}]", bar);
        println!();

        // 总速度
        let speed_str = if total_speed > 0.0 {
            format!("{:.2} MB/s", total_speed / 1_048_576.0)
        } else {
            "-".to_string()
        };
        println!("Total Speed: {}", speed_str);
        println!();

        // Endpoint 状态
        println!("Endpoints:");
        for ep in &endpoints {
            let status = if ep.enabled { "✓" } else { "✗" };
            let speed = if ep.speed > 0.0 {
                format!("{:.2} MB/s", ep.speed / 1_048_576.0)
            } else {
                "-".to_string()
            };

            // 获取该 endpoint 的错误信息
            let error_msg = {
                let handle_guard = handle_arc.lock().await;
                let state = handle_guard.state.lock().await;
                state.connections.iter()
                    .find(|c| c.endpoint == ep.ip && c.last_error.is_some())
                    .and_then(|c| c.last_error.clone())
            };

            if let Some(err) = error_msg {
                println!("  {} {} - Speed: {}, Downloaded: {} - Error: {}",
                    status, ep.ip, speed, format_bytes(ep.downloaded), err);
            } else {
                println!("  {} {} - Speed: {}, Downloaded: {}",
                    status, ep.ip, speed, format_bytes(ep.downloaded));
            }
        }

        // 显示线程进度（按速度排序，只显示活跃线程，最多 8 个）
        {
            let handle_guard = handle_arc.lock().await;
            let state = handle_guard.state.lock().await;

            // 获取活跃连接（有分配 segment 且正在下载的）
            let mut active_connections: Vec<_> = state.connections.iter()
                .filter(|c| c.current_segment.is_some())
                .collect();

            // 按速度排序（降序）
            active_connections.sort_by(|a, b| b.speed.partial_cmp(&a.speed).unwrap_or(std::cmp::Ordering::Equal));

            // 只显示前 8 个
            let display_count = active_connections.len().min(8);

            if display_count > 0 {
                println!("\nActive Threads (top {} by speed):", display_count);
                println!("{:<6} {:<20} {:<12} {:<15} {:<8}", "Thread", "Endpoint", "Speed", "Downloaded", "Progress");
                println!("{:-<61}", "");

                for conn in active_connections.iter().take(display_count) {
                    let segment_idx = conn.current_segment.unwrap();
                    let segment = state.segments.get(segment_idx).unwrap();
                    let segment_size = segment.end - segment.start;
                    let progress_pct = if segment_size > 0 {
                        (segment.downloaded as f64 / segment_size as f64) * 100.0
                    } else {
                        0.0
                    };

                    let speed_str = if conn.speed > 0.0 {
                        format!("{:.2} MB/s", conn.speed / 1_048_576.0)
                    } else {
                        "-".to_string()
                    };

                    // 简洁进度条
                    let bar_width = 10;
                    let filled = (progress_pct / 100.0 * bar_width as f64) as usize;
                    let bar: String = "█".repeat(filled) + &"░".repeat(bar_width - filled);

                    println!("{:<6} {:<20} {:<12} {:<15} [{:<10}] {:.1}%",
                        conn.id,
                        conn.endpoint,
                        speed_str,
                        format_bytes(segment.downloaded),
                        bar,
                        progress_pct
                    );
                }
            }
            drop(state);
            drop(handle_guard);
        }

        println!();
        println!("Press 'q' to stop monitoring");

        tokio::time::sleep(std::time::Duration::from_secs(1)).await;

        // 检查是否完成
        if progress.percentage >= 100.0 {
            disable_raw_mode().expect("Failed to disable raw mode");
            println!("\nDownload completed!");
            return;
        }
    }

    disable_raw_mode().expect("Failed to disable raw mode");
    println!("\nMonitoring stopped. Download continues in background.");
}

/// 暂停下载
fn cmd_pause(id: &str, config: &CliConfig) {
    match state::find_download_by_id(id) {
        Some((path, mut info)) => {
            if info.status == DownloadStatus::Downloading || info.status == DownloadStatus::Pending {
                info.status = DownloadStatus::Paused;
                match state::save_download(&info) {
                    Ok(()) => {
                        if config.json_mode {
                            println!(r#"{{"status": "ok", "message": "Download paused"}}"#);
                        } else {
                            println!("Download {} paused", id);
                        }
                    }
                    Err(e) => {
                        if config.json_mode {
                            println!(r#"{{"status": "error", "message": "{}"}}"#, e);
                        } else {
                            eprintln!("Error: {}", e);
                        }
                    }
                }
            } else {
                if config.json_mode {
                    println!(r#"{{"status": "error", "message": "Cannot pause download in status: {}"}}"#, info.status);
                } else {
                    eprintln!("Error: Cannot pause download in status: {}", info.status);
                }
            }
        }
        None => {
            if config.json_mode {
                println!(r#"{{"status": "error", "message": "Download not found"}}"#);
            } else {
                eprintln!("Error: Download '{}' not found", id);
            }
        }
    }
}

/// 暂停所有下载
fn cmd_pause_all(config: &CliConfig) {
    let downloads = state::load_all_downloads();
    let mut paused_count = 0;

    for mut info in downloads {
        if info.status == DownloadStatus::Downloading || info.status == DownloadStatus::Pending {
            info.status = DownloadStatus::Paused;
            if state::save_download(&info).is_ok() {
                paused_count += 1;
            }
        }
    }

    if !config.json_mode && paused_count > 0 {
        println!("Paused {} download(s)", paused_count);
    }
}

/// 恢复下载
async fn cmd_resume(id: &str, config: &CliConfig) {
    match state::find_download_by_id(id) {
        Some((path, mut info)) => {
            if info.status == DownloadStatus::Paused || info.status == DownloadStatus::Pending {
                // 使用实际 URL（如果有重定向）
                let resume_url = if !info.actual_url.is_empty() {
                    info.actual_url.clone()
                } else {
                    info.url.clone()
                };

                // 创建 RemoteFile，使用保存的 endpoint 信息
                let mut remote_file = crate::remote_file::RemoteFile::new(&resume_url);

                // 添加自定义 headers 和代理配置
                let global_config = crate::config::get_global_config().await;
                for (name, value) in &global_config.headers {
                    remote_file.request_headers.insert(name.clone(), value.clone());
                }
                // 添加代理配置
                for (key, proxy_url) in &global_config.proxy {
                    remote_file.proxy_map.insert(key.clone(), proxy_url.clone());
                }
                drop(global_config);

                // 恢复保存的响应头（含 accept-ranges，用于检测 Range 支持）
                remote_file.update_response_headers(info.response_headers.clone());

                // 使用保存的 endpoint 信息，而不是重新解析
                let mut endpoint_list = crate::endpoint_test::EndpointList::new(&resume_url);
                for ep in &info.endpoints {
                    if ep.enabled {
                        endpoint_list.add_endpoint_with_id(ep.ip, ep.id);
                    }
                }

                // 如果没有保存的 endpoint 信息，则重新获取
                if endpoint_list.endpoints.is_empty() {
                    match remote_file.get_information().await {
                        Ok(()) => {
                            if let Some(list) = &remote_file.endpoint_list {
                                endpoint_list = list.clone();
                            }
                        }
                        Err(e) => {
                            if config.json_mode {
                                println!(r#"{{"status": "error", "message": "{}"}}"#, e);
                            } else {
                                eprintln!("Error: {}", e);
                            }
                            return;
                        }
                    }

                    // 重新探测后仍全部不可用 → 标记失败
                    if endpoint_list.available().is_empty() {
                        if let Some((_, mut di)) = state::find_download_by_id(id) {
                            di.status = DownloadStatus::Failed;
                            let _ = state::save_download(&di);
                        }
                        if config.json_mode {
                            println!(r#"{{"status": "error", "id": "{}", "message": "All endpoints are unavailable"}}"#, id);
                        } else {
                            eprintln!("Error: All endpoints are unavailable");
                        }
                        return;
                    }
                }

                // 更新状态为下载中
                // 无 Content-Length 时不能断点续传，重置 downloaded 为 0
                // （handle.rs 中也会重置，但此处确保 JSON 文件立即可见正确值）
                if info.file_size == 0 {
                    info.downloaded = 0;
                    for seg in &mut info.segments {
                        seg.downloaded = 0;
                    }
                }
                info.status = DownloadStatus::Downloading;
                if let Err(e) = state::save_download(&info) {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "{}"}}"#, e);
                    } else {
                        eprintln!("Error: {}", e);
                    }
                    return;
                }

                // 启动下载（传入已保存的分片信息用于断点续传）
                match crate::downloader::start_download(
                    info.file_size,
                    &info.output,
                    &remote_file,
                    &endpoint_list,
                    &info.id,
                    if info.segments.is_empty() { None } else { Some(info.segments.clone()) },
                    if info.connections > 0 { Some(info.connections) } else { None },
                    Some(info.endpoints.len()),
                    &info.url,
                    &remote_file.url,
                ).await {
                    Ok(handle) => {
                        // 注册到下载管理器
                        let mut manager = super::manager::DOWNLOAD_MANAGER.lock().await;
                        manager.register(info.id.clone(), handle);
                        drop(manager);

                        // 启动后台任务监控下载完成状态
                        let download_id = info.id.clone();
                        let json_mode = config.json_mode;
                        tokio::spawn(async move {
                            loop {
                                tokio::time::sleep(std::time::Duration::from_secs(1)).await;

                                let manager = super::manager::DOWNLOAD_MANAGER.lock().await;
                                if let Some(handle_arc) = manager.get(&download_id) {
                                    let handle = handle_arc.lock().await;
                                    let progress = handle.get_progress().await;
                                    // 获取当前 segment 进度 + completed 标志
                                    let (current_segments, stream_completed) = {
                                        let state = handle.state.lock().await;
                                        (state.get_segment_progress(), state.completed.load(std::sync::atomic::Ordering::Relaxed))
                                    };
                                    drop(handle);
                                    drop(manager);

                                    // 更新进度到文件（保留 segments）
                                    if let Some((_, mut download_info)) = state::find_download_by_id(&download_id) {
                                        download_info.downloaded = progress.downloaded_bytes;
                                        // 更新 segments
                                        download_info.segments = current_segments.iter().map(|seg| {
                                            super::state::SegmentProgress {
                                                start: seg.start,
                                                end: seg.end,
                                                downloaded: seg.downloaded,
                                                completed: seg.downloaded >= (seg.end - seg.start),
                                                endpoint_id: seg.endpoint_id,
                                            }
                                        }).collect();
                                        // 同步 connections 为实际分片数
                                        download_info.connections = download_info.segments.len();
                                        let _ = state::save_download(&download_info);
                                    }

                                    // 检查是否完成
                                    // 对于未知大小的下载（total_bytes=0），通过 completed 标志判断
                                    if progress.percentage >= 100.0 || (progress.total_bytes == 0 && stream_completed) {
                                        // 标记为完成：所有字段置 100% 再删除 .rdown.json
                                        if let Some((path, mut download_info)) = state::find_download_by_id(&download_id) {
                                            download_info.status = DownloadStatus::Completed;
                                            if download_info.file_size == 0 {
                                                download_info.file_size = progress.downloaded_bytes;
                                            }
                                            download_info.downloaded = download_info.file_size;
                                            let _ = state::save_download(&download_info);
                                            let _ = std::fs::remove_file(&path);
                                        }

                                        if json_mode {
                                            println!(r#"{{"status": "ok", "id": "{}", "message": "Download completed"}}"#, download_id);
                                        } else {
                                            println!("Download completed: {}", download_id);
                                        }
                                        let mut manager = super::manager::DOWNLOAD_MANAGER.lock().await;
                                        manager.remove(&download_id);
                                        break;
                                    }

                                    // 检查是否被取消（所有 endpoint 都被禁用）
                                    let is_cancelled = {
                                        let mgr = super::manager::DOWNLOAD_MANAGER.lock().await;
                                        if let Some(h) = mgr.get(&download_id) {
                                            let handle = h.lock().await;
                                            let s = handle.state.lock().await;
                                            s.cancelled.load(std::sync::atomic::Ordering::Relaxed)
                                        } else {
                                            false
                                        }
                                    };

                                    if is_cancelled {
                                        // 标记为失败
                                        if let Some((_, mut di)) = state::find_download_by_id(&download_id) {
                                            di.status = DownloadStatus::Failed;
                                            let _ = state::save_download(&di);
                                        }
                                        if json_mode {
                                            println!(r#"{{"status": "error", "id": "{}", "message": "All endpoints disabled"}}"#, download_id);
                                        } else {
                                            println!("Download failed: {} (all endpoints disabled)", download_id);
                                        }
                                        let mut mgr = super::manager::DOWNLOAD_MANAGER.lock().await;
                                        mgr.remove(&download_id);
                                        break;
                                    }
                                } else {
                                    break;
                                }
                            }
                        });

                        if config.json_mode {
                            println!(r#"{{"status": "ok", "message": "Download resumed"}}"#);
                        } else {
                            println!("Download {} resumed", info.id);
                        }
                    }
                    Err(e) => {
                        if let Some((_, mut download_info)) = state::find_download_by_id(&info.id) {
                            download_info.status = DownloadStatus::Failed;
                            let _ = state::save_download(&download_info);
                        }
                        if config.json_mode {
                            println!(r#"{{"status": "error", "message": "{}"}}"#, e);
                        } else {
                            eprintln!("Download error: {}", e);
                        }
                    }
                }
            } else {
                if config.json_mode {
                    println!(r#"{{"status": "error", "message": "Cannot resume download in status: {}"}}"#, info.status);
                } else {
                    eprintln!("Error: Cannot resume download in status: {}", info.status);
                }
            }
        }
        None => {
            if config.json_mode {
                println!(r#"{{"status": "error", "message": "Download not found"}}"#);
            } else {
                eprintln!("Error: Download '{}' not found", id);
            }
        }
    }
}

/// 取消下载（先暂停，再删除）
async fn cmd_cancel(id: &str, config: &CliConfig) {
    match state::find_download_by_id(id) {
        Some((path, _info)) => {
            // 先暂停下载（如果正在运行）
            {
                let mut manager = super::manager::DOWNLOAD_MANAGER.lock().await;
                if let Some(handle_arc) = manager.get(id) {
                    let handle = handle_arc.lock().await;
                    handle.pause().await;
                    drop(handle);
                }
                // 从下载管理器中移除
                manager.remove(id);
            }

            // 等待一小段时间让下载任务停止
            tokio::time::sleep(std::time::Duration::from_millis(500)).await;

            // 删除 .rdown.json 文件
            match std::fs::remove_file(&path) {
                Ok(()) => {
                    if config.json_mode {
                        println!(r#"{{"status": "ok", "message": "Download cancelled and removed"}}"#);
                    } else {
                        println!("Download {} cancelled and removed", id);
                    }
                }
                Err(e) => {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "{}"}}"#, e);
                    } else {
                        eprintln!("Error: {}", e);
                    }
                }
            }
        }
        None => {
            if config.json_mode {
                println!(r#"{{"status": "error", "message": "Download not found"}}"#);
            } else {
                eprintln!("Error: Download '{}' not found", id);
            }
        }
    }
}

/// 添加 endpoint
async fn cmd_add_endpoint(id: &str, ip: &str, proxy: Option<&str>, config: &CliConfig) {
    let ip_addr: std::net::IpAddr = match ip.parse() {
        Ok(ip) => ip,
        Err(_) => {
            if config.json_mode {
                println!(r#"{{"status": "error", "message": "Invalid IP address"}}"#);
            } else {
                eprintln!("Error: Invalid IP address: {}", ip);
            }
            return;
        }
    };

    match state::find_download_by_id(id) {
        Some((_, mut info)) => {
            // 检查是否已存在
            if info.endpoints.iter().any(|ep| ep.ip == ip_addr) {
                if config.json_mode {
                    println!(r#"{{"status": "error", "message": "Endpoint already exists"}}"#);
                } else {
                    eprintln!("Error: Endpoint already exists");
                }
                return;
            }

            // 生成新的 endpoint ID
            let endpoint_id = info.endpoints.iter().map(|ep| ep.id).max().unwrap_or(0) + 1;

            info.endpoints.push(EndpointInfo {
                id: endpoint_id,
                ip: ip_addr,
                enabled: true,
                proxy: proxy.map(|p| p.to_string()),
            });

            match state::save_download(&info) {
                Ok(()) => {
                    // 传播到正在运行的下载句柄（如果存在）
                    let manager = super::manager::DOWNLOAD_MANAGER.lock().await;
                    if let Some(handle_arc) = manager.get(id) {
                        let handle = handle_arc.lock().await;
                        // 使用简化的添加方法（不进行速度测试，连接将自然积累速度数据）
                        handle.add_endpoint_no_test(ip_addr, endpoint_id).await;
                    }
                    drop(manager);

                    if config.json_mode {
                        println!(r#"{{"status": "ok", "message": "Endpoint added"}}"#);
                    } else {
                        println!("Endpoint {} added to download {}", ip, id);
                    }
                }
                Err(e) => {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "{}"}}"#, e);
                    } else {
                        eprintln!("Error: {}", e);
                    }
                }
            }
        }
        None => {
            if config.json_mode {
                println!(r#"{{"status": "error", "message": "Download not found"}}"#);
            } else {
                eprintln!("Error: Download '{}' not found", id);
            }
        }
    }
}

/// 禁用 endpoint
async fn cmd_disable_endpoint(id: &str, ip: &str, config: &CliConfig) {
    let ip_addr: std::net::IpAddr = match ip.parse() {
        Ok(ip) => ip,
        Err(_) => {
            if config.json_mode {
                println!(r#"{{"status": "error", "message": "Invalid IP address"}}"#);
            } else {
                eprintln!("Error: Invalid IP address: {}", ip);
            }
            return;
        }
    };

    match state::find_download_by_id(id) {
        Some((_, mut info)) => {
            if let Some(ep) = info.endpoints.iter_mut().find(|ep| ep.ip == ip_addr) {
                ep.enabled = false;
                match state::save_download(&info) {
                    Ok(()) => {
                        // 传播到正在运行的下载句柄
                        let manager = super::manager::DOWNLOAD_MANAGER.lock().await;
                        if let Some(handle_arc) = manager.get(id) {
                            let handle = handle_arc.lock().await;
                            handle.disable_endpoint(ip_addr).await;
                        }
                        drop(manager);

                        if config.json_mode {
                            println!(r#"{{"status": "ok", "message": "Endpoint disabled"}}"#);
                        } else {
                            println!("Endpoint {} disabled", ip);
                        }
                    }
                    Err(e) => {
                        if config.json_mode {
                            println!(r#"{{"status": "error", "message": "{}"}}"#, e);
                        } else {
                            eprintln!("Error: {}", e);
                        }
                    }
                }
            } else {
                if config.json_mode {
                    println!(r#"{{"status": "error", "message": "Endpoint not found"}}"#);
                } else {
                    eprintln!("Error: Endpoint not found");
                }
            }
        }
        None => {
            if config.json_mode {
                println!(r#"{{"status": "error", "message": "Download not found"}}"#);
            } else {
                eprintln!("Error: Download '{}' not found", id);
            }
        }
    }
}

/// 启用 endpoint
async fn cmd_enable_endpoint(id: &str, ip: &str, config: &CliConfig) {
    let ip_addr: std::net::IpAddr = match ip.parse() {
        Ok(ip) => ip,
        Err(_) => {
            if config.json_mode {
                println!(r#"{{"status": "error", "message": "Invalid IP address"}}"#);
            } else {
                eprintln!("Error: Invalid IP address: {}", ip);
            }
            return;
        }
    };

    match state::find_download_by_id(id) {
        Some((_, mut info)) => {
            if let Some(ep) = info.endpoints.iter_mut().find(|ep| ep.ip == ip_addr) {
                ep.enabled = true;
                let endpoint_id = ep.id;  // 先取出 id，避免借用冲突
                match state::save_download(&info) {
                    Ok(()) => {
                        // 传播到正在运行的下载句柄
                        let manager = super::manager::DOWNLOAD_MANAGER.lock().await;
                        if let Some(handle_arc) = manager.get(id) {
                            let handle = handle_arc.lock().await;
                            handle.enable_endpoint(ip_addr, Some(endpoint_id)).await;
                        }
                        drop(manager);

                        if config.json_mode {
                            println!(r#"{{"status": "ok", "message": "Endpoint enabled"}}"#);
                        } else {
                            println!("Endpoint {} enabled", ip);
                        }
                    }
                    Err(e) => {
                        if config.json_mode {
                            println!(r#"{{"status": "error", "message": "{}"}}"#, e);
                        } else {
                            eprintln!("Error: {}", e);
                        }
                    }
                }
            } else {
                if config.json_mode {
                    println!(r#"{{"status": "error", "message": "Endpoint not found"}}"#);
                } else {
                    eprintln!("Error: Endpoint not found");
                }
            }
        }
        None => {
            if config.json_mode {
                println!(r#"{{"status": "error", "message": "Download not found"}}"#);
            } else {
                eprintln!("Error: Download '{}' not found", id);
            }
        }
    }
}

/// 设置配置
async fn cmd_set_config(key: &str, value: &str, config: &CliConfig) {
    // 如果没有配置文件，set-config 只在内存中生效
    // 如果有配置文件，会同时保存到文件

    let mut global_config = crate::config::get_global_config().await;

    match key {
        "ip-protocol" | "ip_protocol" => {
            match value.to_lowercase().as_str() {
                "ipv4" | "onlyipv4" | "only_ipv4" => {
                    global_config.ip_protocol = crate::config::IpProtocol::OnlyIPv4;
                }
                "ipv6" | "onlyipv6" | "only_ipv6" => {
                    global_config.ip_protocol = crate::config::IpProtocol::OnlyIPv6;
                }
                "both" => {
                    global_config.ip_protocol = crate::config::IpProtocol::Both;
                }
                _ => {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "Invalid IP protocol. Use: ipv4, ipv6, both"}}"#);
                    } else {
                        eprintln!("Error: Invalid IP protocol '{}'. Use: ipv4, ipv6, both", value);
                    }
                    return;
                }
            }
        }
        "connections" | "total_connections" => {
            match value.parse::<usize>() {
                Ok(n) if n > 0 => {
                    global_config.total_connections = n;
                }
                _ => {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "Invalid connections value"}}"#);
                    } else {
                        eprintln!("Error: Invalid connections value '{}'. Must be a positive integer", value);
                    }
                    return;
                }
            }
        }
        "retry" | "retry_count" => {
            match value.parse::<u32>() {
                Ok(n) => {
                    global_config.retry_count = n;
                }
                _ => {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "Invalid retry count"}}"#);
                    } else {
                        eprintln!("Error: Invalid retry count '{}'. Must be a non-negative integer", value);
                    }
                    return;
                }
            }
        }
        "chunk-size" | "chunk_size" => {
            // 支持 KB, MB, GB 后缀
            let chunk_size = if value.to_uppercase().ends_with("KB") {
                value[..value.len()-2].trim().parse::<usize>().ok().map(|n| n * 1024)
            } else if value.to_uppercase().ends_with("MB") {
                value[..value.len()-2].trim().parse::<usize>().ok().map(|n| n * 1024 * 1024)
            } else if value.to_uppercase().ends_with("GB") {
                value[..value.len()-2].trim().parse::<usize>().ok().map(|n| n * 1024 * 1024 * 1024)
            } else {
                value.parse::<usize>().ok()
            };

            match chunk_size {
                Some(n) if n > 0 => {
                    global_config.chunk_size = n;
                }
                _ => {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "Invalid chunk size"}}"#);
                    } else {
                        eprintln!("Error: Invalid chunk size '{}'. Use: 64KB, 1MB, etc.", value);
                    }
                    return;
                }
            }
        }
        "speed-threshold" | "speed_threshold" => {
            match value.parse::<f64>() {
                Ok(n) if n > 0.0 => {
                    global_config.speed_threshold = n;
                }
                _ => {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "Invalid speed threshold"}}"#);
                    } else {
                        eprintln!("Error: Invalid speed threshold '{}'. Must be a positive number", value);
                    }
                    return;
                }
            }
        }
        "download-timeout" | "download_timeout" => {
            // 支持 s, m, h 后缀
            let timeout_secs = if value.to_lowercase().ends_with('s') {
                value[..value.len()-1].trim().parse::<u64>().ok()
            } else if value.to_lowercase().ends_with('m') {
                value[..value.len()-1].trim().parse::<u64>().ok().map(|n| n * 60)
            } else if value.to_lowercase().ends_with('h') {
                value[..value.len()-1].trim().parse::<u64>().ok().map(|n| n * 3600)
            } else {
                value.parse::<u64>().ok()
            };

            match timeout_secs {
                Some(s) if s > 0 => {
                    global_config.download_timeout_secs = s;
                }
                _ => {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "Invalid timeout"}}"#);
                    } else {
                        eprintln!("Error: Invalid timeout '{}'. Use: 30s, 5m, 1h, etc.", value);
                    }
                    return;
                }
            }
        }
        "endpoint-failure-threshold" | "endpoint_failure_threshold" => {
            match value.parse::<u32>() {
                Ok(n) if n > 0 => {
                    global_config.endpoint_failure_threshold = n;
                }
                _ => {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "Invalid endpoint failure threshold"}}"#);
                    } else {
                        eprintln!("Error: Invalid endpoint failure threshold '{}'. Must be a positive integer", value);
                    }
                    return;
                }
            }
        }
        "retry-reconnect-count" | "retry_reconnect_count" => {
            match value.parse::<u32>() {
                Ok(n) => {
                    global_config.retry_reconnect_count = n;
                }
                _ => {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "Invalid retry reconnect count"}}"#);
                    } else {
                        eprintln!("Error: Invalid retry reconnect count '{}'. Must be a non-negative integer", value);
                    }
                    return;
                }
            }
        }
        "retry-refetch-endpoint-count" | "retry_refetch_endpoint_count" => {
            match value.parse::<u32>() {
                Ok(n) => {
                    global_config.retry_refetch_endpoint_count = n;
                }
                _ => {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "Invalid retry refetch endpoint count"}}"#);
                    } else {
                        eprintln!("Error: Invalid retry refetch endpoint count '{}'. Must be a non-negative integer", value);
                    }
                    return;
                }
            }
        }
        "retry-refetch-url-count" | "retry_refetch_url_count" => {
            match value.parse::<u32>() {
                Ok(n) => {
                    global_config.retry_refetch_url_count = n;
                }
                _ => {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "Invalid retry refetch URL count"}}"#);
                    } else {
                        eprintln!("Error: Invalid retry refetch URL count '{}'. Must be a non-negative integer", value);
                    }
                    return;
                }
            }
        }
        "speed-test-retry" | "speed_test_retry" => {
            match value.parse::<u32>() {
                Ok(n) => {
                    global_config.speed_test_retry_count = n;
                }
                _ => {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "Invalid speed test retry count"}}"#);
                    } else {
                        eprintln!("Error: Invalid speed test retry count '{}'. Must be a non-negative integer", value);
                    }
                    return;
                }
            }
        }
        "speed-test-timeout" | "speed_test_timeout" => {
            match value.parse::<u64>() {
                Ok(s) if s > 0 => {
                    global_config.speed_test_timeout_secs = s;
                }
                _ => {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "Invalid speed test timeout"}}"#);
                    } else {
                        eprintln!("Error: Invalid speed test timeout '{}'. Must be a positive integer (seconds)", value);
                    }
                    return;
                }
            }
        }
        "connectivity-timeout" | "connectivity_timeout" => {
            match value.parse::<u64>() {
                Ok(s) if s > 0 => {
                    global_config.connectivity_timeout_secs = s;
                }
                _ => {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "Invalid connectivity timeout"}}"#);
                    } else {
                        eprintln!("Error: Invalid connectivity timeout '{}'. Must be a positive integer (seconds)", value);
                    }
                    return;
                }
            }
        }
        "dns-timeout" | "dns_timeout" => {
            match value.parse::<u64>() {
                Ok(s) if s > 0 => {
                    global_config.dns_timeout_secs = s;
                }
                _ => {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "Invalid DNS timeout"}}"#);
                    } else {
                        eprintln!("Error: Invalid DNS timeout '{}'. Must be a positive integer (seconds)", value);
                    }
                    return;
                }
            }
        }
        "dns-retry" | "dns_retry" => {
            match value.parse::<u32>() {
                Ok(n) => {
                    global_config.dns_retry_count = n;
                }
                _ => {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "Invalid DNS retry count"}}"#);
                    } else {
                        eprintln!("Error: Invalid DNS retry count '{}'. Must be a non-negative integer", value);
                    }
                    return;
                }
            }
        }
        "scheduler-interval" | "scheduler_interval" => {
            match value.parse::<u64>() {
                Ok(s) if s > 0 => {
                    global_config.scheduler_check_interval_secs = s;
                }
                _ => {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "Invalid scheduler interval"}}"#);
                    } else {
                        eprintln!("Error: Invalid scheduler interval '{}'. Must be a positive integer (seconds)", value);
                    }
                    return;
                }
            }
        }
        "scheduler-slow-threshold" | "scheduler_slow_threshold" => {
            match value.parse::<f64>() {
                Ok(n) if n > 0.0 && n <= 1.0 => {
                    global_config.scheduler_slow_threshold = n;
                }
                _ => {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "Invalid scheduler slow threshold"}}"#);
                    } else {
                        eprintln!("Error: Invalid scheduler slow threshold '{}'. Must be between 0.0 and 1.0", value);
                    }
                    return;
                }
            }
        }
        "scheduler-reallocate-threshold" | "scheduler_reallocate_threshold" => {
            match value.parse::<f64>() {
                Ok(n) if n > 0.0 && n <= 1.0 => {
                    global_config.scheduler_reallocate_threshold = n;
                }
                _ => {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "Invalid scheduler reallocate threshold"}}"#);
                    } else {
                        eprintln!("Error: Invalid scheduler reallocate threshold '{}'. Must be between 0.0 and 1.0", value);
                    }
                    return;
                }
            }
        }
        "dns-servers" | "dns_servers" => {
            // 支持逗号分隔的 DNS 服务器列表
            let servers: Vec<String> = value.split(',')
                .map(|s| s.trim().to_string())
                .filter(|s| !s.is_empty())
                .collect();

            if servers.is_empty() {
                if config.json_mode {
                    println!(r#"{{"status": "error", "message": "Invalid DNS servers"}}"#);
                } else {
                    eprintln!("Error: Invalid DNS servers '{}'. Use comma-separated IP addresses", value);
                }
                return;
            }

            global_config.dns_servers = servers;
        }
        "content-length-zero-retry" | "content_length_zero_retry" => {
            match value.parse::<u32>() {
                Ok(n) => {
                    global_config.content_length_zero_retry_count = n;
                }
                _ => {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "Invalid retry count"}}"#);
                    } else {
                        eprintln!("Error: Invalid retry count '{}'. Must be a non-negative integer", value);
                    }
                    return;
                }
            }
        }
        "header" | "headers" => {
            // 支持两种格式:
            // 1. JSON 对象: {"Header-Name": "Header-Value", ...}
            // 2. 简单格式: Header-Name: Header-Value
            if value.starts_with('{') {
                // JSON 格式
                match serde_json::from_str::<std::collections::HashMap<String, String>>(value) {
                    Ok(headers) => {
                        for (name, val) in headers {
                            global_config.headers.insert(name, val);
                        }
                    }
                    Err(e) => {
                        if config.json_mode {
                            println!(r#"{{"status": "error", "message": "Invalid JSON format: {}"}}"#, e);
                        } else {
                            eprintln!("Error: Invalid JSON format '{}': {}", value, e);
                            eprintln!("Use: {{\"Header-Name\": \"Header-Value\", ...}}");
                        }
                        return;
                    }
                }
            } else {
                // 简单格式: Header-Name: Header-Value
                if let Some(colon_pos) = value.find(':') {
                    let name = value[..colon_pos].trim().to_string();
                    let val = value[colon_pos + 1..].trim().to_string();
                    if !name.is_empty() {
                        global_config.headers.insert(name, val);
                    }
                } else {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "Invalid header format. Use: Header-Name: Header-Value"}}"#);
                    } else {
                        eprintln!("Error: Invalid header format '{}'. Use: Header-Name: Header-Value", value);
                    }
                    return;
                }
            }
        }
        "remove-header" | "remove_header" => {
            // 移除指定的 header
            if global_config.headers.remove(value).is_some() {
                if config.json_mode {
                    println!(r#"{{"status": "ok", "message": "Header removed"}}"#);
                } else {
                    println!("Header '{}' removed", value);
                }
            } else {
                if config.json_mode {
                    println!(r#"{{"status": "error", "message": "Header not found"}}"#);
                } else {
                    eprintln!("Error: Header '{}' not found", value);
                }
            }
            return;
        }
        "clear-headers" | "clear_headers" => {
            // 清除所有自定义 headers
            global_config.headers.clear();
        }
        "proxy" => {
            // 格式: key=proxy_url
            // 例如: *=http://127.0.0.1:10808（全局代理）
            //       1.2.3.4=http://127.0.0.1:10808（单个 IP 代理）
            //       dns:*=http://127.0.0.1:10808（全部 DNS 代理）
            //       dns:114.114.114.114=http://127.0.0.1:10808（单个 DNS 代理）
            if let Some(eq_pos) = value.find('=') {
                let key = value[..eq_pos].trim().to_string();
                let proxy_url = value[eq_pos + 1..].trim().to_string();
                if !key.is_empty() && !proxy_url.is_empty() {
                    global_config.proxy.insert(key, proxy_url);
                } else {
                    if config.json_mode {
                        println!(r#"{{"status": "error", "message": "Invalid proxy format. Use: key=proxy_url"}}"#);
                    } else {
                        eprintln!("Error: Invalid proxy format '{}'. Use: key=proxy_url", value);
                    }
                    return;
                }
            } else {
                if config.json_mode {
                    println!(r#"{{"status": "error", "message": "Invalid proxy format. Use: key=proxy_url"}}"#);
                } else {
                    eprintln!("Error: Invalid proxy format '{}'. Use: key=proxy_url", value);
                    eprintln!("Examples:");
                    eprintln!("  proxy *=http://127.0.0.1:10808        (global proxy)");
                    eprintln!("  proxy 1.2.3.4=http://127.0.0.1:10808  (single IP proxy)");
                    eprintln!("  proxy dns:*=http://127.0.0.1:10808    (all DNS proxy)");
                }
                return;
            }
        }
        "remove-proxy" | "remove_proxy" => {
            // 移除指定的代理
            if global_config.proxy.remove(value).is_some() {
                if config.json_mode {
                    println!(r#"{{"status": "ok", "message": "Proxy removed"}}"#);
                } else {
                    println!("Proxy '{}' removed", value);
                }
            } else {
                if config.json_mode {
                    println!(r#"{{"status": "error", "message": "Proxy not found"}}"#);
                } else {
                    eprintln!("Error: Proxy '{}' not found", value);
                }
            }
            return;
        }
        "clear-proxy" | "clear_proxy" => {
            // 清除所有代理配置
            global_config.proxy.clear();
        }
        _ => {
            if config.json_mode {
                println!(r#"{{"status": "error", "message": "Unknown config key: {}"}}"#, key);
            } else {
                eprintln!("Error: Unknown config key '{}'", key);
                eprintln!("Available keys: ip-protocol, connections, retry, chunk-size, speed-threshold, download-timeout, endpoint-failure-threshold, retry-reconnect-count, retry-refetch-endpoint-count, retry-refetch-url-count, speed-test-retry, speed-test-timeout, connectivity-timeout, dns-timeout, dns-retry, dns-servers, scheduler-interval, scheduler-slow-threshold, scheduler-reallocate-threshold, content-length-zero-retry, header, remove-header, clear-headers, proxy, remove-proxy, clear-proxy");
            }
            return;
        }
    }

    // 保存配置
    crate::config::set_global_config(global_config).await;

    // 保存到文件
    if let Err(e) = crate::config::save_config().await {
        if config.json_mode {
            println!(r#"{{"status": "error", "message": "{}"}}"#, e);
        } else {
            eprintln!("Warning: Failed to save config: {}", e);
        }
    }

    if config.json_mode {
        println!(r#"{{"status": "ok", "message": "Config updated"}}"#);
    } else {
        println!("Config updated: {} = {}", key, value);
    }
}

/// 执行下载循环
async fn cmd_loop(config: &CliConfig) {
    let downloads = state::load_all_downloads();
    let mut pending: Vec<_> = downloads.into_iter().filter(|d| {
        d.status == DownloadStatus::Pending || d.status == DownloadStatus::Downloading
    }).collect();

    if pending.is_empty() {
        if config.json_mode {
            println!(r#"{{"status": "ok", "message": "No pending downloads"}}"#);
        } else {
            println!("No pending downloads");
        }
        return;
    }

    if !config.json_mode {
        println!("Starting {} download(s)...", pending.len());
    }

    let mut handles = Vec::new();

    for mut info in pending {
        if !config.json_mode {
            println!("Downloading: {} ({})", info.id, info.url);
        }

        // 使用实际 URL（如果有重定向）
        let resume_url = if !info.actual_url.is_empty() {
            &info.actual_url
        } else {
            &info.url
        };

        // 使用已有的 endpoint 信息，不重新扫描
        let mut remote_file = RemoteFile::new(resume_url);

        // 添加自定义 headers 和代理配置
        let global_config = crate::config::get_global_config().await;
        for (name, value) in &global_config.headers {
            remote_file.request_headers.insert(name.clone(), value.clone());
        }
        // 添加代理配置
        for (key, proxy_url) in &global_config.proxy {
            remote_file.proxy_map.insert(key.clone(), proxy_url.clone());
        }
        drop(global_config);

        // 恢复保存的响应头（含 accept-ranges，用于检测 Range 支持）
        remote_file.update_response_headers(info.response_headers.clone());

        // 从下载信息中创建 endpoint list
        let mut endpoint_list = EndpointList::new(resume_url);
        for ep in &info.endpoints {
            if ep.enabled {
                endpoint_list.add_endpoint_with_id(ep.ip, ep.id);
            }
        }

        if endpoint_list.endpoints.is_empty() {
            if config.json_mode {
                println!(r#"{{"status": "error", "id": "{}", "message": "No enabled endpoints"}}"#, info.id);
            } else {
                eprintln!("  Error: No enabled endpoints");
            }
            continue;
        }

        // 启动下载

        match crate::downloader::start_download(
            info.file_size,
            &info.output,
            &remote_file,
            &endpoint_list,
            &info.id,
            if info.segments.is_empty() { None } else { Some(info.segments.clone()) },
            if info.connections > 0 { Some(info.connections) } else { None },
            Some(info.endpoints.len()),
            &info.url,
            &remote_file.url,
        ).await {
            Ok(handle) => {
                // 更新状态为下载中，并从 handle 中读取实际的连接数
                if let Some((_, mut download_info)) = state::find_download_by_id(&info.id) {
                    download_info.status = DownloadStatus::Downloading;
                    // 无 Content-Length 时不能断点续传，重置 downloaded 为 0
                    // （handle.rs 中也会重置，但此处确保 JSON 文件立即可见正确值）
                    if download_info.file_size == 0 {
                        download_info.downloaded = 0;
                        for seg in &mut download_info.segments {
                            seg.downloaded = 0;
                        }
                    }
                    let actual_connections = {
                        let state = handle.state.lock().await;
                        state.connections.len()
                    };
                    download_info.connections = actual_connections;
                    let _ = state::save_download(&download_info);
                }

                // 注册到下载管理器
                let mut manager = super::manager::DOWNLOAD_MANAGER.lock().await;
                manager.register(info.id.clone(), handle);
                let handle_arc = manager.get(&info.id).unwrap();
                drop(manager);

                // 启动后台任务监控下载完成状态
                let download_id = info.id.clone();
                tokio::spawn(async move {
                    loop {
                        tokio::time::sleep(std::time::Duration::from_secs(1)).await;

                        let manager = super::manager::DOWNLOAD_MANAGER.lock().await;
                        if let Some(handle_arc) = manager.get(&download_id) {
                            let handle = handle_arc.lock().await;
                            let progress = handle.get_progress().await;
                            // 获取当前 segment 进度 + completed 标志
                            let (current_segments, stream_completed) = {
                                let state = handle.state.lock().await;
                                (state.get_segment_progress(), state.completed.load(std::sync::atomic::Ordering::Relaxed))
                            };
                            drop(handle);
                            drop(manager);

                            // 更新进度到文件（保留 segments）
                            if let Some((_, mut download_info)) = state::find_download_by_id(&download_id) {
                                download_info.downloaded = progress.downloaded_bytes;
                                // 更新 segments
                                download_info.segments = current_segments.iter().map(|seg| {
                                    super::state::SegmentProgress {
                                        start: seg.start,
                                        end: seg.end,
                                        downloaded: seg.downloaded,
                                        completed: seg.downloaded >= (seg.end - seg.start),
                                        endpoint_id: seg.endpoint_id,
                                    }
                                }).collect();
                                // 同步 connections 为实际分片数
                                download_info.connections = download_info.segments.len();
                                let _ = state::save_download(&download_info);
                            }

                            // 检查是否完成
                            // 对于未知大小的下载（total_bytes=0），通过 completed 标志判断
                            if progress.percentage >= 100.0 || (progress.total_bytes == 0 && stream_completed) {
                                // 标记为完成：所有字段置 100% 再删除 .rdown.json
                                if let Some((path, mut download_info)) = state::find_download_by_id(&download_id) {
                                    download_info.status = DownloadStatus::Completed;
                                    if download_info.file_size == 0 {
                                        download_info.file_size = progress.downloaded_bytes;
                                    }
                                    download_info.downloaded = download_info.file_size;
                                    let _ = state::save_download(&download_info);
                                    let _ = std::fs::remove_file(&path);
                                }

                                eprintln!("Download completed: {}", download_id);
                                let mut manager = super::manager::DOWNLOAD_MANAGER.lock().await;
                                manager.remove(&download_id);
                                break;
                            }
                        } else {
                            break;
                        }
                    }
                });

                handles.push((info.id.clone(), handle_arc));

                if config.json_mode {
                    println!(r#"{{"status": "ok", "id": "{}", "message": "Download started"}}"#, info.id);
                } else {
                    println!("  Download started");
                }
            }
            Err(e) => {
                if let Some((_, mut download_info)) = state::find_download_by_id(&info.id) {
                    download_info.status = DownloadStatus::Failed;
                    let _ = state::save_download(&download_info);
                }

                if config.json_mode {
                    println!(r#"{{"status": "error", "id": "{}", "message": "{}"}}"#, info.id, e);
                } else {
                    eprintln!("  Download error: {}", e);
                }
            }
        }
    }

    if handles.is_empty() {
        if config.json_mode {
            println!(r#"{{"status": "ok", "message": "No downloads started"}}"#);
        } else {
            println!("No downloads started");
        }
        return;
    }

    if !config.json_mode {
        println!("\nWaiting for {} download(s) to complete...", handles.len());
    }

    // 等待所有下载完成（不持有锁，让后台任务可以更新进度）
    loop {
        tokio::time::sleep(std::time::Duration::from_secs(1)).await;

        let mut all_completed = true;
        let mut completed_ids = Vec::new();

        for (id, handle_arc) in &handles {
            let manager = super::manager::DOWNLOAD_MANAGER.lock().await;
            if let Some(handle_arc) = manager.get(id) {
                let handle = handle_arc.lock().await;
                let progress = handle.get_progress().await;
                drop(handle);
                drop(manager);

                if progress.percentage >= 100.0 {
                    completed_ids.push(id.clone());
                } else {
                    all_completed = false;
                }
            } else {
                // Handle not found, consider it completed
                completed_ids.push(id.clone());
            }
        }

        // 处理完成的下载
        for id in &completed_ids {
            // 删除 .rdown.json 文件
            if let Some((path, _)) = state::find_download_by_id(id) {
                let _ = std::fs::remove_file(&path);
            }

            if config.json_mode {
                println!(r#"{{"status": "ok", "id": "{}", "message": "Download completed"}}"#, id);
            } else {
                println!("Download {} completed", id);
            }

            // 从 handles 中移除
            handles.retain(|(h_id, _)| h_id != id);
        }

        if all_completed || handles.is_empty() {
            break;
        }
    }

    if !config.json_mode {
        println!("\nAll downloads completed.");
    }
}

/// 格式化字节数
fn format_bytes(bytes: u64) -> String {
    if bytes >= 1_073_741_824 {
        format!("{:.2} GB", bytes as f64 / 1_073_741_824.0)
    } else if bytes >= 1_048_576 {
        format!("{:.2} MB", bytes as f64 / 1_048_576.0)
    } else if bytes >= 1024 {
        format!("{:.2} KB", bytes as f64 / 1024.0)
    } else {
        format!("{} B", bytes)
    }
}

/// 格式化速度
fn format_speed(bytes_per_sec: f64) -> String {
    if bytes_per_sec >= 1_000_000.0 {
        format!("{:.2} MB/s", bytes_per_sec / 1_000_000.0)
    } else if bytes_per_sec >= 1_000.0 {
        format!("{:.2} KB/s", bytes_per_sec / 1_000.0)
    } else {
        format!("{:.2} B/s", bytes_per_sec)
    }
}
