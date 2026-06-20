use std::collections::HashMap;
use std::net::IpAddr;
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::RwLock;

use crate::dns::DnsResult;
use crate::endpoint_test::{PingResult, SpeedResult, TcpPingResult};
use crate::remote_file::RemoteFile;

/// 单个 Endpoint 数据
#[derive(Debug, Clone)]
pub struct DataEndpoint {
    /// IP 地址
    pub ip: IpAddr,
    /// 配置中的 endpoint ID（用于持久化和显示）
    pub config_id: Option<usize>,
    /// ICMP Ping 结果
    pub icmp_ping: Option<PingResult>,
    /// TCP Ping 结果
    pub tcp_ping: Option<TcpPingResult>,
    /// 速度测试结果
    pub speed: Option<SpeedResult>,
}

impl DataEndpoint {
    pub fn new(ip: IpAddr) -> Self {
        Self {
            ip,
            config_id: None,
            icmp_ping: None,
            tcp_ping: None,
            speed: None,
        }
    }

    /// 创建带配置 ID 的 endpoint
    pub fn with_config_id(ip: IpAddr, config_id: usize) -> Self {
        Self {
            ip,
            config_id: Some(config_id),
            icmp_ping: None,
            tcp_ping: None,
            speed: None,
        }
    }

    /// 获取最佳延迟（ICMP 或 TCP 中较短的那个）
    pub fn best_latency(&self) -> Option<Duration> {
        let icmp_latency = self
            .icmp_ping
            .as_ref()
            .and_then(|p| if p.success { p.rtt } else { None });

        let tcp_latency = self
            .tcp_ping
            .as_ref()
            .and_then(|p| if p.success { p.latency } else { None });

        match (icmp_latency, tcp_latency) {
            (Some(a), Some(b)) => Some(a.min(b)),
            (Some(a), None) => Some(a),
            (None, Some(b)) => Some(b),
            (None, None) => None,
        }
    }

    /// 获取传输速度（字节/秒）
    pub fn speed_bps(&self) -> Option<f64> {
        self.speed
            .as_ref()
            .and_then(|s| if s.success { s.speed } else { None })
    }

    /// 是否可用
    /// 如果测速已执行但失败，则 endpoint 不可用（即使 TCP ping 成功）
    /// 如果测速未执行，TCP ping 成功即视为可用
    pub fn is_available(&self) -> bool {
        let speed_ok = self.speed.as_ref().map_or(false, |s| s.success);

        // 测速已执行但失败 → 不可用
        if self.speed.is_some() && !speed_ok {
            return false;
        }

        let tcp_ok = self.tcp_ping.as_ref().map_or(false, |t| t.success);
        tcp_ok || speed_ok
    }
}

/// Endpoint 列表
#[derive(Debug, Clone)]
pub struct EndpointList {
    /// 原始域名
    pub domain: String,
    /// Endpoint 列表
    pub endpoints: Vec<DataEndpoint>,
    /// 绑定的 RemoteFile
    pub remote_file: Option<Arc<RwLock<RemoteFile>>>,
}

impl EndpointList {
    /// 创建空的 EndpointList
    pub fn new(domain: &str) -> Self {
        Self {
            domain: domain.to_string(),
            endpoints: Vec::new(),
            remote_file: None,
        }
    }

    /// 添加 endpoint
    pub fn add_endpoint(&mut self, ip: IpAddr) {
        if !self.endpoints.iter().any(|e| e.ip == ip) {
            self.endpoints.push(DataEndpoint::new(ip));
        }
    }

    /// 添加带配置 ID 的 endpoint
    pub fn add_endpoint_with_id(&mut self, ip: IpAddr, config_id: usize) {
        if !self.endpoints.iter().any(|e| e.ip == ip) {
            self.endpoints.push(DataEndpoint::with_config_id(ip, config_id));
        }
    }

    /// 从 DnsResult 创建
    pub fn from_dns_result(dns_result: &DnsResult) -> Self {
        let endpoints = dns_result
            .ips
            .iter()
            .map(|&ip| DataEndpoint::new(ip))
            .collect();

        Self {
            domain: dns_result.domain.clone(),
            endpoints,
            remote_file: None,
        }
    }

    /// 绑定 RemoteFile
    pub fn bind_remote_file(&mut self, remote_file: Arc<RwLock<RemoteFile>>) {
        self.remote_file = Some(remote_file);
    }

    /// 更新绑定的 RemoteFile 的响应头
    pub async fn update_remote_file_headers(&self, headers: HashMap<String, String>) {
        if let Some(remote_file) = &self.remote_file {
            let mut file = remote_file.write().await;
            file.update_response_headers(headers);
        }
    }

    /// 按延迟排序
    pub fn sort_by_latency(&mut self) {
        self.endpoints.sort_by(|a, b| {
            match (a.best_latency(), b.best_latency()) {
                (Some(a_latency), Some(b_latency)) => a_latency.cmp(&b_latency),
                (Some(_), None) => std::cmp::Ordering::Less,
                (None, Some(_)) => std::cmp::Ordering::Greater,
                (None, None) => std::cmp::Ordering::Equal,
            }
        });
    }

    /// 按速度排序（从快到慢）
    pub fn sort_by_speed(&mut self) {
        self.endpoints.sort_by(|a, b| {
            match (a.speed_bps(), b.speed_bps()) {
                (Some(a_speed), Some(b_speed)) => {
                    b_speed.partial_cmp(&a_speed).unwrap_or(std::cmp::Ordering::Equal)
                }
                (Some(_), None) => std::cmp::Ordering::Less,
                (None, Some(_)) => std::cmp::Ordering::Greater,
                (None, None) => std::cmp::Ordering::Equal,
            }
        });
    }

    /// 获取可用的 endpoint 列表
    pub fn available(&self) -> Vec<&DataEndpoint> {
        self.endpoints.iter().filter(|e| e.is_available()).collect()
    }

    /// 获取最佳 endpoint（延迟最低）
    pub fn best_by_latency(&self) -> Option<&DataEndpoint> {
        self.available()
            .into_iter()
            .min_by_key(|e| e.best_latency().unwrap_or(Duration::MAX))
    }

    /// 获取最佳 endpoint（速度最快）
    pub fn best_by_speed(&self) -> Option<&DataEndpoint> {
        self.available().into_iter().max_by(|a, b| {
            a.speed_bps()
                .unwrap_or(0.0)
                .partial_cmp(&b.speed_bps().unwrap_or(0.0))
                .unwrap_or(std::cmp::Ordering::Equal)
        })
    }

    /// 格式化输出
    pub fn format(&self) -> String {
        let mut output = format!("Endpoint 列表 - {}\n", self.domain);
        output.push_str(&format!("{:-<80}\n", ""));
        output.push_str(&format!(
            "{:<20} {:<12} {:<10} {:<12} {:<15} {:<8}\n",
            "IP", "延迟", "状态码", "头大小", "速度", "可用"
        ));
        output.push_str(&format!("{:-<80}\n", ""));

        for ep in &self.endpoints {
            let latency_str = ep
                .best_latency()
                .map(|l| format!("{:?}", l))
                .unwrap_or_else(|| "-".to_string());

            let (status_str, header_str, speed_str) = if let Some(speed) = &ep.speed {
                let status = speed
                    .status_code
                    .map(|s| s.to_string())
                    .unwrap_or_else(|| "-".to_string());
                let header = speed
                    .header_size
                    .map(|s| format!("{} B", s))
                    .unwrap_or_else(|| "-".to_string());
                let spd = speed
                    .speed
                    .map(|s| format_speed(s))
                    .unwrap_or_else(|| "-".to_string());
                (status, header, spd)
            } else {
                ("-".to_string(), "-".to_string(), "-".to_string())
            };

            let available_str = if ep.is_available() { "✓" } else { "✗" };

            output.push_str(&format!(
                "{:<20} {:<12} {:<10} {:<12} {:<15} {:<8}\n",
                ep.ip, latency_str, status_str, header_str, speed_str, available_str
            ));
        }

        output
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
