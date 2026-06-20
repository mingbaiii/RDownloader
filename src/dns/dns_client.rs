use std::fmt;
use std::net::{IpAddr, SocketAddr};
use std::sync::Arc;
use std::time::Duration;

use async_trait::async_trait;
use thiserror::Error;

use super::doh_dns_client::DohDnsClient;
use super::mix_dns_client::MixDnsClient;
use super::system_dns_client::SystemDnsClient;
use super::udp_dns_client::UdpDnsClient;

/// DNS 解析错误
#[derive(Debug, Error)]
pub enum DnsError {
    #[error("网络错误: {0}")]
    Network(String),
    #[error("解析失败: {0}")]
    ResolutionFailed(String),
    #[error("无效的服务器地址: {0}")]
    InvalidServer(String),
    #[error("超时")]
    Timeout,
}

/// DNS 客户端配置
#[derive(Debug, Clone)]
pub struct DnsConfig {
    /// 查询超时时间
    pub timeout: Duration,
    /// 重试次数
    pub retry_count: u32,
    /// 重试间隔
    pub retry_interval: Duration,
    /// 代理地址
    pub proxy: Option<String>,
}

impl Default for DnsConfig {
    fn default() -> Self {
        Self {
            timeout: Duration::from_secs(5),
            retry_count: 3,
            retry_interval: Duration::from_millis(500),
            proxy: None,
        }
    }
}

impl DnsConfig {
    pub fn new(timeout: Duration, retry_count: u32, retry_interval: Duration) -> Self {
        Self {
            timeout,
            retry_count,
            retry_interval,
            proxy: None,
        }
    }

    pub fn timeout(mut self, timeout: Duration) -> Self {
        self.timeout = timeout;
        self
    }

    pub fn retry_count(mut self, count: u32) -> Self {
        self.retry_count = count;
        self
    }

    pub fn retry_interval(mut self, interval: Duration) -> Self {
        self.retry_interval = interval;
        self
    }

    pub fn proxy(mut self, proxy: impl Into<String>) -> Self {
        self.proxy = Some(proxy.into());
        self
    }
}

/// DNS 解析结果
#[derive(Debug, Clone)]
pub struct DnsResult {
    pub domain: String,
    pub ips: Vec<IpAddr>,
}

impl DnsResult {
    pub fn new(domain: impl Into<String>) -> Self {
        Self {
            domain: domain.into(),
            ips: Vec::new(),
        }
    }

    pub fn with_ips(mut self, ips: Vec<IpAddr>) -> Self {
        self.ips = ips;
        self
    }

    /// 获取所有 IPv4 地址
    pub fn ipv4(&self) -> Vec<IpAddr> {
        self.ips.iter().filter(|ip| ip.is_ipv4()).copied().collect()
    }

    /// 获取所有 IPv6 地址
    pub fn ipv6(&self) -> Vec<IpAddr> {
        self.ips.iter().filter(|ip| ip.is_ipv6()).copied().collect()
    }

    /// 是否有结果
    pub fn is_empty(&self) -> bool {
        self.ips.is_empty()
    }
}

impl fmt::Display for DnsResult {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}: ", self.domain)?;
        for (i, ip) in self.ips.iter().enumerate() {
            if i > 0 {
                write!(f, ", ")?;
            }
            write!(f, "{}", ip)?;
        }
        Ok(())
    }
}

/// DNS 客户端 trait
#[async_trait]
pub trait DnsClient: Send + Sync {
    /// 解析域名，返回所有 IP（包括递归解析 CNAME）
    async fn resolve(&self, domain: &str) -> Result<DnsResult, DnsError>;

    /// 客户端名称（用于日志）
    fn name(&self) -> &str;
}

/// DNS 服务器配置
pub enum DnsServerConfig {
    /// UDP DNS 服务器 (IP:Port)
    Udp(SocketAddr),
    /// DoH 服务器 (HTTPS URL)
    Doh(String),
    /// 系统 DNS
    System,
}

impl DnsServerConfig {
    /// 从字符串创建配置
    /// - 如果是 IP 地址（可选端口），创建 UDP 配置
    /// - 如果是 HTTPS URL，创建 DoH 配置
    pub fn from_str(s: &str) -> Result<Self, DnsError> {
        // 系统 DNS
        if s.eq_ignore_ascii_case("system") {
            return Ok(DnsServerConfig::System);
        }

        // 尝试解析为 URL
        if s.starts_with("https://") || s.starts_with("http://") {
            return Ok(DnsServerConfig::Doh(s.to_string()));
        }

        // 尝试解析为 SocketAddr
        if let Ok(addr) = s.parse::<SocketAddr>() {
            return Ok(DnsServerConfig::Udp(addr));
        }

        // 尝试解析为 IP（使用默认端口 53）
        if let Ok(ip) = s.parse::<IpAddr>() {
            return Ok(DnsServerConfig::Udp(SocketAddr::new(ip, 53)));
        }

        Err(DnsError::InvalidServer(s.to_string()))
    }
}

/// 创建单个 DNS 客户端
pub fn create_dns_client(
    config: DnsServerConfig,
    dns_config: DnsConfig,
) -> Result<Arc<dyn DnsClient>, DnsError> {
    match config {
        DnsServerConfig::Udp(addr) => Ok(Arc::new(UdpDnsClient::new(addr, dns_config)?)),
        DnsServerConfig::Doh(url) => Ok(Arc::new(DohDnsClient::new(url, dns_config)?)),
        DnsServerConfig::System => Ok(Arc::new(SystemDnsClient::new(dns_config))),
    }
}

/// 创建 DNS 客户端（从字符串，使用默认配置）
pub fn create_dns_client_from_str(server: &str) -> Result<Arc<dyn DnsClient>, DnsError> {
    let config = DnsServerConfig::from_str(server)?;
    create_dns_client(config, DnsConfig::default())
}

/// 创建 DNS 客户端（从字符串，自定义配置）
pub fn create_dns_client_with_config(
    server: &str,
    dns_config: DnsConfig,
) -> Result<Arc<dyn DnsClient>, DnsError> {
    let config = DnsServerConfig::from_str(server)?;
    create_dns_client(config, dns_config)
}

/// 创建混合 DNS 客户端（使用默认配置）
pub fn create_mix_dns_client(servers: &[&str]) -> Result<Arc<dyn DnsClient>, DnsError> {
    create_mix_dns_client_with_config(servers, DnsConfig::default())
}

/// 创建混合 DNS 客户端（自定义配置）
pub fn create_mix_dns_client_with_config(
    servers: &[&str],
    dns_config: DnsConfig,
) -> Result<Arc<dyn DnsClient>, DnsError> {
    let mut clients: Vec<Arc<dyn DnsClient>> = Vec::new();

    for server in servers {
        let client = create_dns_client_with_config(server, dns_config.clone())?;
        clients.push(client);
    }

    if clients.is_empty() {
        return Err(DnsError::InvalidServer("至少需要一个 DNS 服务器".to_string()));
    }

    Ok(Arc::new(MixDnsClient::new(clients)))
}

/// 创建带代理的混合 DNS 客户端（使用自定义基础配置）
/// proxy_map: key 为 dns 服务器地址，value 为代理地址
pub fn create_mix_dns_client_with_proxy_and_config(
    servers: &[&str],
    proxy_map: &std::collections::HashMap<String, String>,
    base_config: DnsConfig,
) -> Result<Arc<dyn DnsClient>, DnsError> {
    let mut clients: Vec<Arc<dyn DnsClient>> = Vec::new();

    let mut errors: Vec<String> = Vec::new();

    for server in servers {
        let dns_config = if let Some(proxy) = proxy_map.get(*server) {
            base_config.clone().proxy(proxy)
        } else {
            base_config.clone()
        };

        match create_dns_client_with_config(server, dns_config) {
            Ok(client) => clients.push(client),
            Err(e) => {
                eprintln!("跳过无效 DNS 服务器 '{}': {}", server, e);
                errors.push(format!("{}: {}", server, e));
            }
        }
    }

    if clients.is_empty() {
        if errors.is_empty() {
            return Err(DnsError::InvalidServer("至少需要一个 DNS 服务器".to_string()));
        }
        return Err(DnsError::InvalidServer(format!(
            "所有 DNS 服务器均无效: {}", errors.join("; ")
        )));
    }

    Ok(Arc::new(MixDnsClient::new(clients)))
}

/// 创建带代理的混合 DNS 客户端（使用默认配置）
/// proxy_map: key 为 dns 服务器地址，value 为代理地址
pub fn create_mix_dns_client_with_proxy(
    servers: &[&str],
    proxy_map: &std::collections::HashMap<String, String>,
) -> Result<Arc<dyn DnsClient>, DnsError> {
    create_mix_dns_client_with_proxy_and_config(servers, proxy_map, DnsConfig::default())
}

/// 创建系统 DNS 客户端（使用默认配置）
pub fn create_system_dns_client() -> Arc<dyn DnsClient> {
    create_system_dns_client_with_config(DnsConfig::default())
}

/// 创建系统 DNS 客户端（自定义配置）
pub fn create_system_dns_client_with_config(dns_config: DnsConfig) -> Arc<dyn DnsClient> {
    Arc::new(SystemDnsClient::new(dns_config))
}
