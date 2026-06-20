use std::collections::HashMap;
use std::net::IpAddr;
use std::sync::Arc;
use std::sync::atomic::Ordering;
use std::time::Duration;

use tokio::sync::Mutex;
use tokio::sync::watch;

use crate::config::GLOBAL_CONFIG;
use crate::remote_file::RemoteFile;

use super::allocator::calculate_connection_counts;
use super::connection::ConnectionRunner;
use super::state::{Segment, SegmentStatus, SharedDownloadState};

/// 调度器配置
#[derive(Debug, Clone)]
pub struct SchedulerConfig {
    /// 检查间隔
    pub check_interval: Duration,
}

impl Default for SchedulerConfig {
    fn default() -> Self {
        Self {
            check_interval: Duration::from_secs(3),
        }
    }
}

/// 调度器
pub struct Scheduler {
    /// 下载状态
    state: SharedDownloadState,
    /// 调度变更通知发送端
    schedule_tx: watch::Sender<()>,
    /// 配置（检查间隔等固定参数）
    config: SchedulerConfig,
    /// 文件句柄（用于生成新 ConnectionRunner）
    file: Arc<Mutex<tokio::fs::File>>,
    /// RemoteFile 配置（用于生成新 ConnectionRunner）
    remote_file: Arc<Mutex<RemoteFile>>,
    /// 上次记录的 total_connections（用于检测配置变化）
    last_total_connections: Mutex<usize>,
    /// 上次每个 endpoint 的连接数（用于检测分配变化是否超过阈值）
    last_endpoint_conn_counts: Mutex<HashMap<IpAddr, usize>>,
}

impl Scheduler {
    pub fn new(
        state: SharedDownloadState,
        schedule_tx: watch::Sender<()>,
        config: SchedulerConfig,
        file: Arc<Mutex<tokio::fs::File>>,
        remote_file: Arc<Mutex<RemoteFile>>,
    ) -> Self {
        Self {
            state,
            schedule_tx,
            config,
            file,
            remote_file,
            last_total_connections: Mutex::new(0),
            last_endpoint_conn_counts: Mutex::new(HashMap::new()),
        }
    }

    /// 运行调度器
    pub async fn run(&self) {
        // 记录初始连接数
        {
            let state = self.state.lock().await;
            *self.last_total_connections.lock().await = state.connections.len();
        }

        loop {
            tokio::time::sleep(self.config.check_interval).await;

            // 检查是否完成或取消
            {
                let state = self.state.lock().await;
                if state.completed.load(Ordering::Relaxed)
                    || state.cancelled.load(Ordering::Relaxed)
                {
                    return;
                }
            }

            // 读取最新配置
            let (total_connections, slow_threshold, reallocate_threshold) = {
                let cfg = GLOBAL_CONFIG.read().await;
                (
                    cfg.total_connections,
                    cfg.scheduler_slow_threshold,
                    cfg.scheduler_reallocate_threshold,
                )
            };

            // 1. 处理 total_connections 变化（set-config connections 命令）
            self.adjust_connection_count(total_connections).await;

            // 2. 计算各 endpoint 速度
            let endpoint_speeds = self.get_endpoint_speeds().await;
            if endpoint_speeds.is_empty() {
                continue;
            }

            // 3. 基于速度重新分配连接
            self.rebalance_connections(
                &endpoint_speeds,
                total_connections,
                slow_threshold,
                reallocate_threshold,
            )
            .await;
        }
    }

    /// 根据配置的 total_connections 调整连接和 segment 数量
    async fn adjust_connection_count(&self, target_count: usize) {
        if target_count == 0 {
            return;
        }

        let mut state = self.state.lock().await;

        // 无 Content-Length 时不调整连接数，避免破坏单连接下载
        if state.total_bytes == 0 {
            return;
        }

        // 服务器不支持 Range 时强制保持单连接（多连接无意义）
        if !state.range_supported.load(Ordering::Relaxed) {
            return;
        }

        let current_count = state.connections.len();

        if current_count == target_count {
            *self.last_total_connections.lock().await = current_count;
            return;
        }

        if current_count < target_count {
            // 需要增加连接
            let add_count = target_count - current_count;
            eprintln!(
                "Scheduler: total_connections changed {} -> {}, adding {} connection(s)",
                current_count, target_count, add_count
            );

            let file_size = state.total_bytes;
            let downloaded: u64 = state.segments.iter().map(|s| s.downloaded).sum();
            let remaining = file_size.saturating_sub(downloaded);

            // 获取当前 endpoint 列表（只取非 disabled），保留正确的 endpoint_id
            let mut endpoints: Vec<(IpAddr, usize)> = Vec::new();
            let mut seen = std::collections::HashSet::new();
            for conn in state.connections.iter() {
                if !state.disabled_endpoints.contains(&conn.endpoint) && seen.insert(conn.endpoint)
                {
                    endpoints.push((conn.endpoint, conn.endpoint_id));
                }
            }
            // 全部被禁用时，不创建新连接（等 enable-endpoint 或 retry）
            if endpoints.is_empty() {
                return;
            }

            if endpoints.is_empty() {
                return;
            }

            // 重新计算所有连接的分片（基于新的总数）
            let segment_size = remaining / target_count as u64;
            let mut new_segments: Vec<Segment> = Vec::new();

            for i in 0..target_count {
                let start = downloaded + i as u64 * segment_size;
                let end = if i == target_count - 1 {
                    file_size
                } else {
                    downloaded + (i as u64 + 1) * segment_size
                };
                new_segments.push(Segment::new(start, end));
            }

            // 保留已完成分片的下载进度
            let old_segments = std::mem::replace(&mut state.segments, new_segments);
            for old_seg in &old_segments {
                if old_seg.status == SegmentStatus::Completed || old_seg.downloaded > 0 {
                    // 将旧进度映射到新分片
                    let old_end = old_seg.start + old_seg.downloaded;
                    for new_seg in state.segments.iter_mut() {
                        if new_seg.start < old_end && new_seg.end > old_seg.start {
                            // 重叠区域
                            let overlap_start = new_seg.start.max(old_seg.start);
                            let overlap_end = new_seg.end.min(old_end);
                            if overlap_end > overlap_start {
                                new_seg.downloaded = overlap_end - new_seg.start;
                                if new_seg.downloaded >= new_seg.size() {
                                    new_seg.status = SegmentStatus::Completed;
                                }
                            }
                        }
                    }
                }
            }

            // 重建连接列表
            let mut new_connections: Vec<super::state::ConnectionInfo> = (0..target_count)
                .map(|i| {
                    let (ip, eid) = endpoints[i % endpoints.len()];
                    super::state::ConnectionInfo::new(i, ip, eid)
                })
                .collect();

            // 分配 segment ↔ connection
            for (i, conn) in new_connections.iter_mut().enumerate() {
                if i < state.segments.len() {
                    conn.current_segment = Some(i);
                    state.segments[i].assigned_endpoint = Some(conn.endpoint);
                    state.segments[i].assigned_endpoint_id = Some(conn.endpoint_id);
                }
            }

            state.connections = new_connections;

            // 更新 downloaded_bytes
            let total_downloaded: u64 = state.segments.iter().map(|s| s.downloaded).sum();
            state
                .downloaded_bytes
                .store(total_downloaded, Ordering::Relaxed);

            // 释放锁以便 spawn 新 runner
            let conns_to_spawn: Vec<(usize, IpAddr, usize)> = (current_count..target_count)
                .map(|i| {
                    let ip = endpoints[i % endpoints.len()].0;
                    let eid = endpoints[i % endpoints.len()].1;
                    (i, ip, eid)
                })
                .collect();
            drop(state);

            // 为新增的连接创建 ConnectionRunner
            for (id, endpoint, _endpoint_id) in &conns_to_spawn {
                let mut runner = ConnectionRunner {
                    id: *id,
                    endpoint: *endpoint,
                    file: self.file.clone(),
                    state: self.state.clone(),
                    schedule_rx: self.schedule_tx.subscribe(),
                    remote_file: self.remote_file.clone(),
                };
                tokio::spawn(async move {
                    runner.run().await;
                });
            }

            eprintln!(
                "Scheduler: spawned {} new connection runner(s)",
                conns_to_spawn.len()
            );
        } else {
            // 需要减少连接
            let remove_count = current_count - target_count;
            eprintln!(
                "Scheduler: total_connections changed {} -> {}, removing {} connection(s)",
                current_count, target_count, remove_count
            );

            // 释放多余连接的 segment
            let seg_count = state.segments.len();
            let pending_seg_indices: Vec<usize> = {
                let mut indices = Vec::new();
                for conn in state.connections.iter_mut().skip(target_count) {
                    if let Some(seg_idx) = conn.current_segment.take() {
                        if seg_idx < seg_count {
                            indices.push(seg_idx);
                        }
                    }
                }
                indices
            };
            for seg_idx in &pending_seg_indices {
                state.segments[*seg_idx].status = SegmentStatus::Pending;
                state.segments[*seg_idx].assigned_endpoint = None;
            }

            // 重新创建 segment 以匹配新的连接数
            let file_size = state.total_bytes;
            let downloaded: u64 = state.segments.iter().map(|s| s.downloaded).sum();
            let remaining = file_size.saturating_sub(downloaded);
            let segment_size = remaining / target_count as u64;

            let old_segments = std::mem::take(&mut state.segments);
            let mut new_segments: Vec<Segment> = Vec::new();

            for i in 0..target_count {
                let start = downloaded + i as u64 * segment_size;
                let end = if i == target_count - 1 {
                    file_size
                } else {
                    downloaded + (i as u64 + 1) * segment_size
                };
                new_segments.push(Segment::new(start, end));
            }

            // 映射旧进度到新分片
            for old_seg in &old_segments {
                if old_seg.downloaded > 0 {
                    let old_end = old_seg.start + old_seg.downloaded;
                    for new_seg in new_segments.iter_mut() {
                        if new_seg.start < old_end && new_seg.end > old_seg.start {
                            let overlap_start = new_seg.start.max(old_seg.start);
                            let overlap_end = new_seg.end.min(old_end);
                            if overlap_end > overlap_start {
                                new_seg.downloaded = overlap_end - new_seg.start;
                                if new_seg.downloaded >= new_seg.size() {
                                    new_seg.status = SegmentStatus::Completed;
                                }
                            }
                        }
                    }
                }
            }

            state.segments = new_segments;

            // 截断连接列表并重新分配 segment
            state.connections.truncate(target_count);

            // 收集需要赋值的 segment 数据（避免同时 borrow connections 和 segments）
            let assignments: Vec<(usize, Option<IpAddr>, Option<usize>)> = state
                .connections
                .iter()
                .enumerate()
                .map(|(i, conn)| {
                    if i < state.segments.len() {
                        (i, Some(conn.endpoint), Some(conn.endpoint_id))
                    } else {
                        (i, None, None)
                    }
                })
                .collect();

            for (i, endpoint, endpoint_id) in &assignments {
                let conn = &mut state.connections[*i];
                conn.id = *i;
                if let (Some(ep), Some(eid)) = (*endpoint, *endpoint_id) {
                    conn.current_segment = Some(*i);
                    state.segments[*i].assigned_endpoint = Some(ep);
                    state.segments[*i].assigned_endpoint_id = Some(eid);
                } else {
                    conn.current_segment = None;
                }
            }

            let total_downloaded: u64 = state.segments.iter().map(|s| s.downloaded).sum();
            state
                .downloaded_bytes
                .store(total_downloaded, Ordering::Relaxed);
        }

        *self.last_total_connections.lock().await = target_count;

        // 通知所有 runner 重新调度
        let _ = self.schedule_tx.send(());
    }

    /// 获取每个 endpoint 的平均速度
    async fn get_endpoint_speeds(&self) -> HashMap<IpAddr, f64> {
        let state = self.state.lock().await;
        let mut speeds: HashMap<IpAddr, Vec<f64>> = HashMap::new();

        for conn in &state.connections {
            if conn.speed > 0.0 {
                speeds.entry(conn.endpoint).or_default().push(conn.speed);
            } else {
                // 速度为 0 的连接也记录（可能是刚启动的）
                speeds.entry(conn.endpoint).or_default().push(0.0);
            }
        }

        // 计算每个 endpoint 的平均速度
        speeds
            .into_iter()
            .map(|(ip, vals)| {
                let sum: f64 = vals.iter().sum();
                let avg = if vals.is_empty() {
                    0.0
                } else {
                    sum / vals.len() as f64
                };
                (ip, avg)
            })
            .collect()
    }

    /// 基于速度重新分配连接
    async fn rebalance_connections(
        &self,
        speeds: &HashMap<IpAddr, f64>,
        total_connections: usize,
        slow_threshold: f64,
        reallocate_threshold: f64,
    ) {
        if speeds.len() <= 1 {
            return;
        }

        let mut state = self.state.lock().await;

        // 无 Content-Length 时不重新分配，避免破坏单连接下载
        if state.total_bytes == 0 {
            drop(state);
            return;
        }

        // 收集所有 endpoint（排除 disabled 的）及其速度
        let active_endpoints: Vec<(IpAddr, f64)> = {
            let mut eps: Vec<(IpAddr, f64)> = speeds
                .iter()
                .filter(|(ip, _)| !state.disabled_endpoints.contains(ip))
                .map(|(ip, speed)| (*ip, *speed))
                .collect();
            // 包含有连接但没速度数据的 endpoint
            let known_ips: std::collections::HashSet<IpAddr> =
                eps.iter().map(|(ip, _)| *ip).collect();
            for conn in &state.connections {
                if !state.disabled_endpoints.contains(&conn.endpoint)
                    && !known_ips.contains(&conn.endpoint)
                {
                    eps.push((conn.endpoint, 0.0));
                }
            }
            // 包含已知但 0 connection 的启用 endpoint
            for ip in &state.known_endpoints {
                if !state.disabled_endpoints.contains(ip) && !known_ips.contains(ip) {
                    eps.push((*ip, 0.0));
                }
            }
            eps
        };

        if active_endpoints.len() <= 1 {
            drop(state);
            return;
        }

        // 计算平均速度
        let total_speed: f64 = active_endpoints.iter().map(|(_, s)| *s).sum();
        let avg_speed = total_speed / active_endpoints.len() as f64;

        // 检测慢节点：低于平均速度 × slow_threshold
        let slow_ips: std::collections::HashSet<IpAddr> = active_endpoints
            .iter()
            .filter(|(_, speed)| *speed > 0.0 && *speed < avg_speed * slow_threshold)
            .map(|(ip, _)| *ip)
            .collect();

        // 如果所有 endpoint 都慢，不处理
        let all_slow = !slow_ips.is_empty() && slow_ips.len() >= active_endpoints.len();
        if all_slow {
            drop(state);
            return;
        }

        // 构建用于计算 target_counts 的 endpoint 列表（排除慢节点）
        let effective_endpoints: Vec<(IpAddr, f64)> = if slow_ips.is_empty() {
            active_endpoints.clone()
        } else {
            active_endpoints
                .iter()
                .filter(|(ip, _)| !slow_ips.contains(ip))
                .map(|(ip, speed)| (*ip, *speed))
                .collect()
        };

        if effective_endpoints.is_empty() {
            drop(state);
            return;
        }

        // 使用 allocator 的函数计算每个 endpoint 应得的连接数
        let target_counts = calculate_connection_counts(
            &effective_endpoints,
            total_connections,
            2.0, // 始终按速度比例分配
        );

        // 计算当前每个 endpoint 的连接数
        let mut current_counts: HashMap<IpAddr, usize> = HashMap::new();
        for ip in effective_endpoints.iter().map(|(ip, _)| *ip) {
            let count = state
                .connections
                .iter()
                .filter(|c| c.endpoint == ip)
                .count();
            current_counts.insert(ip, count);
        }

        // 检查变化是否超过 reallocate_threshold
        let last_counts = self.last_endpoint_conn_counts.lock().await.clone();
        let total_diff: f64 = target_counts
            .iter()
            .map(|(ip, target)| {
                let current = current_counts.get(ip).copied().unwrap_or(0);
                let last = last_counts.get(ip).copied().unwrap_or(current);
                let base = (current.max(last) as f64).max(1.0);
                let diff = if *target > current {
                    (*target - current) as f64
                } else {
                    (current - *target) as f64
                };
                diff / base
            })
            .sum();
        let disabled_conn_count: usize = state
            .connections
            .iter()
            .filter(|c| state.disabled_endpoints.contains(&c.endpoint))
            .count();

        let should_reallocate = !last_counts.is_empty()
            && total_diff > reallocate_threshold * target_counts.len() as f64;

        if !should_reallocate && slow_ips.is_empty() && disabled_conn_count == 0 {
            let mut new_last = self.last_endpoint_conn_counts.lock().await;
            *new_last = current_counts;
            drop(state);
            return;
        }

        if !slow_ips.is_empty() {
            eprintln!(
                "Scheduler: migrating connections from {} slow endpoint(s): {:?}",
                slow_ips.len(),
                slow_ips
            );
        }
        if should_reallocate {
            eprintln!(
                "Scheduler: rebalancing (diff {:.2} > threshold {:.2})",
                total_diff,
                reallocate_threshold * target_counts.len() as f64
            );
        }
        // 执行重新分配：找到需要增加连接的 endpoint 和需要减少的 endpoint
        let mut surplus: Vec<(IpAddr, usize)> = Vec::new();
        let mut deficit: Vec<(IpAddr, usize)> = Vec::new();

        for (ip, target) in &target_counts {
            let current = current_counts.get(ip).copied().unwrap_or(0);
            if current > *target {
                surplus.push((*ip, current - *target));
            } else if current < *target {
                deficit.push((*ip, *target - current));
            }
        }

        // 构建 IP → endpoint_id 的查找表
        let ip_to_eid: HashMap<IpAddr, usize> = state
            .connections
            .iter()
            .map(|c| (c.endpoint, c.endpoint_id))
            .collect();

        // 先迁移 disabled endpoint 上的连接
        let seg_count = state.segments.len();
        let mut remaining_disabled = disabled_conn_count;
        if remaining_disabled > 0 {
            for (need_ip, need_count) in deficit.iter_mut() {
                if remaining_disabled == 0 {
                    break;
                }
                let take = (*need_count).min(remaining_disabled);
                let target_eid = ip_to_eid.get(need_ip).copied().unwrap_or(0);
                let mut moved = 0usize;
                for ci in 0..state.connections.len() {
                    if moved >= take {
                        break;
                    }
                    if state
                        .disabled_endpoints
                        .contains(&state.connections[ci].endpoint)
                    {
                        let seg_idx = state.connections[ci].current_segment;
                        state.connections[ci].endpoint = *need_ip;
                        state.connections[ci].endpoint_id = target_eid;
                        state.connections[ci].failure_count = 0;
                        if let Some(idx) = seg_idx {
                            if idx < seg_count {
                                state.segments[idx].assigned_endpoint = Some(*need_ip);
                                state.segments[idx].assigned_endpoint_id = Some(target_eid);
                            }
                        }
                        moved += 1;
                    }
                }
                *need_count -= moved;
                remaining_disabled -= moved;
            }
        }

        // 将剩余多余连接迁移到不足的 endpoint
        for (need_ip, need_count) in &deficit {
            let target_eid = ip_to_eid.get(need_ip).copied().unwrap_or(0);
            let mut remaining = *need_count;
            for (surplus_ip, surplus_count) in surplus.iter_mut() {
                if remaining == 0 {
                    break;
                }
                let take = remaining.min(*surplus_count);
                if take > 0 {
                    let mut moved = 0usize;
                    for ci in 0..state.connections.len() {
                        if moved >= take {
                            break;
                        }
                        if state.connections[ci].endpoint == *surplus_ip {
                            let seg_idx = state.connections[ci].current_segment;
                            state.connections[ci].endpoint = *need_ip;
                            state.connections[ci].endpoint_id = target_eid;
                            if let Some(idx) = seg_idx {
                                if idx < seg_count {
                                    state.segments[idx].assigned_endpoint = Some(*need_ip);
                                    state.segments[idx].assigned_endpoint_id = Some(target_eid);
                                }
                            }
                            moved += 1;
                        }
                    }
                    remaining -= moved;
                    *surplus_count -= moved;
                }
            }
        }

        // 慢节点：迁移所有连接
        if !slow_ips.is_empty() {
            for slow_ip in &slow_ips {
                // 找到仍在使用慢节点的连接，迁移到最快的有效 endpoint
                for ci in 0..state.connections.len() {
                    if state.connections[ci].endpoint == *slow_ip {
                        if let Some((fast_ip, _)) = effective_endpoints.first() {
                            let target_eid = ip_to_eid.get(fast_ip).copied().unwrap_or(0);
                            let seg_idx = state.connections[ci].current_segment;
                            state.connections[ci].endpoint = *fast_ip;
                            state.connections[ci].endpoint_id = target_eid;
                            state.connections[ci].failure_count = 0;
                            if let Some(idx) = seg_idx {
                                if idx < seg_count {
                                    state.segments[idx].assigned_endpoint = Some(*fast_ip);
                                    state.segments[idx].assigned_endpoint_id = Some(target_eid);
                                }
                            }
                        }
                    }
                }
                // 检查旧 endpoint 是否还有连接
                let still_has = state.connections.iter().any(|c| c.endpoint == *slow_ip);
                if !still_has {
                    state.disabled_endpoints.insert(*slow_ip);
                }
            }
            eprintln!(
                "Scheduler: migrated connections from {} slow endpoint(s)",
                slow_ips.len()
            );
        }

        // 更新记录
        let new_counts: HashMap<IpAddr, usize> =
            state.connections.iter().fold(HashMap::new(), |mut acc, c| {
                *acc.entry(c.endpoint).or_default() += 1;
                acc
            });
        *self.last_endpoint_conn_counts.lock().await = new_counts;

        drop(state);

        // 通知所有 runner 重新调度
        let _ = self.schedule_tx.send(());
    }
}
