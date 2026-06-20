use std::collections::HashSet;
use std::net::IpAddr;
use std::sync::atomic::Ordering;
use std::sync::Arc;
use std::time::{Duration, Instant};

use futures_util::StreamExt;
use reqwest::Client;
use tokio::fs::File;
use tokio::io::{AsyncSeekExt, AsyncWriteExt};
use tokio::sync::{Mutex, watch};

use crate::config::GLOBAL_CONFIG;
use crate::remote_file::RemoteFile;

use super::state::{DownloadState, SegmentStatus, SharedDownloadState};

/// Connection 运行状态
pub struct ConnectionRunner {
    /// Connection ID
    pub id: usize,
    /// 使用的 endpoint
    pub endpoint: IpAddr,
    /// 文件句柄
    pub file: Arc<Mutex<File>>,
    /// 下载状态
    pub state: SharedDownloadState,
    /// 调度变更通知
    pub schedule_rx: watch::Receiver<()>,
    /// RemoteFile 配置
    pub remote_file: Arc<Mutex<RemoteFile>>,
}

impl ConnectionRunner {
    /// 运行 connection
    pub async fn run(&mut self) {
        loop {
            // 检查是否取消
            {
                let state = self.state.lock().await;
                if state.cancelled.load(Ordering::Relaxed) {
                    return;
                }
            }

            // 检查是否完成
            {
                let state = self.state.lock().await;
                if state.completed.load(Ordering::Relaxed) {
                    return;
                }
            }

            // 检查是否暂停
            {
                let state = self.state.lock().await;
                if state.paused.load(Ordering::Relaxed) {
                    drop(state);
                    tokio::time::sleep(Duration::from_millis(100)).await;
                    continue;
                }
            }

            // 检查此 connection 是否已被 scheduler 移除（如 total_connections 减少）
            {
                let state = self.state.lock().await;
                let exists = state.connections.iter().any(|c| c.id == self.id);
                if !exists {
                    return;
                }
            }

            // 从共享状态重新读取当前 endpoint（支持运行时 endpoint 重新分配）
            // 如果当前 endpoint 已被禁用，主动迁移到非禁用 endpoint
            {
                let mut state = self.state.lock().await;
                if let Some(conn) = state.connections.iter().find(|c| c.id == self.id) {
                    self.endpoint = conn.endpoint;
                }
                if state.disabled_endpoints.contains(&self.endpoint) {
                    // 从已知 endpoint 中找非禁用的
                    let new_ip = state.known_endpoints.iter()
                        .find(|ip| !state.disabled_endpoints.contains(ip))
                        .copied();
                    if let Some(ip) = new_ip {
                        let new_eid = state.connections.iter()
                            .find(|c| c.endpoint == ip)
                            .map(|c| c.endpoint_id)
                            .unwrap_or(0);
                        if let Some(conn) = state.connections.iter_mut().find(|c| c.id == self.id) {
                            conn.endpoint = ip;
                            conn.endpoint_id = new_eid;
                            self.endpoint = ip;
                        }
                    }
                }
            }

            // 获取当前分配的 segment，或尝试获取一个未分配的 Pending segment
            let segment_index = {
                let state = self.state.lock().await;
                state.connections.iter().find(|c| c.id == self.id).and_then(|c| c.current_segment)
            };

            let segment_index = match segment_index {
                Some(idx) => {
                    // 检查该 segment 是否还是 Pending 或 Downloading
                    let state = self.state.lock().await;
                    if idx < state.segments.len() {
                        let seg_status = &state.segments[idx].status;
                        if *seg_status != SegmentStatus::Pending && *seg_status != SegmentStatus::Downloading {
                            // segment 状态异常，清除分配
                            drop(state);
                            let mut state = self.state.lock().await;
                            if let Some(conn) = state.connections.iter_mut().find(|c| c.id == self.id) {
                                conn.current_segment = None;
                            }
                            continue;
                        }
                    }
                    Some(idx)
                }
                None => {
                    // 没有分配 segment，尝试从空闲的 Pending segment 中获取一个
                    let mut state = self.state.lock().await;

                    // 从共享状态同步 endpoint（防止被外部修改）
                    if let Some(conn) = state.connections.iter().find(|c| c.id == self.id) {
                        self.endpoint = conn.endpoint;
                    }

                    // 找一个未被任何连接申领的 Pending segment
                    let claimed_indices: std::collections::HashSet<usize> = state.connections.iter()
                        .filter_map(|c| c.current_segment)
                        .collect();

                    let pending_idx = state.segments.iter().enumerate()
                        .position(|(i, s)| s.status == SegmentStatus::Pending && !claimed_indices.contains(&i));

                    match pending_idx {
                        Some(idx) => {
                            // 分配给自己
                            let endpoint_id = if let Some(conn) = state.connections.iter_mut().find(|c| c.id == self.id) {
                                conn.current_segment = Some(idx);
                                Some(conn.endpoint_id)
                            } else {
                                None
                            };
                            if let Some(segment) = state.segments.get_mut(idx) {
                                segment.assigned_endpoint = Some(self.endpoint);
                                segment.assigned_endpoint_id = endpoint_id;
                                segment.status = SegmentStatus::Downloading;
                            }
                            drop(state);
                            Some(idx)
                        }
                        None => {
                            drop(state);
                            // 没有可用的 segment，等待调度
                            tokio::select! {
                                _ = self.schedule_rx.changed() => {}
                                _ = tokio::time::sleep(Duration::from_millis(200)) => {}
                            }
                            continue;
                        }
                    }
                }
            };

            let segment_index = match segment_index {
                Some(idx) => idx,
                None => continue,
            };

            // 下载 segment
            let result = self.download_segment(segment_index).await;

            match result {
                Ok(()) => {
                    // 检查 segment 是否真的下载完成（可能被 disable_endpoint 中断）
                    let mut state = self.state.lock().await;
                    let segment_completed = if let Some(segment) = state.segments.get(segment_index) {
                        // segment 可能已在 download_segment 内部被标记为 Completed
                        // （如完整文件下载场景），也可能通过 downloaded 字节数判断
                        segment.status == SegmentStatus::Completed
                            || segment.downloaded >= (segment.end - segment.start)
                    } else {
                        false
                    };

                    if segment_completed {
                        // segment 真正完成
                        if let Some(segment) = state.segments.get_mut(segment_index) {
                            segment.status = SegmentStatus::Completed;
                        }
                        if let Some(conn) = state.connections.iter_mut().find(|c| c.id == self.id) {
                            conn.current_segment = None;
                            conn.failure_count = 0;  // 重置失败计数
                            conn.speed = 0.0;  // 重置速度
                        }

                        // 检查是否全部完成
                        let all_completed = state.segments.iter().all(|s| s.status == SegmentStatus::Completed);
                        if all_completed {
                            state.completed.store(true, Ordering::Relaxed);
                        }
                    } else {
                        // 被中断（如 endpoint 被禁用，或 scheduler 重建了 segments），
                        // 保留已下载进度，标记为 Pending 等待重新分配
                        if let Some(segment) = state.segments.get_mut(segment_index) {
                            segment.status = SegmentStatus::Pending;
                            segment.assigned_endpoint = None;
                            segment.assigned_endpoint_id = None;
                        } else {
                            // segment 已被 scheduler 移除（如 total_connections 减少），
                            // segment 数据已迁移到新分片，无需额外处理
                        }
                        if let Some(conn) = state.connections.iter_mut().find(|c| c.id == self.id) {
                            conn.current_segment = None;
                            conn.speed = 0.0;
                        }
                    }
                }
                Err(e) => {
                    eprintln!("Connection {} download error: {}", self.id, e);

                    let mut state = self.state.lock().await;

                    // 检查是否应该立即迁移的错误
                    let is_permanent_error = e.starts_with("HTTP error:") ||
                        e.contains("Unexpected content length") ||
                        e.contains("Content-Length=0") ||
                        e.contains("Got 0 bytes") ||
                        e.contains("error sending request");

                    // 增加失败计数
                    let should_migrate = if let Some(conn) = state.connections.iter_mut().find(|c| c.id == self.id) {
                        conn.failure_count += 1;
                        conn.last_error = Some(e.clone());

                        let config = crate::config::GLOBAL_CONFIG.try_read();
                        let threshold = config.map(|c| c.endpoint_failure_threshold).unwrap_or(3);
                        is_permanent_error || conn.failure_count >= threshold
                    } else {
                        false
                    };

                    if should_migrate {
                        // 获取当前 connection 的 endpoint
                        let old_ip = if let Some(conn) = state.connections.iter().find(|c| c.id == self.id) {
                            conn.endpoint
                        } else {
                            // connection 已被移除
                            drop(state);
                            continue;
                        };

                        // 从已知 endpoint 中找其他可用的（不在 disabled_endpoints 中，不是当前 IP）
                        let other_eps: Vec<IpAddr> = state.known_endpoints.iter()
                            .filter(|ip| **ip != old_ip && !state.disabled_endpoints.contains(ip))
                            .copied()
                            .collect();

                        if other_eps.is_empty() {
                            // 没有其他可用的 endpoint，标记旧 endpoint 为 disabled，等待 enable-endpoint 或 retry
                            eprintln!("No other endpoints available after connection {} failed, waiting for retry...", self.id);
                            state.disabled_endpoints.insert(old_ip);
                        } else {
                            // 迁移到另一个 endpoint
                            let new_ip = other_eps[0];
                            // 查找目标 endpoint 的正确 endpoint_id
                            let new_eid = state.connections.iter()
                                .find(|c| c.endpoint == new_ip)
                                .map(|c| c.endpoint_id)
                                .unwrap_or(0);
                            if let Some(conn) = state.connections.iter_mut().find(|c| c.id == self.id) {
                                eprintln!("Connection {} migrating endpoint {} -> {}", self.id, conn.endpoint, new_ip);
                                conn.endpoint = new_ip;
                                conn.endpoint_id = new_eid;
                                conn.failure_count = 0;
                                conn.last_error = None;
                            }

                            // 检查旧 endpoint 是否还有 connection
                            let old_has_conns = state.connections.iter()
                                .any(|c| c.endpoint == old_ip);
                            if !old_has_conns {
                                state.disabled_endpoints.insert(old_ip);
                                eprintln!("Endpoint {} disabled (no connections remaining)", old_ip);
                            }
                        }
                    }

                    // 标记 segment 为 Pending，清除 connection 的分配
                    if let Some(segment) = state.segments.get_mut(segment_index) {
                        segment.status = SegmentStatus::Pending;
                        segment.assigned_endpoint = None;
                        segment.assigned_endpoint_id = None;
                    }
                    if let Some(conn) = state.connections.iter_mut().find(|c| c.id == self.id) {
                        conn.current_segment = None;
                    }
                }
            }

            // 等待新的调度
            tokio::select! {
                _ = self.schedule_rx.changed() => {
                    // 调度变更，继续循环
                }
                _ = tokio::time::sleep(Duration::from_millis(100)) => {
                    // 超时，继续循环
                }
            }
        }
    }

    /// 下载一个 segment
    async fn download_segment(&mut self, segment_index: usize) -> Result<(), String> {
        let config = GLOBAL_CONFIG.read().await;
        let chunk_size = config.chunk_size;
        let retry_count = config.retry_count;
        let download_timeout = config.download_timeout();
        drop(config);

        // 获取 segment 信息
        let (start, end, current_pos) = {
            let state = self.state.lock().await;
            let segment = state.segments.get(segment_index).ok_or("Segment not found")?;
            (segment.start, segment.end, segment.current_position())
        };

        // 标记 segment 为下载中，同时重新同步 self.endpoint
        {
            let mut state = self.state.lock().await;
            // 从共享状态重新同步 endpoint
            let endpoint_id = if let Some(conn) = state.connections.iter().find(|c| c.id == self.id) {
                self.endpoint = conn.endpoint;
                Some(conn.endpoint_id)
            } else {
                None
            };
            if let Some(segment) = state.segments.get_mut(segment_index) {
                segment.status = SegmentStatus::Downloading;
                segment.assigned_endpoint = Some(self.endpoint);
                segment.assigned_endpoint_id = endpoint_id;
            }
            if let Some(conn) = state.connections.iter_mut().find(|c| c.id == self.id) {
                conn.current_segment = Some(segment_index);
            }
        }

        // 构建 HTTP 客户端
        let remote_file = self.remote_file.lock().await;
        let proxy = remote_file.get_endpoint_proxy(&self.endpoint).cloned();
        let mut url = remote_file.url.clone();
        let request_headers = remote_file.request_headers.clone();
        drop(remote_file);

        let url_info = crate::utils::parse_url(&url);

        // host 始终从 url 获取（跟随重定向）
        let host = url_info.as_ref()
            .map(|u| u.host.clone())
            .unwrap_or_default();

        let port = url_info.as_ref().ok()
            .and_then(|u| u.ports.first().copied())
            .unwrap_or(443);

        let is_ip_url = host.parse::<std::net::IpAddr>().is_ok();

        // IP URL: 替换为 endpoint IP
        if is_ip_url {
            let path = url_info.as_ref().map(|u| u.path.as_str()).unwrap_or("/");
            let scheme = url_info.as_ref().map(|u| if u.https { "https" } else { "http" }).unwrap_or("https");
            if self.endpoint.is_ipv6() {
                url = format!("{}://[{}]:{}{}", scheme, self.endpoint, port, path);
            } else {
                url = format!("{}://{}:{}{}", scheme, self.endpoint, port, path);
            }
        }

        let mut client_builder = Client::builder()
            .timeout(download_timeout)
            .http1_only()
            .no_proxy()
            .pool_max_idle_per_host(0)
            .danger_accept_invalid_certs(true)
            .redirect(reqwest::redirect::Policy::limited(10));

        // hostname URL: 用 resolve pin 到 endpoint IP
        if !is_ip_url {
            client_builder = client_builder.resolve(&host, std::net::SocketAddr::new(self.endpoint, port));
        }

        if let Some(proxy_url) = proxy {
            let proxy = reqwest::Proxy::all(&proxy_url)
                .map_err(|e| format!("Proxy error: {}", e))?;
            client_builder = client_builder.proxy(proxy);
        }

        let client = client_builder
            .build()
            .map_err(|e| format!("Client error: {}", e))?;

        // 开始下载
        let mut pos = current_pos;
        let speed_window = Duration::from_secs(1);
        let mut window_bytes = 0u64;
        let mut window_start = Instant::now();
        let mut last_data_time = Instant::now();

        while pos < end {
            // 检查是否取消或暂停，或已完成（另一 connection 可能已处理 no-Range 降级）
            {
                let state = self.state.lock().await;
                if state.cancelled.load(Ordering::Relaxed)
                    || state.paused.load(Ordering::Relaxed)
                    || state.completed.load(Ordering::Relaxed)
                {
                    return Ok(());
                }
            }

            // 检查是否有调度变更
            if self.schedule_rx.has_changed().unwrap_or(false) {
                // 检查当前 segment 是否还分配给自己
                let state = self.state.lock().await;
                let still_assigned = state.segments.get(segment_index)
                    .and_then(|s| s.assigned_endpoint)
                    .map(|ep| ep == self.endpoint)
                    .unwrap_or(false);

                if !still_assigned {
                    // 调度变更导致 segment 被重新分配，退出
                    drop(state);
                    return Ok(());
                }
            }

            // 计算本次下载大小
            let remaining = end - pos;
            let current_chunk = std::cmp::min(chunk_size as u64, remaining);

            // 构建请求
            let mut request = client.get(&url)
                .header("Host", &host);

            // 只有在文件大小已知且大于 0 时才使用 Range 头
            let use_range = end > start && end != u64::MAX;
            if use_range {
                request = request.header("Range", format!("bytes={}-{}", pos, pos + current_chunk - 1));
            }

            for (key, value) in &request_headers {
                request = request.header(key, value);
            }

            // 发送请求（带重试）
            let mut response = None;
            for attempt in 0..=retry_count {
                if attempt > 0 {
                    tokio::time::sleep(Duration::from_millis(100 * attempt as u64)).await;
                }

                match request.try_clone().ok_or("Failed to clone request")?.send().await {
                    Ok(resp) => {
                        // 检查 HTTP 状态码
                        if !resp.status().is_success() {
                            let status = resp.status();
                            if attempt == retry_count {
                                return Err(format!("HTTP error: {}", status));
                            }
                            continue;
                        }
                        response = Some(resp);
                        break;
                    }
                    Err(e) => {
                        if attempt == retry_count {
                            return Err(format!("Request failed after {} retries: {}", retry_count, e));
                        }
                    }
                }
            }

            let mut response = response.ok_or("No response")?;

            // 检查服务器是否支持 Range 请求
            // 如果我们发送了 Range 头但服务器返回 200 OK（而不是 206 Partial Content），
            // 说明服务器不支持 Range 请求
            let range_supported = response.status() == reqwest::StatusCode::PARTIAL_CONTENT;

            // 检查响应头中是否有 Content-Length，验证下载大小
            let mut content_length_zero_retries = 0;
            let max_content_length_zero_retries = {
                let config = GLOBAL_CONFIG.read().await;
                config.content_length_zero_retry_count
            };

            if let Some(content_length) = response.headers().get("content-length") {
                if let Ok(length_str) = content_length.to_str() {
                    if let Ok(length) = length_str.parse::<u64>() {
                        // Content-Length=0 时重试
                        if length == 0 && current_chunk > 0 {
                            content_length_zero_retries += 1;
                            if content_length_zero_retries <= max_content_length_zero_retries {
                                eprintln!("Connection {} got Content-Length=0, retrying ({}/{})",
                                    self.id, content_length_zero_retries, max_content_length_zero_retries);
                                tokio::time::sleep(Duration::from_millis(500 * content_length_zero_retries as u64)).await;
                                continue;
                            } else {
                                return Err("Content-Length=0 after max retries".to_string());
                            }
                        }

                        // 尝试更新文件大小（如果当前为 0）
                        if length > 0 {
                            let mut state = self.state.lock().await;
                            if state.total_bytes == 0 {
                                // 计算实际文件大小：Content-Length 是当前请求的范围大小
                                // 需要从 Content-Range 头获取完整文件大小
                                if let Some(content_range) = response.headers().get("content-range") {
                                    if let Ok(range_str) = content_range.to_str() {
                                        // 格式: bytes 0-1023/4096
                                        if let Some(total_str) = range_str.split('/').last() {
                                            if let Ok(total) = total_str.parse::<u64>() {
                                                state.total_bytes = total;
                                                eprintln!("Connection {} updated file size: {} bytes", self.id, total);
                                            }
                                        }
                                    }
                                } else {
                                    // 没有 Content-Range，说明是完整响应
                                    // 使用 Content-Length 作为文件大小
                                    state.total_bytes = length;
                                    // 同时更新 segment 的 end 位置
                                    if let Some(segment) = state.segments.get_mut(segment_index) {
                                        segment.end = segment.start + length;
                                    }
                                    eprintln!("Connection {} updated file size: {} bytes", self.id, length);
                                }
                            }
                        }

                        // 如果响应体大小与预期不符，可能是错误页面
                        if length < current_chunk && pos + length < end {
                            return Err(format!("Unexpected content length: {} (expected {})", length, current_chunk));
                        }
                    }
                }
            }

            // 文件大小未知（end == u64::MAX），流式读取响应体
            // 边接收边写硬盘边更新进度，避免一次性加载整个响应
            if end == u64::MAX {
                let mut stream = response.bytes_stream();
                let mut stream_pos = pos;
                let mut chunk_count = 0u64;
                loop {
                    let next_result = stream.next().await;
                    match next_result {
                        None => {
                            break;
                        }
                        Some(Err(e)) => {
                            return Err(format!("Stream read error: {}", e));
                        }
                        Some(Ok(data)) => {
                            chunk_count += 1;
                            // 检查取消/暂停
                            {
                                let state = self.state.lock().await;
                                if state.cancelled.load(Ordering::Relaxed)
                                    || state.paused.load(Ordering::Relaxed)
                                    || state.completed.load(Ordering::Relaxed)
                                {
                                    return Ok(());
                                }
                            }

                            if data.is_empty() {
                                continue;
                            }
                            let data_len = data.len() as u64;

                            // 写入文件
                            {
                                let mut file = self.file.lock().await;
                                file.seek(std::io::SeekFrom::Start(stream_pos))
                                    .await
                                    .map_err(|e| format!("Seek error: {}", e))?;
                                file.write_all(&data)
                                    .await
                                    .map_err(|e| format!("Write error: {}", e))?;
                            }

                            stream_pos += data_len;

                            // 更新进度
                            {
                                let mut state = self.state.lock().await;
                                if let Some(segment) = state.segments.get_mut(segment_index) {
                                    segment.downloaded = stream_pos - segment.start;
                                }
                                if let Some(conn) = state.connections.iter_mut().find(|c| c.id == self.id) {
                                    conn.downloaded += data_len;
                                }
                                state.downloaded_bytes.fetch_add(data_len, Ordering::Relaxed);
                                // 不更新 total_bytes：未知文件大小时 total_bytes 保持 0
                                // 否则会导致 percentage = downloaded/total = 100% 过早触发完成
                            }

                            // 计算速度（与分段下载使用相同逻辑）
                            window_bytes += data_len;
                            last_data_time = Instant::now();
                            let elapsed = window_start.elapsed();
                            if elapsed >= speed_window {
                                let speed = window_bytes as f64 / elapsed.as_secs_f64();
                                let mut state = self.state.lock().await;
                                if let Some(conn) = state.connections.iter_mut().find(|c| c.id == self.id) {
                                    conn.speed = speed;
                                }
                                window_bytes = 0;
                                window_start = Instant::now();
                            }
                        }
                    }
                }


                // 标记完成
                {
                    let mut state = self.state.lock().await;
                    if let Some(segment) = state.segments.get_mut(segment_index) {
                        segment.downloaded = stream_pos - segment.start;
                        segment.status = SegmentStatus::Completed;
                    }
                    state.completed.store(true, Ordering::Relaxed);
                }

                return Ok(());
            }

            // 有 Content-Length 但服务器不支持 Range 请求
            // 流式读取完整响应，边接收边写盘边更新进度
            if !range_supported && end != u64::MAX {
                // 检查是否已被其他 connection 处理
                {
                    let state = self.state.lock().await;
                    if state.completed.load(Ordering::Relaxed) {
                        return Ok(());
                    }
                }

                eprintln!("Connection {}: Range not supported by server, falling back to single-connection streaming download", self.id);

                let mut stream = response.bytes_stream();
                let mut stream_pos: u64 = 0;
                let mut chunk_count = 0u64;
                let speed_window = Duration::from_secs(1);
                let mut window_bytes = 0u64;
                let mut window_start = Instant::now();
                let file_size = end - start; // total expected size

                // 重置文件写入位置为 0
                {
                    let mut file = self.file.lock().await;
                    file.seek(std::io::SeekFrom::Start(0))
                        .await
                        .map_err(|e| format!("Seek error: {}", e))?;
                }

                loop {
                    // 检查取消/暂停
                    {
                        let state = self.state.lock().await;
                        if state.cancelled.load(Ordering::Relaxed)
                            || state.paused.load(Ordering::Relaxed)
                            || state.completed.load(Ordering::Relaxed)
                        {
                            return Ok(());
                        }
                    }

                    let next_result = stream.next().await;
                    match next_result {
                        None => {
                            break;
                        }
                        Some(Err(e)) => {
                            return Err(format!("Stream read error: {}", e));
                        }
                        Some(Ok(data)) => {
                            chunk_count += 1;
                            if data.is_empty() {
                                continue;
                            }
                            let data_len = data.len() as u64;

                            // 写入文件
                            {
                                let mut file = self.file.lock().await;
                                file.seek(std::io::SeekFrom::Start(stream_pos))
                                    .await
                                    .map_err(|e| format!("Seek error: {}", e))?;
                                file.write_all(&data)
                                    .await
                                    .map_err(|e| format!("Write error: {}", e))?;
                            }

                            stream_pos += data_len;

                            // 更新进度
                            {
                                let mut state = self.state.lock().await;
                                if let Some(segment) = state.segments.get_mut(segment_index) {
                                    segment.downloaded = stream_pos;
                                }
                                if let Some(conn) = state.connections.iter_mut().find(|c| c.id == self.id) {
                                    conn.downloaded += data_len;
                                }
                                state.downloaded_bytes.fetch_add(data_len, Ordering::Relaxed);
                            }

                            // 计算速度
                            window_bytes += data_len;
                            let elapsed = window_start.elapsed();
                            if elapsed >= speed_window {
                                let speed = window_bytes as f64 / elapsed.as_secs_f64();
                                let mut state = self.state.lock().await;
                                if let Some(conn) = state.connections.iter_mut().find(|c| c.id == self.id) {
                                    conn.speed = speed;
                                }
                                window_bytes = 0;
                                window_start = Instant::now();
                            }

                        }
                    }
                }

                // 标记所有 segment 为已完成
                let mut state = self.state.lock().await;
                let total = state.total_bytes.max(stream_pos);
                state.total_bytes = total;
                for segment in state.segments.iter_mut() {
                    segment.downloaded = segment.end - segment.start;
                    segment.status = SegmentStatus::Completed;
                }
                for conn in state.connections.iter_mut() {
                    conn.current_segment = None;
                    conn.speed = 0.0;
                }
                state.downloaded_bytes.store(total, Ordering::Relaxed);
                state.completed.store(true, Ordering::Relaxed);

                return Ok(());
            }

            // 正常分段下载：读取 chunk 响应体
            let bytes = response.bytes().await
                .map_err(|e| format!("Read body error: {}", e))?;

            let bytes_len = bytes.len() as u64;

            // 如果实际读取的字节数为 0，也重试
            if bytes_len == 0 && current_chunk > 0 {
                content_length_zero_retries += 1;
                if content_length_zero_retries <= max_content_length_zero_retries {
                    eprintln!("Connection {} got 0 bytes, retrying ({}/{})",
                        self.id, content_length_zero_retries, max_content_length_zero_retries);
                    tokio::time::sleep(Duration::from_millis(500 * content_length_zero_retries as u64)).await;
                    continue;
                } else {
                    return Err("Got 0 bytes after max retries".to_string());
                }
            }

            // 写入文件
            {
                let mut file = self.file.lock().await;
                file.seek(std::io::SeekFrom::Start(pos))
                    .await
                    .map_err(|e| format!("Seek error: {}", e))?;
                file.write_all(&bytes)
                    .await
                    .map_err(|e| format!("Write error: {}", e))?;
            }

            // 更新状态
            pos += bytes_len;

            {
                let mut state = self.state.lock().await;

                // 更新 segment 下载进度
                if let Some(segment) = state.segments.get_mut(segment_index) {
                    segment.downloaded = pos - segment.start;
                }

                // 更新 connection 下载进度
                if let Some(conn) = state.connections.iter_mut().find(|c| c.id == self.id) {
                    conn.downloaded += bytes_len;
                }

                // 更新总下载进度
                state.downloaded_bytes.fetch_add(bytes_len, Ordering::Relaxed);
            }

            // 计算速度
            window_bytes += bytes_len;
            last_data_time = Instant::now();
            let elapsed = window_start.elapsed();
            if elapsed >= speed_window {
                let speed = window_bytes as f64 / elapsed.as_secs_f64();
                let mut state = self.state.lock().await;
                if let Some(conn) = state.connections.iter_mut().find(|c| c.id == self.id) {
                    conn.speed = speed;
                }
                window_bytes = 0;
                window_start = Instant::now();
            }

            // 检查是否超时（2秒没有数据）
            if last_data_time.elapsed() > Duration::from_secs(2) {
                let mut state = self.state.lock().await;
                if let Some(conn) = state.connections.iter_mut().find(|c| c.id == self.id) {
                    conn.speed = 0.0;
                }
            }
        }

        Ok(())
    }
}
