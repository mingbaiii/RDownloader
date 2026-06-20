use std::collections::HashMap;
use std::net::IpAddr;
use std::path::PathBuf;
use std::sync::LazyLock;
use std::time::Duration;

use serde::{Deserialize, Serialize};

/// IP 协议偏好
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum IpProtocol {
    /// 仅使用 IPv4
    OnlyIPv4,
    /// 仅使用 IPv6
    OnlyIPv6,
    /// 两者都使用
    Both,
}

impl Default for IpProtocol {
    fn default() -> Self {
        Self::OnlyIPv4
    }
}

/// 默认 DNS 服务器
fn default_dns_servers() -> Vec<String> {
    vec![
        "system".to_string(),                        // 系统 DNS
        "114.114.114.114".to_string(),               // 114 UDP DNS
        "https://223.5.5.5/dns-query".to_string(),   // 阿里 DoH
    ]
}

/// 全局配置
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct GlobalConfig {
    /// IP 协议偏好
    pub ip_protocol: IpProtocol,
    /// 总 connection 数
    pub total_connections: usize,
    /// 下载失败重试次数
    pub retry_count: u32,
    /// 初始 chunk 大小 (字节)
    pub chunk_size: usize,
    /// 速度差距阈值（超过此值按比例分配，否则平均分配）
    pub speed_threshold: f64,
    /// 下载超时（秒）
    pub download_timeout_secs: u64,
    /// 单个 endpoint 连续失败多少次后禁用
    pub endpoint_failure_threshold: u32,
    /// 重试步骤 1：重新连接的次数
    pub retry_reconnect_count: u32,
    /// 重试步骤 2：重新获取 endpoint 的次数
    pub retry_refetch_endpoint_count: u32,
    /// 重试步骤 3：重新获取 actual URL 的次数
    pub retry_refetch_url_count: u32,
    /// Content-Length=0 时重试次数
    pub content_length_zero_retry_count: u32,
    /// 速度测试重试次数
    pub speed_test_retry_count: u32,
    /// 速度测试超时（秒）
    pub speed_test_timeout_secs: u64,
    /// 连通性测试超时（秒）
    pub connectivity_timeout_secs: u64,
    /// DNS 查询超时（秒）
    pub dns_timeout_secs: u64,
    /// DNS 查询重试次数
    pub dns_retry_count: u32,
    /// 调度器检查间隔（秒）
    pub scheduler_check_interval_secs: u64,
    /// 调度器慢节点阈值（低于平均速度的比例）
    pub scheduler_slow_threshold: f64,
    /// 调度器重新分配阈值（变化超过此比例才重新分配）
    pub scheduler_reallocate_threshold: f64,
    /// DNS 服务器列表
    #[serde(default = "default_dns_servers")]
    pub dns_servers: Vec<String>,
    /// 自定义请求头
    pub headers: HashMap<String, String>,
    /// 代理配置：key 为 "*"（全局）、IP 地址、"dns:*"、"dns:xxx"，value 为代理地址
    pub proxy: HashMap<String, String>,
}

impl Default for GlobalConfig {
    fn default() -> Self {
        Self {
            ip_protocol: IpProtocol::default(),
            total_connections: 64,
            retry_count: 3,
            chunk_size: 64 * 1024,  // 64KB
            speed_threshold: 2.0,
            download_timeout_secs: 30,
            endpoint_failure_threshold: 3,
            retry_reconnect_count: 3,
            retry_refetch_endpoint_count: 2,
            retry_refetch_url_count: 1,
            content_length_zero_retry_count: 3,
            speed_test_retry_count: 3,
            speed_test_timeout_secs: 15,
            connectivity_timeout_secs: 3,
            dns_timeout_secs: 5,
            dns_retry_count: 3,
            scheduler_check_interval_secs: 3,
            scheduler_slow_threshold: 0.3,
            scheduler_reallocate_threshold: 0.2,
            dns_servers: default_dns_servers(),
            headers: HashMap::new(),
            proxy: HashMap::new(),
        }
    }
}

impl GlobalConfig {
    /// 获取下载超时 Duration
    pub fn download_timeout(&self) -> Duration {
        Duration::from_secs(self.download_timeout_secs)
    }

    /// 从文件加载配置
    pub fn load_from_file(path: &PathBuf) -> Self {
        if path.exists() {
            match std::fs::read_to_string(path) {
                Ok(json) => {
                    match serde_json::from_str(&json) {
                        Ok(config) => return config,
                        Err(e) => {
                            eprintln!("Warning: Failed to parse config file: {}", e);
                        }
                    }
                }
                Err(e) => {
                    eprintln!("Warning: Failed to read config file: {}", e);
                }
            }
        }
        Self::default()
    }

    /// 保存配置到文件
    pub fn save_to_file(&self, path: &PathBuf) -> Result<(), String> {
        let json = serde_json::to_string_pretty(self)
            .map_err(|e| format!("Failed to serialize config: {}", e))?;
        std::fs::write(path, json)
            .map_err(|e| format!("Failed to write config file: {}", e))?;
        Ok(())
    }
}

/// 全局配置实例
pub static GLOBAL_CONFIG: LazyLock<tokio::sync::RwLock<GlobalConfig>> =
    LazyLock::new(|| tokio::sync::RwLock::new(GlobalConfig::default()));

/// 配置文件路径
pub static CONFIG_PATH: LazyLock<tokio::sync::RwLock<Option<PathBuf>>> =
    LazyLock::new(|| tokio::sync::RwLock::new(None));

/// 初始化配置
pub async fn init_config(config_path: Option<PathBuf>) {
    let mut path_guard = CONFIG_PATH.write().await;
    *path_guard = config_path.clone();

    if let Some(path) = config_path {
        let config = GlobalConfig::load_from_file(&path);
        let mut global = GLOBAL_CONFIG.write().await;
        *global = config;
    }
}

/// 设置全局配置
pub async fn set_global_config(config: GlobalConfig) {
    let mut global = GLOBAL_CONFIG.write().await;
    *global = config;
}

/// 获取全局配置
pub async fn get_global_config() -> GlobalConfig {
    GLOBAL_CONFIG.read().await.clone()
}

/// 保存当前配置到文件
/// 如果已通过 --config 指定路径，保存到该路径
/// 否则保存到当前目录下的 rdownloader.json
pub async fn save_config() -> Result<(), String> {
    let path_guard = CONFIG_PATH.read().await;
    let path = if let Some(path) = path_guard.as_ref() {
        path.clone()
    } else {
        // 自动使用当前目录下的默认配置文件名
        let cwd = std::env::current_dir()
            .map_err(|e| format!("Get current dir error: {}", e))?;
        cwd.join("rdownloader.json")
    };
    let config = GLOBAL_CONFIG.read().await;
    config.save_to_file(&path)?;
    // 更新保存的路径
    drop(path_guard);
    let mut path_guard = CONFIG_PATH.write().await;
    *path_guard = Some(path);
    Ok(())
}

/// 根据 IP 协议偏好过滤 IP 列表
pub fn filter_ips(ips: &[IpAddr], protocol: IpProtocol) -> Vec<IpAddr> {
    match protocol {
        IpProtocol::OnlyIPv4 => ips.iter().filter(|ip| ip.is_ipv4()).copied().collect(),
        IpProtocol::OnlyIPv6 => ips.iter().filter(|ip| ip.is_ipv6()).copied().collect(),
        IpProtocol::Both => ips.to_vec(),
    }
}

/// 根据全局配置过滤 IP 列表
pub async fn filter_ips_by_config(ips: &[IpAddr]) -> Vec<IpAddr> {
    let config = get_global_config().await;
    filter_ips(ips, config.ip_protocol)
}
