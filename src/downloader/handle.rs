use std::collections::{HashMap, HashSet};
use std::net::IpAddr;
use std::sync::atomic::Ordering;
use std::sync::Arc;

use tokio::sync::{Mutex, watch};
use tokio::task::JoinHandle;

use crate::config::GLOBAL_CONFIG;
use crate::endpoint_test::EndpointList;
use crate::remote_file::RemoteFile;

use super::allocator;
use super::connection::ConnectionRunner;
use super::state::{DownloadState, EndpointStatus, ProgressInfo, SharedDownloadState, Segment, SegmentStatus};

/// 下载句柄
pub struct DownloadHandle {
    /// 下载状态
    pub state: SharedDownloadState,
    /// 调度变更通知发送端
    schedule_tx: watch::Sender<()>,
    /// 输出路径（用于保存进度）
    output_path: String,
    /// 下载 ID
    download_id: String,
    /// 原始 URL
    original_url: String,
    /// 当前实际 URL
    current_url: String,
    /// RemoteFile 配置
    remote_file: Arc<Mutex<RemoteFile>>,
    /// 文件句柄（用于生成新 ConnectionRunner）
    file: Arc<Mutex<tokio::fs::File>>,
}

impl DownloadHandle {
    /// 创建新的下载句柄
    pub async fn new(
        state: SharedDownloadState,
        schedule_tx: watch::Sender<()>,
        _tasks: Vec<JoinHandle<()>>,
        output_path: String,
        download_id: String,
        original_url: String,
        current_url: String,
        remote_file: Arc<Mutex<RemoteFile>>,
        file: Arc<Mutex<tokio::fs::File>>,
    ) -> Self {
        // tasks 不会被 await，由 tokio runtime 管理生命周期
        drop(_tasks);

        Self {
            state,
            schedule_tx,
            output_path,
            download_id,
            original_url,
            current_url,
            remote_file,
            file,
        }
    }

    /// 为单个连接生成 ConnectionRunner 并 spawn
    fn spawn_runner(&self, conn_id: usize, endpoint: IpAddr) {
        let mut runner = ConnectionRunner {
            id: conn_id,
            endpoint,
            file: self.file.clone(),
            state: self.state.clone(),
            schedule_rx: self.schedule_tx.subscribe(),
            remote_file: self.remote_file.clone(),
        };
        tokio::spawn(async move {
            runner.run().await;
        });
    }

    /// 禁用某个 endpoint
    /// 将被禁用的连接重新分配到其他 endpoint，保留已下载进度
    pub async fn disable_endpoint(&self, ip: IpAddr) {
        let mut state = self.state.lock().await;

        // 收集被禁用的 connection ID 及其当前 segment
        let disabled_conns: Vec<(usize, Option<usize>)> = state.connections.iter()
            .filter(|c| c.endpoint == ip)
            .map(|c| (c.id, c.current_segment))
            .collect();

        if disabled_conns.is_empty() {
            state.disabled_endpoints.insert(ip);
            drop(state);
            return;
        }

        // 收集其他可用的 endpoint（不在 disabled_endpoints 中）
        let remaining_eps: Vec<(IpAddr, usize)> = {
            let mut eps: Vec<IpAddr> = state.connections.iter()
                .filter(|c| c.endpoint != ip && !state.disabled_endpoints.contains(&c.endpoint))
                .map(|c| c.endpoint)
                .collect();
            eps.dedup();
            if eps.is_empty() {
                // fallback: 使用所有非当前 IP 的 endpoint
                eps = state.connections.iter()
                    .filter(|c| c.endpoint != ip)
                    .map(|c| c.endpoint)
                    .collect();
                eps.dedup();
            }
            eps.into_iter().map(|ep_ip| {
                let eid = state.connections.iter()
                    .find(|c| c.endpoint == ep_ip)
                    .map(|c| c.endpoint_id)
                    .unwrap_or(0);
                (ep_ip, eid)
            }).collect()
        };

        // 标记该 endpoint 为 disabled
        state.disabled_endpoints.insert(ip);

        if remaining_eps.is_empty() {
            // 所有 endpoint 都被禁用，释放 connection 等待用户启用新 endpoint 或重试管理器介入
            eprintln!("All endpoints disabled, waiting for new endpoints or retry...");
            for conn in state.connections.iter_mut() {
                if conn.endpoint == ip {
                    conn.current_segment = None;
                }
            }
            for segment in state.segments.iter_mut() {
                if segment.assigned_endpoint == Some(ip) && segment.status != SegmentStatus::Completed {
                    segment.status = SegmentStatus::Pending;
                    segment.assigned_endpoint = None;
                }
            }
            // 不取消下载，等待 enable-endpoint 或 retry
            drop(state);
            let _ = self.schedule_tx.send(());
            return;
        }

        // 将被禁用的连接重新分配到其他 endpoint
        for (i, (conn_id, seg_idx)) in disabled_conns.iter().enumerate() {
            let (new_ip, new_eid) = remaining_eps[i % remaining_eps.len()];

            if let Some(conn) = state.connections.iter_mut().find(|c| c.id == *conn_id) {
                conn.endpoint = new_ip;
                conn.endpoint_id = new_eid;
                conn.failure_count = 0;
                conn.last_error = None;
                conn.current_segment = *seg_idx;
            }

            // 更新 segment 的 assigned_endpoint
            if let Some(idx) = seg_idx {
                if *idx < state.segments.len() {
                    state.segments[*idx].assigned_endpoint = Some(new_ip);
                    state.segments[*idx].assigned_endpoint_id = Some(new_eid);
                    if state.segments[*idx].status != SegmentStatus::Completed {
                        state.segments[*idx].status = SegmentStatus::Pending;
                    }
                }
            }
        }

        eprintln!(
            "Disable endpoint {}: reassigned {} connection(s) to {} remaining endpoint(s)",
            ip, disabled_conns.len(), remaining_eps.len()
        );

        drop(state);
        let _ = self.schedule_tx.send(());
    }

    /// 启用某个 endpoint
    /// endpoint_id: 可选，该 IP 在配置中的 endpoint ID
    pub async fn enable_endpoint(&self, ip: IpAddr, endpoint_id: Option<usize>) {
        let mut state = self.state.lock().await;

        // 从 disabled 集合中移除，加入已知列表
        state.disabled_endpoints.remove(&ip);
        state.known_endpoints.insert(ip);

        let has_existing = state.connections.iter().any(|c| c.endpoint == ip);

        if has_existing {
            // 该 endpoint 已有 connection（可能因自动迁移而保留），将 Pending segment 分配给空闲连接
            let pending_indices: Vec<usize> = state.segments.iter().enumerate()
                .filter(|(_, s)| s.status == SegmentStatus::Pending)
                .map(|(i, _)| i)
                .collect();

            for seg_idx in pending_indices {
                if let Some(conn_idx) = state.connections.iter().position(|c| {
                    c.endpoint == ip && c.current_segment.is_none()
                }) {
                    state.segments[seg_idx].assigned_endpoint = Some(ip);
                    state.segments[seg_idx].assigned_endpoint_id = Some(endpoint_id.unwrap_or(state.connections[conn_idx].endpoint_id));
                    state.connections[conn_idx].current_segment = Some(seg_idx);
                }
            }
        } else {
            // 该 IP 没有现成连接：从其他 endpoint 中重新分配

            // 收集现有的 endpoint（不在 disabled 中，排除当前 IP）
            let existing_eps: Vec<(IpAddr, usize, usize)> = {
                let mut map: HashMap<IpAddr, (usize, Vec<usize>)> = HashMap::new();
                for conn in state.connections.iter() {
                    if conn.endpoint != ip && !state.disabled_endpoints.contains(&conn.endpoint) {
                        let entry = map.entry(conn.endpoint).or_insert((0, Vec::new()));
                        entry.0 = conn.endpoint_id;
                        entry.1.push(conn.id);
                    }
                }
                map.into_iter()
                    .map(|(ep_ip, (eid, conn_ids))| (ep_ip, eid, conn_ids.len()))
                    .collect()
            };

            if existing_eps.is_empty() {
                // 所有其他 endpoint 都被 disabled，从全部 connection 中取一部分给新 endpoint
                let total = state.connections.len();
                let target = ((total + 1) / 2).max(1); // 至少一半
                let eid = endpoint_id.unwrap_or(0);
                let seg_count = state.segments.len();
                let mut moved = 0usize;
                for ci in 0..state.connections.len() {
                    if moved >= target {
                        break;
                    }
                    let seg_idx = state.connections[ci].current_segment;
                    state.connections[ci].endpoint = ip;
                    state.connections[ci].endpoint_id = eid;
                    if let Some(idx) = seg_idx {
                        if idx < seg_count {
                            state.segments[idx].assigned_endpoint = Some(ip);
                            state.segments[idx].assigned_endpoint_id = Some(eid);
                        }
                    }
                    moved += 1;
                }
                eprintln!(
                    "Enable endpoint {}: moved {} connections (all other endpoints disabled)",
                    ip, moved
                );
                drop(state);
                let _ = self.schedule_tx.send(());
                return;
            }

            // 只统计非 disabled endpoint 上的 connection 总数
            let total_active: usize = existing_eps.iter().map(|(_, _, c)| c).sum();

            // 计算新 endpoint 应收的连接数
            let total_eps = existing_eps.len() + 1;
            let per_ep = (total_active + total_eps - 1) / total_eps;
            let target_for_new = per_ep.max(1).min(total_active);

            let eid = endpoint_id.unwrap_or(0);
            let seg_count = state.segments.len();

            // 从每个现有 endpoint 平均取走连接
            let take_per_existing = target_for_new / existing_eps.len().max(1);
            let mut take_extra = target_for_new % existing_eps.len().max(1);

            let mut moved = 0usize;
            let mut seg_updates: Vec<(usize, IpAddr, usize)> = Vec::new();

            for (ep_ip, _, _) in &existing_eps {
                if moved >= target_for_new {
                    break;
                }
                let mut to_take = take_per_existing;
                if take_extra > 0 {
                    to_take += 1;
                    take_extra -= 1;
                }
                if to_take == 0 {
                    continue;
                }

                let mut taken_from_this = 0;
                for conn in state.connections.iter_mut() {
                    if moved >= target_for_new || taken_from_this >= to_take {
                        break;
                    }
                    if conn.endpoint == *ep_ip {
                        let seg_idx = conn.current_segment;
                        conn.endpoint = ip;
                        conn.endpoint_id = eid;
                        if let Some(idx) = seg_idx {
                            if idx < seg_count {
                                seg_updates.push((idx, ip, eid));
                            }
                        }
                        moved += 1;
                        taken_from_this += 1;
                    }
                }
            }
            for (idx, _ip, eid) in &seg_updates {
                state.segments[*idx].assigned_endpoint = Some(*_ip);
                state.segments[*idx].assigned_endpoint_id = Some(*eid);
            }

            eprintln!(
                "Enable endpoint {} (config_id: {:?}): moved {} connections from {} existing endpoint(s) (target: {} per endpoint)",
                ip, endpoint_id, moved, existing_eps.len(), per_ep
            );
        }

        drop(state);
        let _ = self.schedule_tx.send(());
    }

    /// 添加新 endpoint（不进行速度测试）
    /// 从其他 endpoint 平均取走连接，分配给新 endpoint
    pub async fn add_endpoint_no_test(&self, ip: IpAddr, endpoint_id: usize) {
        let mut state = self.state.lock().await;

        // 从 disabled 中移除，加入已知列表
        state.disabled_endpoints.remove(&ip);
        state.known_endpoints.insert(ip);

        // 检查是否已存在该 endpoint 的连接
        if state.connections.iter().any(|c| c.endpoint == ip) {
            drop(state);
            return;
        }

        // 收集现有 endpoint（不在 disabled 中，排除当前 IP）
        let existing_eps: Vec<(IpAddr, usize)> = {
            let mut map: HashMap<IpAddr, usize> = HashMap::new();
            for conn in state.connections.iter() {
                if conn.endpoint != ip && !state.disabled_endpoints.contains(&conn.endpoint) {
                    *map.entry(conn.endpoint).or_insert(0) += 1;
                }
            }
            map.into_iter().collect()
        };

        if existing_eps.is_empty() {
            // 所有其他 endpoint 都被 disabled，从全部 connection 中取一半给新 endpoint
            let total = state.connections.len();
            let target = ((total + 1) / 2).max(1);
            let seg_count = state.segments.len();
            let mut moved = 0usize;
            for ci in 0..state.connections.len() {
                if moved >= target {
                    break;
                }
                let seg_idx = state.connections[ci].current_segment;
                state.connections[ci].endpoint = ip;
                state.connections[ci].endpoint_id = endpoint_id;
                if let Some(idx) = seg_idx {
                    if idx < seg_count {
                        state.segments[idx].assigned_endpoint = Some(ip);
                        state.segments[idx].assigned_endpoint_id = Some(endpoint_id);
                    }
                }
                moved += 1;
            }
            eprintln!(
                "Add endpoint {}: moved {} connections (all other endpoints disabled)",
                ip, moved
            );
            drop(state);
            let _ = self.schedule_tx.send(());
            return;
        }

        // 只统计非 disabled endpoint 上的 connection 总数
        let total_active: usize = existing_eps.iter().map(|(_, c)| c).sum();

        // 计算新 endpoint 应收的连接数
        let total_eps = existing_eps.len() + 1;
        let per_ep = (total_active + total_eps - 1) / total_eps;
        let target_for_new = per_ep.max(1).min(total_active);

        let seg_count = state.segments.len();

        // 从每个现有 endpoint 平均取走连接
        let take_per_existing = target_for_new / existing_eps.len().max(1);
        let mut take_extra = target_for_new % existing_eps.len().max(1);

        let mut moved = 0usize;
        let mut seg_updates: Vec<(usize, IpAddr, usize)> = Vec::new();

        for (ep_ip, _) in &existing_eps {
            if moved >= target_for_new {
                break;
            }
            let mut to_take = take_per_existing;
            if take_extra > 0 {
                to_take += 1;
                take_extra -= 1;
            }
            if to_take == 0 {
                continue;
            }

            let mut taken_from_this = 0;
            for conn in state.connections.iter_mut() {
                if moved >= target_for_new || taken_from_this >= to_take {
                    break;
                }
                if conn.endpoint == *ep_ip {
                    let seg_idx = conn.current_segment;
                    conn.endpoint = ip;
                    conn.endpoint_id = endpoint_id;
                    if let Some(idx) = seg_idx {
                        if idx < seg_count {
                            seg_updates.push((idx, ip, endpoint_id));
                        }
                    }
                    moved += 1;
                    taken_from_this += 1;
                }
            }
        }
        for (idx, _ip, eid) in &seg_updates {
            state.segments[*idx].assigned_endpoint = Some(*_ip);
            state.segments[*idx].assigned_endpoint_id = Some(*eid);
        }

        eprintln!(
            "Add endpoint {} (config_id: {}): redistributed {} connections from {} existing endpoint(s) (target: {} per endpoint)",
            ip, endpoint_id, moved, existing_eps.len(), per_ep
        );

        // 将剩余 Pending segment 分配给任何有空闲的连接
        let pending_indices: Vec<usize> = state.segments.iter().enumerate()
            .filter(|(_, s)| s.status == SegmentStatus::Pending && s.assigned_endpoint.is_none())
            .map(|(i, _)| i)
            .collect();

        for seg_idx in pending_indices {
            if let Some(conn_idx) = state.connections.iter().position(|c| c.current_segment.is_none()) {
                state.segments[seg_idx].assigned_endpoint = Some(state.connections[conn_idx].endpoint);
                state.segments[seg_idx].assigned_endpoint_id = Some(state.connections[conn_idx].endpoint_id);
                state.connections[conn_idx].current_segment = Some(seg_idx);
            }
        }

        drop(state);
        let _ = self.schedule_tx.send(());
    }

    /// 添加新 endpoint（带速度测试）
    pub async fn add_endpoint(&self, ip: IpAddr, remote_file: &RemoteFile) -> Result<(), String> {
        // 先测试新 endpoint 的速度
        let url_info = crate::utils::parse_url(&remote_file.url)?;
        let speed_config = {
            let gcfg = crate::config::get_global_config().await;
            let download_proxy = remote_file.proxy_map.get("*").cloned();
            crate::endpoint_test::SpeedTestConfig::new(
                &url_info.host,
                url_info.ports.first().copied().unwrap_or(443),
                &url_info.path,
                url_info.https,
            )
            .timeout(std::time::Duration::from_secs(gcfg.speed_test_timeout_secs))
            .retry_count(gcfg.speed_test_retry_count)
            .proxy_url(download_proxy)
        };

        let speed_result = crate::endpoint_test::test_speed(ip, &speed_config).await;

        if !speed_result.success {
            return Err(format!("Endpoint {} speed test failed", ip));
        }

        // 使用统一的添加逻辑（重新分配现有连接而非创建僵尸连接）
        let endpoint_id = {
            let state = self.state.lock().await;
            state.connections.iter().map(|c| c.endpoint_id).max().unwrap_or(0) + 1
        };

        self.add_endpoint_no_test(ip, endpoint_id).await;

        Ok(())
    }

    /// 获取当前所有 endpoint 状态
    pub async fn get_endpoints(&self) -> Vec<EndpointStatus> {
        let state = self.state.lock().await;
        state.get_endpoint_status()
    }

    /// 暂停下载
    pub async fn pause(&self) {
        let state = self.state.lock().await;
        state.paused.store(true, Ordering::Relaxed);
    }

    /// 恢复下载
    pub async fn resume(&self) {
        let state = self.state.lock().await;
        state.paused.store(false, Ordering::Relaxed);
    }

    /// 取消下载
    pub async fn cancel(&self) {
        let state = self.state.lock().await;
        state.cancelled.store(true, Ordering::Relaxed);
    }

    /// 保存当前进度到状态文件
    pub async fn save_progress(&self) -> Result<(), String> {
        let state = self.state.lock().await;
        let segments = state.get_segment_progress();

        // 查找对应的 .rdown.json 文件
        let current_dir = std::env::current_dir()
            .map_err(|e| format!("Get current dir error: {}", e))?;

        let rdown_file = current_dir.join(format!("{}.{}.rdown.json", self.output_path, self.download_id));

        if rdown_file.exists() {
            let json = std::fs::read_to_string(&rdown_file)
                .map_err(|e| format!("Read rdown file error: {}", e))?;

            let mut info: crate::cli::state::DownloadInfo = serde_json::from_str(&json)
                .map_err(|e| format!("Parse rdown file error: {}", e))?;

            // 更新分片进度
            info.segments = segments.iter().map(|seg| crate::cli::state::SegmentProgress {
                start: seg.start,
                end: seg.end,
                downloaded: seg.downloaded,
                completed: seg.downloaded >= (seg.end - seg.start),
                endpoint_id: seg.endpoint_id,
            }).collect();

            info.downloaded = state.downloaded_bytes.load(Ordering::Relaxed);
            // 同步 connections 为实际分片数
            info.connections = info.segments.len();

            let json = serde_json::to_string_pretty(&info)
                .map_err(|e| format!("Serialize error: {}", e))?;

            std::fs::write(&rdown_file, json)
                .map_err(|e| format!("Write rdown file error: {}", e))?;
        }

        Ok(())
    }

    /// 等待下载完成（通过轮询状态检查）
    pub async fn wait(&mut self) -> Result<(), String> {
        // 启动重试监控任务
        let state_clone = self.state.clone();
        let remote_file_clone = self.remote_file.clone();
        let original_url = self.original_url.clone();
        let current_url = self.current_url.clone();

        let retry_task = tokio::spawn(async move {
            let retry_manager = super::retry::RetryManager::new(
                state_clone.clone(),
                original_url,
                current_url,
                remote_file_clone.clone(),
            );

            loop {
                tokio::time::sleep(std::time::Duration::from_secs(5)).await;

                // 检查是否所有 endpoint 都失败
                if retry_manager.all_endpoints_failed().await {
                    eprintln!("All endpoints failed, starting retry...");

                    match retry_manager.retry().await {
                        super::retry::RetryResult::Success(endpoint_list) => {
                            eprintln!("Retry successful, {} endpoints available", endpoint_list.endpoints.len());

                            let mut state = state_clone.lock().await;
                            let available_ips: Vec<std::net::IpAddr> = endpoint_list.endpoints.iter().map(|ep| ep.ip).collect();

                            if !available_ips.is_empty() {
                                // 清空 disabled_endpoints
                                state.disabled_endpoints.clear();

                                // 构建 IP → config endpoint_id 的映射
                                let ip_to_eid: HashMap<IpAddr, usize> = endpoint_list.endpoints.iter()
                                    .filter_map(|ep| ep.config_id.map(|id| (ep.ip, id)))
                                    .collect();

                                // 重新分配 endpoint
                                for (i, conn) in state.connections.iter_mut().enumerate() {
                                    let ip = available_ips[i % available_ips.len()];
                                    conn.endpoint = ip;
                                    conn.endpoint_id = ip_to_eid.get(&ip).copied().unwrap_or(i);
                                    conn.failure_count = 0;
                                }

                                // 重置所有非 Completed segment 为 Pending
                                for segment in state.segments.iter_mut() {
                                    if segment.status != SegmentStatus::Completed {
                                        segment.status = SegmentStatus::Pending;
                                        segment.downloaded = 0;
                                    }
                                }

                                let total_downloaded: u64 = state.segments.iter().map(|s| s.downloaded).sum();
                                state.downloaded_bytes.store(total_downloaded, std::sync::atomic::Ordering::Relaxed);

                                eprintln!("Endpoints reassigned, download will resume");
                            }
                        }
                        super::retry::RetryResult::Failed(e) => {
                            eprintln!("All retry steps failed: {}", e);

                            let mut state = state_clone.lock().await;
                            let current_connections = state.connections.len();

                            if current_connections > 1 {
                                eprintln!("Falling back to single connection download...");

                                state.connections.truncate(1);
                                state.connections[0].failure_count = 0;
                                state.connections[0].current_segment = None;

                                let total_downloaded: u64 = state.segments.iter()
                                    .filter(|s| s.status == SegmentStatus::Completed)
                                    .map(|s| s.downloaded)
                                    .sum();

                                let first_incomplete_start = state.segments.iter()
                                    .find(|s| s.status != SegmentStatus::Completed)
                                    .map(|s| s.start)
                                    .unwrap_or(state.total_bytes);

                                state.segments.clear();
                                if first_incomplete_start < state.total_bytes {
                                    let mut new_segment = Segment::new(first_incomplete_start, state.total_bytes);
                                    new_segment.downloaded = 0;
                                    new_segment.status = SegmentStatus::Pending;
                                    state.segments.push(new_segment);
                                    state.connections[0].current_segment = Some(0);
                                }

                                state.downloaded_bytes.store(total_downloaded, std::sync::atomic::Ordering::Relaxed);

                                eprintln!("Switched to single connection, resuming download...");
                            } else {
                                state.cancelled.store(true, std::sync::atomic::Ordering::Relaxed);
                                break;
                            }
                        }
                    }
                }

                // 检查是否完成或取消
                let state = state_clone.lock().await;
                if state.completed.load(Ordering::Relaxed) || state.cancelled.load(Ordering::Relaxed) {
                    break;
                }
            }
        });

        // Connection 任务由 tokio runtime 自行管理（fire-and-forget）
        // 通过轮询状态来感知下载完成
        loop {
            tokio::time::sleep(std::time::Duration::from_secs(1)).await;
            let state = self.state.lock().await;
            if state.completed.load(Ordering::Relaxed) || state.cancelled.load(Ordering::Relaxed) {
                break;
            }
        }

        // 停止重试监控任务
        retry_task.abort();

        // 最终保存一次进度
        let _ = self.save_progress().await;

        let state = self.state.lock().await;
        if state.completed.load(Ordering::Relaxed) {
            Ok(())
        } else if state.cancelled.load(Ordering::Relaxed) {
            Err("Download cancelled".to_string())
        } else {
            Err("Download failed".to_string())
        }
    }

    /// 获取进度信息
    pub async fn get_progress(&self) -> ProgressInfo {
        let state = self.state.lock().await;
        state.get_progress()
    }
}

/// 启动下载
pub async fn start_download(
    file_size: u64,
    output_path: &str,
    remote_file: &RemoteFile,
    endpoint_list: &EndpointList,
    download_id: &str,
    saved_segments: Option<Vec<crate::cli::state::SegmentProgress>>,
    saved_connections: Option<usize>,
    saved_total_endpoints: Option<usize>,
    original_url: &str,
    current_url: &str,
) -> Result<DownloadHandle, String> {
    // 获取可用的 endpoint 列表
    let available_endpoints: Vec<std::net::IpAddr> = endpoint_list
        .available()
        .into_iter()
        .map(|ep| ep.ip)
        .collect();

    // 如果没有可用的 endpoint，使用所有 endpoint
    let endpoints_to_use = if available_endpoints.is_empty() {
        endpoint_list.endpoints.iter().map(|ep| ep.ip).collect::<Vec<_>>()
    } else {
        available_endpoints
    };

    if endpoints_to_use.is_empty() {
        return Err("No available endpoints".to_string());
    }

    // 构建 IP → 配置 endpoint ID 的映射（用于保持显示与配置一致）
    let ip_to_config_id: std::collections::HashMap<std::net::IpAddr, usize> = endpoint_list
        .endpoints.iter()
        .filter_map(|ep| ep.config_id.map(|id| (ep.ip, id)))
        .collect();

    // 预解析每个 endpoint 的显示 ID：优先使用配置 ID，否则使用索引
    let endpoint_ids: Vec<usize> = endpoints_to_use.iter().enumerate()
        .map(|(i, ip)| ip_to_config_id.get(ip).copied().unwrap_or(i))
        .collect();

    // 检查服务器是否支持 Range 请求（从 speed test 响应头获取，大小写不敏感）
    let range_supported = remote_file
        .response_headers
        .iter()
        .find(|(k, _)| k.eq_ignore_ascii_case("accept-ranges"))
        .map(|(_, v)| v != "none")
        .unwrap_or(false);

    // 分配 segment 和 connection
    let mut allocation = if let Some(ref segments) = saved_segments {
        // 使用保存的分片信息
        let segments: Vec<Segment> = segments.iter().enumerate().map(|(i, seg)| {
            let mut segment = Segment::new(seg.start, seg.end);
            // 无 Content-Length 或不支持 Range 时不能断点续传，重置 downloaded 为 0
            if file_size == 0 || !range_supported {
                segment.downloaded = 0;
            } else {
                segment.downloaded = seg.downloaded;
            }
            if seg.completed {
                segment.status = SegmentStatus::Completed;
            }
            // 使用保存的 endpoint_id
            segment.assigned_endpoint_id = seg.endpoint_id;
            segment
        }).collect();

        // 使用保存的连接数（来自 task JSON），但不超过可用 endpoint 的合理上限
        // 无 Content-Length 或不支持 Range 时强制单连接
        let total_connections = if file_size == 0 || !range_supported {
            1
        } else {
            saved_connections
                .filter(|&n| n > 0)
                .unwrap_or(segments.len())
        };

        // 检查保存的 endpoint 配置是否与当前可用 endpoint 一致
        // 如果 endpoint 配置已变化（如某些 endpoint 被禁用），需要重新分配
        let saved_unique_endpoint_ids: std::collections::HashSet<usize> = segments.iter()
            .filter(|s| !s.is_completed())
            .filter_map(|s| s.assigned_endpoint_id)
            .collect();
        let unique_endpoint_count = saved_unique_endpoint_ids.len().max(1);

        let endpoint_config_changed = {
            // 检查保存的 endpoint_id 是否与当前可用的配置 ID 匹配
            let valid_config_ids: std::collections::HashSet<usize> = endpoint_ids.iter().copied().collect();
            let config_ids_mismatch = !valid_config_ids.is_empty()
                && saved_unique_endpoint_ids.iter().any(|id| !valid_config_ids.contains(id));

            if let Some(saved_total) = saved_total_endpoints {
                // 比较保存的端点总数与当前可用端点数量
                // 如果总数不同，说明有些端点被禁用/启用
                saved_total != endpoints_to_use.len()
                    || config_ids_mismatch
                    || unique_endpoint_count != endpoints_to_use.len()
                    || saved_unique_endpoint_ids.iter().any(|&id| id >= endpoints_to_use.len())
            } else {
                config_ids_mismatch
                    || unique_endpoint_count != endpoints_to_use.len()
                    || saved_unique_endpoint_ids.iter().any(|&id| id >= endpoints_to_use.len())
            }
        };

        // 如果连接数与分片数不一致，或者 endpoint 配置已变化，需要重新分配分片
        let (segments, connections) = if total_connections == segments.len() && !endpoint_config_changed {
            // 数量一致且 endpoint 配置未变化，直接使用保存的分片
            let connections: Vec<super::state::ConnectionInfo> = (0..total_connections)
                .map(|i| {
                    let endpoint = endpoints_to_use[i % endpoints_to_use.len()];
                    let endpoint_id = endpoint_ids[i % endpoint_ids.len()];
                    let mut conn = super::state::ConnectionInfo::new(i, endpoint, endpoint_id);
                    conn.current_segment = Some(i);
                    conn
                })
                .collect();

            (segments, connections)
        } else if total_connections == segments.len() && endpoint_config_changed {
            // 数量一致但 endpoint 配置已变化（如某些 endpoint 被禁用）
            // 保留 segment 的 start/end/downloaded，重新分配 endpoint_id
            let mut segments = segments;
            for (i, segment) in segments.iter_mut().enumerate() {
                if !segment.is_completed() {
                    segment.assigned_endpoint_id = Some(endpoint_ids[i % endpoint_ids.len()]);
                }
            }

            let connections: Vec<super::state::ConnectionInfo> = (0..total_connections)
                .map(|i| {
                    let endpoint = endpoints_to_use[i % endpoints_to_use.len()];
                    let endpoint_id = endpoint_ids[i % endpoint_ids.len()];
                    let mut conn = super::state::ConnectionInfo::new(i, endpoint, endpoint_id);
                    conn.current_segment = Some(i);
                    conn
                })
                .collect();

            (segments, connections)
        } else {
            // 数量不一致，重新分配（基于已下载字节数和剩余大小）
            let downloaded_bytes: u64 = segments.iter().map(|s| s.downloaded).sum();
            let remaining = file_size.saturating_sub(downloaded_bytes);

            let mut connections: Vec<super::state::ConnectionInfo> = (0..total_connections)
                .map(|i| {
                    let endpoint = endpoints_to_use[i % endpoints_to_use.len()];
                    let endpoint_id = endpoint_ids[i % endpoint_ids.len()];
                    super::state::ConnectionInfo::new(i, endpoint, endpoint_id)
                })
                .collect();

            let segment_size = remaining / total_connections as u64;
            let mut new_segments = Vec::new();

            for i in 0..total_connections {
                let start = downloaded_bytes + i as u64 * segment_size;
                let end = if i == total_connections - 1 {
                    file_size
                } else {
                    downloaded_bytes + (i as u64 + 1) * segment_size
                };
                new_segments.push(Segment::new(start, end));
            }

            // 分配 segment 给 connection（一对一映射）
            for (i, conn) in connections.iter_mut().enumerate() {
                if i < new_segments.len() {
                    conn.current_segment = Some(i);
                }
            }

            (new_segments, connections)
        };

        allocator::AllocationResult {
            segments,
            connections,
        }
    } else {
        // 没有保存的分片，使用保存的连接数或全局配置
        let mut allocation = allocator::allocate(file_size, endpoint_list, range_supported).await;

        // 如果有保存的连接数，调整分配
        // 但不支持 Range 时跳过，保持单连接
        if range_supported {
            if let Some(saved_conn) = saved_connections {
                let effective_conn = saved_conn;
                if effective_conn != allocation.connections.len() {
                // 重新创建连接列表，均匀分配 endpoint
                let mut connections: Vec<super::state::ConnectionInfo> = (0..effective_conn)
                    .map(|i| {
                        let endpoint = endpoints_to_use[i % endpoints_to_use.len()];
                        let endpoint_id = endpoint_ids[i % endpoint_ids.len()];
                        super::state::ConnectionInfo::new(i, endpoint, endpoint_id)
                    })
                    .collect();

                // 重新创建分片
                let segment_size = file_size / effective_conn as u64;
                let mut segments = Vec::new();
                for i in 0..effective_conn {
                    let start = i as u64 * segment_size;
                    let end = if i == effective_conn - 1 {
                        file_size
                    } else {
                        (i as u64 + 1) * segment_size
                    };
                    segments.push(Segment::new(start, end));
                }

                // 分配 segment 给 connection（一对一映射）
                for (i, conn) in connections.iter_mut().enumerate() {
                    if i < segments.len() {
                        conn.current_segment = Some(i);
                    }
                }

                allocation = allocator::AllocationResult {
                    segments,
                    connections,
                };
            }
        }
        }

        allocation
    };

    // 统一 endpoint_id：allocator 使用的是数组下标，这里统一映射为 config ID
    for conn in &mut allocation.connections {
        if let Some(&config_id) = ip_to_config_id.get(&conn.endpoint) {
            conn.endpoint_id = config_id;
        }
    }
    for segment in &mut allocation.segments {
        if let Some(ip) = segment.assigned_endpoint {
            if let Some(&config_id) = ip_to_config_id.get(&ip) {
                segment.assigned_endpoint_id = Some(config_id);
            }
        }
    }

    if allocation.connections.is_empty() {
        return Err("No available endpoints".to_string());
    }

    // 检查是否需要创建文件
    let file_exists = tokio::fs::metadata(output_path).await.is_ok();

    let file = if file_exists {
        // 无 Content-Length 或不支持 Range → 不能续传，截断文件从头开始
        if file_size == 0 || !range_supported {
            tokio::fs::File::create(output_path)
                .await
                .map_err(|e| format!("Truncate file error: {}", e))?
        } else {
            // 文件已存在，以读写模式打开（支持断点续传）
            tokio::fs::OpenOptions::new()
                .read(true)
                .write(true)
                .open(output_path)
                .await
                .map_err(|e| format!("Open file error: {}", e))?
        }
    } else {
        // 创建新文件
        let file = tokio::fs::File::create(output_path)
            .await
            .map_err(|e| format!("Create file error: {}", e))?;

        // 预分配文件大小
        file.set_len(file_size)
            .await
            .map_err(|e| format!("Set file size error: {}", e))?;

        file
    };

    let file = Arc::new(Mutex::new(file));

    // 计算已下载的总字节数
    let downloaded_bytes: u64 = allocation.segments.iter().map(|seg| seg.downloaded).sum();

    // 设置每个 segment 的 assigned_endpoint，确保 disable_endpoint 和 scheduler
    // 能根据 IP 找到属于被禁用 endpoint 的 segment
    for conn in &allocation.connections {
        if let Some(seg_idx) = conn.current_segment {
            if seg_idx < allocation.segments.len() {
                allocation.segments[seg_idx].assigned_endpoint = Some(conn.endpoint);
                allocation.segments[seg_idx].assigned_endpoint_id = Some(conn.endpoint_id);
            }
        }
    }

    // 创建下载状态
    let state = Arc::new(Mutex::new(DownloadState {
        total_bytes: file_size,
        downloaded_bytes: std::sync::atomic::AtomicU64::new(downloaded_bytes),
        segments: allocation.segments,
        connections: allocation.connections,
        disabled_endpoints: HashSet::new(),
        known_endpoints: endpoints_to_use.iter().cloned().collect(),
        paused: std::sync::atomic::AtomicBool::new(false),
        cancelled: std::sync::atomic::AtomicBool::new(false),
        completed: std::sync::atomic::AtomicBool::new(false),
        range_supported: std::sync::atomic::AtomicBool::new(range_supported),
    }));

    // 创建调度通知 channel
    let (schedule_tx, schedule_rx) = watch::channel(());

    // 创建共享的 RemoteFile
    let remote_file = Arc::new(Mutex::new(remote_file.clone()));

    // 启动 connection 任务（fire-and-forget，由 tokio runtime 管理）
    let connections = {
        let state_guard = state.lock().await;
        state_guard.connections.clone()
    };

    let mut tasks = Vec::with_capacity(connections.len());
    for conn in connections {
        let mut runner = ConnectionRunner {
            id: conn.id,
            endpoint: conn.endpoint,
            file: file.clone(),
            state: state.clone(),
            schedule_rx: schedule_rx.clone(),
            remote_file: remote_file.clone(),
        };

        tasks.push(tokio::spawn(async move {
            runner.run().await;
        }));
    }

    // 启动调度器（传入 file 和 remote_file 以便动态生成 ConnectionRunner）
    let scheduler_config = {
        let config = GLOBAL_CONFIG.read().await;
        super::scheduler::SchedulerConfig {
            check_interval: std::time::Duration::from_secs(config.scheduler_check_interval_secs),
        }
    };
    let scheduler = super::scheduler::Scheduler::new(
        state.clone(),
        schedule_tx.clone(),
        scheduler_config,
        file.clone(),
        remote_file.clone(),
    );

    tokio::spawn(async move {
        scheduler.run().await;
    });

    Ok(DownloadHandle::new(
        state,
        schedule_tx,
        tasks,
        output_path.to_string(),
        download_id.to_string(),
        original_url.to_string(),
        current_url.to_string(),
        remote_file.clone(),
        file.clone(),
    ).await)
}
