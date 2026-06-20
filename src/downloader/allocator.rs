use std::net::IpAddr;

use crate::config::GLOBAL_CONFIG;
use crate::endpoint_test::EndpointList;

use super::state::{ConnectionInfo, Segment};

/// 分配结果
pub struct AllocationResult {
    pub segments: Vec<Segment>,
    pub connections: Vec<ConnectionInfo>,
}

/// 根据 endpoint 质量分配 connection 和 segment
pub async fn allocate(file_size: u64, endpoint_list: &EndpointList, range_supported: bool) -> AllocationResult {
    let config = GLOBAL_CONFIG.read().await;
    // 无 Content-Length 或不支持 Range 时强制单连接（多连接无意义）
    let total_connections = if file_size == 0 || !range_supported { 1 } else { config.total_connections };
    let speed_threshold = config.speed_threshold;
    drop(config);

    // 获取可用的 endpoint 及其速度
    let available_endpoints: Vec<(IpAddr, f64)> = endpoint_list
        .available()
        .into_iter()
        .filter_map(|ep| {
            ep.speed_bps().map(|speed| (ep.ip, speed))
        })
        .collect();

    // 如果没有带速度信息的可用 endpoint，尝试使用所有可用 endpoint（不考虑速度）
    let endpoints_to_use = if available_endpoints.is_empty() {
        let all_available: Vec<(IpAddr, f64)> = endpoint_list
            .available()
            .into_iter()
            .map(|ep| (ep.ip, 1.0))  // 使用默认速度 1.0
            .collect();

        if all_available.is_empty() {
            // 如果没有任何"可用"的 endpoint，使用所有 endpoint 作为回退
            // 这可能是因为所有测试都失败了，但 DNS 解析成功了
            let all_endpoints: Vec<(IpAddr, f64)> = endpoint_list
                .endpoints
                .iter()
                .map(|ep| (ep.ip, 1.0))
                .collect();

            if all_endpoints.is_empty() {
                return AllocationResult {
                    segments: Vec::new(),
                    connections: Vec::new(),
                };
            }
            all_endpoints
        } else {
            all_available
        }
    } else {
        available_endpoints
    };

    // 计算每个 endpoint 分配的 connection 数量
    let connection_counts = calculate_connection_counts(
        &endpoints_to_use,
        total_connections,
        speed_threshold,
    );

    // 创建 connection 列表
    let mut connections = Vec::new();
    let mut connection_id = 0;

    for (endpoint_id, (ip, count)) in connection_counts.iter().enumerate() {
        for _ in 0..*count {
            connections.push(ConnectionInfo::new(connection_id, *ip, endpoint_id));
            connection_id += 1;
        }
    }

    // 创建 segment 列表（按 connection 数量等分）
    let actual_connections = connections.len();
    let mut segments = Vec::new();

    if file_size == 0 {
        // 文件大小未知，创建单个 segment，end 设置为最大值
        // 实际大小会在下载时从 Content-Length 获取
        let mut segment = Segment::new(0, u64::MAX);
        if let Some(conn) = connections.first() {
            segment.assigned_endpoint_id = Some(conn.endpoint_id);
        }
        segments.push(segment);
    } else {
        let segment_size = file_size / actual_connections as u64;

        for i in 0..actual_connections {
            let start = i as u64 * segment_size;
            let end = if i == actual_connections - 1 {
                file_size
            } else {
                (i as u64 + 1) * segment_size
            };
            let mut segment = Segment::new(start, end);
            // 设置 endpoint ID
            if let Some(conn) = connections.get(i) {
                segment.assigned_endpoint_id = Some(conn.endpoint_id);
            }
            segments.push(segment);
        }
    }

    // 分配 segment 给 connection（一对一映射）
    for (i, conn) in connections.iter_mut().enumerate() {
        if i < segments.len() {
            conn.current_segment = Some(i);
        }
    }

    AllocationResult {
        segments,
        connections,
    }
}

/// 计算每个 endpoint 分配的 connection 数量
pub(super) fn calculate_connection_counts(
    endpoints: &[(IpAddr, f64)],
    total_connections: usize,
    speed_threshold: f64,
) -> Vec<(IpAddr, usize)> {
    if endpoints.is_empty() {
        return Vec::new();
    }

    if endpoints.len() == 1 {
        return vec![(endpoints[0].0, total_connections)];
    }

    // 计算速度比率
    let max_speed = endpoints.iter().map(|(_, s)| *s).fold(0.0f64, f64::max);
    let min_speed = endpoints.iter().map(|(_, s)| *s).fold(f64::MAX, f64::min);

    // 有节点速度为 0（刚启用还没数据）→ 平均分配，等有速度数据后再按比例调整
    if min_speed == 0.0 {
        let per_endpoint = total_connections / endpoints.len();
        let remainder = total_connections % endpoints.len();

        return endpoints
            .iter()
            .enumerate()
            .map(|(i, (ip, _))| {
                let count = if i < remainder {
                    per_endpoint + 1
                } else {
                    per_endpoint
                };
                (*ip, count)
            })
            .collect();
    }

    let ratio = max_speed / min_speed;

    if ratio < speed_threshold {
        // 速度差距不大，平均分配
        let per_endpoint = total_connections / endpoints.len();
        let remainder = total_connections % endpoints.len();

        endpoints
            .iter()
            .enumerate()
            .map(|(i, (ip, _))| {
                let count = if i < remainder {
                    per_endpoint + 1
                } else {
                    per_endpoint
                };
                (*ip, count)
            })
            .collect()
    } else {
        // 按速度比例分配
        let total_speed: f64 = endpoints.iter().map(|(_, s)| *s).sum();

        endpoints
            .iter()
            .map(|(ip, speed)| {
                let ratio = speed / total_speed;
                let count = (ratio * total_connections as f64).round() as usize;
                // 至少分配 1 个 connection
                (*ip, count.max(1))
            })
            .collect()
    }
}