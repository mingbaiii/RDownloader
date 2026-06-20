use std::collections::HashSet;
use std::net::IpAddr;
use std::sync::atomic::{AtomicU64, AtomicBool, Ordering};
use std::sync::Arc;
use serde::{Serialize, Deserialize};
use tokio::sync::Mutex;

/// Segment 状态
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub enum SegmentStatus {
    /// 等待下载
    Pending,
    /// 正在下载
    Downloading,
    /// 已完成
    Completed,
}

/// Segment 信息
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Segment {
    /// 起始位置
    pub start: u64,
    /// 结束位置
    pub end: u64,
    /// 状态
    pub status: SegmentStatus,
    /// 分配的 endpoint
    #[serde(skip)]
    pub assigned_endpoint: Option<IpAddr>,
    /// 分配的 endpoint ID
    #[serde(default)]
    pub assigned_endpoint_id: Option<usize>,
    /// 已下载字节数
    pub downloaded: u64,
}

impl Segment {
    pub fn new(start: u64, end: u64) -> Self {
        Self {
            start,
            end,
            status: SegmentStatus::Pending,
            assigned_endpoint: None,
            assigned_endpoint_id: None,
            downloaded: 0,
        }
    }

    /// 获取 segment 大小
    pub fn size(&self) -> u64 {
        self.end - self.start
    }

    /// 获取当前写入位置
    pub fn current_position(&self) -> u64 {
        self.start + self.downloaded
    }

    /// 剩余字节数
    pub fn remaining(&self) -> u64 {
        self.end - self.current_position()
    }

    /// 是否已完成
    pub fn is_completed(&self) -> bool {
        self.downloaded >= self.size()
    }
}

/// 分片进度（用于持久化）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SegmentProgress {
    /// 起始位置
    pub start: u64,
    /// 结束位置
    pub end: u64,
    /// 已下载大小
    pub downloaded: u64,
    /// 分片状态
    pub status: SegmentStatus,
    /// 使用的 endpoint ID
    #[serde(default)]
    pub endpoint_id: Option<usize>,
}

impl From<&Segment> for SegmentProgress {
    fn from(seg: &Segment) -> Self {
        Self {
            start: seg.start,
            end: seg.end,
            downloaded: seg.downloaded,
            status: seg.status.clone(),
            endpoint_id: seg.assigned_endpoint_id,
        }
    }
}

impl SegmentProgress {
    /// 转换为 Segment
    pub fn to_segment(&self) -> Segment {
        Segment {
            start: self.start,
            end: self.end,
            status: self.status.clone(),
            assigned_endpoint: None,
            assigned_endpoint_id: self.endpoint_id,
            downloaded: self.downloaded,
        }
    }
}

/// Connection 信息
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ConnectionInfo {
    /// Connection ID
    pub id: usize,
    /// 使用的 endpoint
    #[serde(skip)]
    #[serde(default = "default_endpoint")]
    pub endpoint: IpAddr,
    /// endpoint ID（用于持久化）
    #[serde(default)]
    pub endpoint_id: usize,
    /// 当前分配的 segment 索引
    pub current_segment: Option<usize>,
    /// 已下载字节数
    pub downloaded: u64,
    /// 当前速度 (B/s)
    #[serde(skip)]
    #[serde(default)]
    pub speed: f64,
    /// 连续失败次数
    #[serde(skip)]
    #[serde(default)]
    pub failure_count: u32,
    /// 最后一次错误信息
    #[serde(skip)]
    #[serde(default)]
    pub last_error: Option<String>,
}

fn default_endpoint() -> IpAddr {
    "0.0.0.0".parse().unwrap()
}

impl ConnectionInfo {
    pub fn new(id: usize, endpoint: IpAddr, endpoint_id: usize) -> Self {
        Self {
            id,
            endpoint,
            endpoint_id,
            current_segment: None,
            downloaded: 0,
            speed: 0.0,
            failure_count: 0,
            last_error: None,
        }
    }
}

/// Endpoint 状态
#[derive(Debug, Clone)]
pub struct EndpointStatus {
    pub ip: IpAddr,
    pub enabled: bool,
    pub connections: usize,
    pub downloaded: u64,
    pub speed: f64,
}

/// 进度信息
#[derive(Debug, Clone)]
pub struct ProgressInfo {
    pub total_bytes: u64,
    pub downloaded_bytes: u64,
    pub speed: f64,
    pub active_connections: usize,
    pub percentage: f64,
}

/// 下载状态
pub struct DownloadState {
    /// 文件总大小
    pub total_bytes: u64,
    /// 已下载字节数
    pub downloaded_bytes: AtomicU64,
    /// 所有 segment
    pub segments: Vec<Segment>,
    /// 所有 connection
    pub connections: Vec<ConnectionInfo>,
    /// 已禁用的 endpoint
    pub disabled_endpoints: HashSet<IpAddr>,
    /// 所有已知的 endpoint IP（用于发现空闲的启用节点）
    pub known_endpoints: HashSet<IpAddr>,
    /// 是否暂停
    pub paused: AtomicBool,
    /// 是否取消
    pub cancelled: AtomicBool,
    /// 是否完成
    pub completed: AtomicBool,
    /// 服务器是否支持 Range 请求（不支持时禁止多连接扩展）
    pub range_supported: AtomicBool,
}

impl DownloadState {
    pub fn new(total_bytes: u64, segments: Vec<Segment>, connections: Vec<ConnectionInfo>) -> Self {
        Self {
            total_bytes,
            downloaded_bytes: AtomicU64::new(0),
            segments,
            connections,
            disabled_endpoints: HashSet::new(),
            known_endpoints: HashSet::new(),
            paused: AtomicBool::new(false),
            cancelled: AtomicBool::new(false),
            completed: AtomicBool::new(false),
            range_supported: AtomicBool::new(true),  // 默认支持，由 start_download 设置实际值
        }
    }

    /// 获取进度信息
    pub fn get_progress(&self) -> ProgressInfo {
        let downloaded = self.downloaded_bytes.load(Ordering::Relaxed);
        let percentage = if self.total_bytes > 0 {
            (downloaded as f64 / self.total_bytes as f64) * 100.0
        } else if self.completed.load(Ordering::Relaxed) {
            // 未知文件大小但已完成（chunked streaming 场景）
            100.0
        } else {
            0.0
        };

        // 计算总速度
        let speed: f64 = self.connections.iter().map(|c| c.speed).sum();

        // 计算活跃 connection 数
        let active_connections = self.connections.iter().filter(|c| c.current_segment.is_some()).count();

        ProgressInfo {
            total_bytes: self.total_bytes,
            downloaded_bytes: downloaded,
            speed,
            active_connections,
            percentage,
        }
    }

    /// 获取 endpoint 状态
    pub fn get_endpoint_status(&self) -> Vec<EndpointStatus> {
        let mut endpoint_map: std::collections::HashMap<IpAddr, EndpointStatus> = std::collections::HashMap::new();

        for conn in &self.connections {
            let entry = endpoint_map.entry(conn.endpoint).or_insert_with(|| EndpointStatus {
                ip: conn.endpoint,
                enabled: !self.disabled_endpoints.contains(&conn.endpoint),
                connections: 0,
                downloaded: 0,
                speed: 0.0,
            });

            entry.connections += 1;
            entry.downloaded += conn.downloaded;
            entry.speed += conn.speed;
        }

        endpoint_map.into_values().collect()
    }

    /// 获取所有 segment 的进度
    pub fn get_segment_progress(&self) -> Vec<SegmentProgress> {
        self.segments.iter().map(SegmentProgress::from).collect()
    }
}

/// 下载状态的共享类型
pub type SharedDownloadState = Arc<Mutex<DownloadState>>;
