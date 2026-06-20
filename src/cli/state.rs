use std::collections::HashMap;
use std::net::IpAddr;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

/// 下载状态
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub enum DownloadStatus {
    Pending,
    Downloading,
    Paused,
    Completed,
    Failed,
    Cancelled,
}

impl std::fmt::Display for DownloadStatus {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            DownloadStatus::Pending => write!(f, "pending"),
            DownloadStatus::Downloading => write!(f, "downloading"),
            DownloadStatus::Paused => write!(f, "paused"),
            DownloadStatus::Completed => write!(f, "completed"),
            DownloadStatus::Failed => write!(f, "failed"),
            DownloadStatus::Cancelled => write!(f, "cancelled"),
        }
    }
}

/// Endpoint 信息
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EndpointInfo {
    /// Endpoint ID（顺序编号）
    #[serde(default)]
    pub id: usize,
    pub ip: IpAddr,
    pub enabled: bool,
    pub proxy: Option<String>,
}

/// 分片进度（用于持久化）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SegmentProgress {
    /// 起始位置
    pub start: u64,
    /// 结束位置（u64::MAX 序列化为 -1 以兼容有符号语言）
    #[serde(serialize_with = "serialize_end", deserialize_with = "deserialize_end")]
    pub end: u64,
    /// 已下载大小
    pub downloaded: u64,
    /// 是否已完成
    pub completed: bool,
    /// 使用的 endpoint ID
    #[serde(default)]
    pub endpoint_id: Option<usize>,
}

fn serialize_end<S>(end: &u64, serializer: S) -> Result<S::Ok, S::Error>
where
    S: serde::Serializer,
{
    if *end == u64::MAX {
        serializer.serialize_i64(-1)
    } else {
        serializer.serialize_u64(*end)
    }
}

fn deserialize_end<'de, D>(deserializer: D) -> Result<u64, D::Error>
where
    D: serde::Deserializer<'de>,
{
    use serde::de::{self, Visitor};
    struct EndVisitor;
    impl<'de> Visitor<'de> for EndVisitor {
        type Value = u64;
        fn expecting(&self, f: &mut std::fmt::Formatter) -> std::fmt::Result {
            f.write_str("a u64 or -1 (for unknown end)")
        }
        fn visit_u64<E: de::Error>(self, v: u64) -> Result<u64, E> {
            Ok(v)
        }
        fn visit_i64<E: de::Error>(self, v: i64) -> Result<u64, E> {
            if v == -1 { Ok(u64::MAX) }
            else if v >= 0 { Ok(v as u64) }
            else { Err(E::custom(format!("negative end value: {}", v))) }
        }
    }
    deserializer.deserialize_any(EndVisitor)
}

/// 下载信息
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DownloadInfo {
    pub id: String,
    pub url: String,
    /// 重定向后的实际 URL
    #[serde(default)]
    pub actual_url: String,
    pub output: String,
    pub file_size: u64,
    pub downloaded: u64,
    pub status: DownloadStatus,
    pub endpoints: Vec<EndpointInfo>,
    pub created_at: String,
    /// 分片进度（用于断点续传）
    #[serde(default)]
    pub segments: Vec<SegmentProgress>,
    /// 使用的连接数
    #[serde(default = "default_connections")]
    pub connections: usize,
    /// 服务器响应头（从 speed test 获取，含 accept-ranges 信息）
    #[serde(default)]
    pub response_headers: HashMap<String, String>,
}

fn default_connections() -> usize {
    4
}

impl DownloadInfo {
    pub fn new(id: String, url: String, output: String) -> Self {
        Self {
            id,
            url,
            actual_url: String::new(),
            output,
            file_size: 0,
            downloaded: 0,
            status: DownloadStatus::Pending,
            endpoints: Vec::new(),
            created_at: chrono::Utc::now().to_rfc3339(),
            segments: Vec::new(),
            // 注意：connections 会在 cmd_download / cmd_resume 中用 GLOBAL_CONFIG.total_connections 覆盖
            // 这里设为 0 以避免 JSON 中出现误导的默认值 4
            connections: 0,
            response_headers: HashMap::new(),
        }
    }

    pub fn progress(&self) -> f64 {
        if self.file_size == 0 {
            0.0
        } else {
            (self.downloaded as f64 / self.file_size as f64) * 100.0
        }
    }

    /// 更新分片进度
    pub fn update_segments(&mut self, segments: &[crate::downloader::state::Segment]) {
        self.segments = segments.iter().map(|seg| SegmentProgress {
            start: seg.start,
            end: seg.end,
            downloaded: seg.downloaded,
            completed: seg.downloaded >= (seg.end - seg.start),
            endpoint_id: seg.assigned_endpoint_id,
        }).collect();
        self.downloaded = segments.iter().map(|seg| seg.downloaded).sum();
    }

    /// 获取已完成的分片范围
    pub fn get_completed_segments(&self) -> Vec<(u64, u64)> {
        self.segments.iter()
            .filter(|seg| seg.completed)
            .map(|seg| (seg.start, seg.end))
            .collect()
    }

    /// 获取未完成的分片
    pub fn get_incomplete_segments(&self) -> Vec<&SegmentProgress> {
        self.segments.iter()
            .filter(|seg| !seg.completed)
            .collect()
    }
}

/// 生成 .rdown.json 文件名
pub fn generate_rdown_filename(original_name: &str, id: &str) -> String {
    format!("{}.{}.rdown.json", original_name, id)
}

/// 解析 .rdown.json 文件名，返回 (原文件名, id)
pub fn parse_rdown_filename(filename: &str) -> Option<(String, String)> {
    let path = Path::new(filename);
    let stem = path.file_stem()?.to_str()?;

    // 移除 .rdown 后缀
    let stem = stem.strip_suffix(".rdown").unwrap_or(stem);

    // 找到最后一个 . 分隔 ID
    if let Some(last_dot) = stem.rfind('.') {
        let original = &stem[..last_dot];
        let id = &stem[last_dot + 1..];

        // 校验 ID 格式（8 位字母数字）
        if id.len() == 8 && id.chars().all(|c| c.is_alphanumeric()) {
            return Some((original.to_string(), id.to_string()));
        }
    }

    // 如果没有找到 . 分隔，检查整个 stem 是否是 8 位 ID
    if stem.len() == 8 && stem.chars().all(|c| c.is_alphanumeric()) {
        return Some(("download".to_string(), stem.to_string()));
    }

    None
}

/// 保存下载信息到 .rdown.json 文件
pub fn save_download(info: &DownloadInfo) -> Result<(), String> {
    let filename = generate_rdown_filename(&info.output, &info.id);
    let json = serde_json::to_string_pretty(info)
        .map_err(|e| format!("Serialize error: {}", e))?;
    std::fs::write(&filename, json)
        .map_err(|e| format!("Write error: {}", e))?;
    Ok(())
}

/// 从 .rdown.json 文件加载下载信息
pub fn load_download(path: &Path) -> Result<DownloadInfo, String> {
    let json = std::fs::read_to_string(path)
        .map_err(|e| format!("Read error: {}", e))?;
    let info: DownloadInfo = serde_json::from_str(&json)
        .map_err(|e| format!("Parse error: {}", e))?;
    Ok(info)
}

/// 加载当前目录所有下载
pub fn load_all_downloads() -> Vec<DownloadInfo> {
    let current_dir = match std::env::current_dir() {
        Ok(dir) => dir,
        Err(_) => return Vec::new(),
    };

    let mut downloads = Vec::new();

    if let Ok(entries) = std::fs::read_dir(&current_dir) {
        for entry in entries.flatten() {
            let path = entry.path();
            if let Some(filename) = path.file_name().and_then(|f| f.to_str()) {
                if filename.ends_with(".rdown.json") {
                    if let Some((_, id)) = parse_rdown_filename(filename) {
                        if let Ok(info) = load_download(&path) {
                            if info.id == id {
                                downloads.push(info);
                            }
                        }
                    }
                }
            }
        }
    }

    downloads
}

/// 查找指定 ID 的下载文件（支持前缀匹配）
pub fn find_download_by_id(id: &str) -> Option<(PathBuf, DownloadInfo)> {
    let current_dir = std::env::current_dir().ok()?;
    let mut matches = Vec::new();

    if let Ok(entries) = std::fs::read_dir(&current_dir) {
        for entry in entries.flatten() {
            let path = entry.path();
            if let Some(filename) = path.file_name().and_then(|f| f.to_str()) {
                if filename.ends_with(".rdown.json") {
                    if let Some((_, file_id)) = parse_rdown_filename(filename) {
                        // 精确匹配
                        if file_id == id {
                            if let Ok(info) = load_download(&path) {
                                return Some((path, info));
                            }
                        }
                        // 前缀匹配
                        if file_id.starts_with(id) {
                            if let Ok(info) = load_download(&path) {
                                matches.push((path, info));
                            }
                        }
                    }
                }
            }
        }
    }

    // 如果只有一个前缀匹配，返回它
    if matches.len() == 1 {
        return Some(matches.into_iter().next().unwrap());
    }

    // 如果有多个前缀匹配，返回 None（需要用户更精确地指定）
    if matches.len() > 1 {
        eprintln!("Multiple downloads match '{}':", id);
        for (_, info) in &matches {
            eprintln!("  {}", info.id);
        }
    }

    None
}

/// 检查 ID 是否已存在
pub fn id_exists(id: &str) -> bool {
    find_download_by_id(id).is_some()
}
