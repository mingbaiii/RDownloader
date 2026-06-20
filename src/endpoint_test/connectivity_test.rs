use std::net::{IpAddr, SocketAddr, TcpStream};
use std::time::Duration;

use surge_ping::{Client as PingClient, Config as PingConfig, PingIdentifier, PingSequence};

use crate::dns::DnsResult;
use crate::endpoint_test::endpoint::EndpointList;

/// 连通性测试结果
#[derive(Debug, Clone)]
pub struct ConnectivityResult {
    pub ip: IpAddr,
    pub icmp_ping: Option<PingResult>,
    pub tcp_ping: Option<TcpPingResult>,
}

/// ICMP Ping 测试结果
#[derive(Debug, Clone)]
pub struct PingResult {
    pub success: bool,
    pub rtt: Option<Duration>,
}

/// TCP Ping 测试结果
#[derive(Debug, Clone)]
pub struct TcpPingResult {
    pub success: bool,
    pub latency: Option<Duration>,
    pub port: Option<u16>,
}

/// 连通性测试配置
#[derive(Debug, Clone)]
pub struct TestConfig {
    /// 超时时间
    pub timeout: Duration,
    /// 是否执行 ICMP ping 测试
    pub test_icmp_ping: bool,
    /// 是否执行 TCP ping 测试
    pub test_tcp_ping: bool,
    /// TCP ping 端口列表
    pub tcp_ports: Vec<u16>,
}

impl Default for TestConfig {
    fn default() -> Self {
        Self {
            timeout: Duration::from_secs(3),
            test_icmp_ping: true,
            test_tcp_ping: true,
            tcp_ports: vec![80, 443],
        }
    }
}

impl TestConfig {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn timeout(mut self, timeout: Duration) -> Self {
        self.timeout = timeout;
        self
    }

    pub fn test_icmp_ping(mut self, enable: bool) -> Self {
        self.test_icmp_ping = enable;
        self
    }

    pub fn test_tcp_ping(mut self, enable: bool) -> Self {
        self.test_tcp_ping = enable;
        self
    }

    pub fn tcp_ports(mut self, ports: Vec<u16>) -> Self {
        self.tcp_ports = ports;
        self
    }
}

/// 测试单个 IP 的连通性
pub async fn test_ip(ip: IpAddr, config: &TestConfig) -> ConnectivityResult {
    let icmp_ping_result = if config.test_icmp_ping {
        Some(test_icmp_ping(ip, config.timeout).await)
    } else {
        None
    };

    let tcp_ping_result = if config.test_tcp_ping {
        Some(test_tcp_ping(ip, &config.tcp_ports, config.timeout).await)
    } else {
        None
    };

    ConnectivityResult {
        ip,
        icmp_ping: icmp_ping_result,
        tcp_ping: tcp_ping_result,
    }
}

/// 测试 EndpointList 中所有 IP 的连通性（并行测试）
pub async fn test_connectivity(endpoint_list: &mut EndpointList, config: &TestConfig) {
    let ips: Vec<IpAddr> = endpoint_list.endpoints.iter().map(|e| e.ip).collect();

    // 并行测试所有 IP
    let mut tasks = Vec::new();
    for ip in ips {
        let config = config.clone();
        tasks.push(tokio::spawn(async move { test_ip(ip, &config).await }));
    }

    // 收集结果
    let mut results = Vec::new();
    for task in tasks {
        if let Ok(result) = task.await {
            results.push(result);
        }
    }

    // 更新 EndpointList
    for result in results {
        if let Some(endpoint) = endpoint_list.endpoints.iter_mut().find(|e| e.ip == result.ip) {
            endpoint.icmp_ping = result.icmp_ping;
            endpoint.tcp_ping = result.tcp_ping;
        }
    }
}

/// 测试 DnsResult 中所有 IP 的连通性
pub async fn test_connectivity_dns(
    dns_result: &DnsResult,
    config: &TestConfig,
) -> Vec<ConnectivityResult> {
    let mut results = Vec::new();

    let mut tasks = Vec::new();
    for &ip in &dns_result.ips {
        let config = config.clone();
        tasks.push(tokio::spawn(async move { test_ip(ip, &config).await }));
    }

    for task in tasks {
        if let Ok(result) = task.await {
            results.push(result);
        }
    }

    results
}

/// ICMP Ping 测试（使用 surge-ping）
async fn test_icmp_ping(ip: IpAddr, timeout: Duration) -> PingResult {
    let identifier = PingIdentifier(rand::random::<u16>());

    let config = PingConfig::default();
    let ping_client = match PingClient::new(&config) {
        Ok(client) => client,
        Err(_) => {
            return PingResult {
                success: false,
                rtt: None,
            }
        }
    };

    let mut pinger = ping_client.pinger(ip, identifier).await;
    pinger.timeout(timeout);

    let payload = [0u8; 56];

    match pinger.ping(PingSequence(0), &payload).await {
        Ok((_, rtt)) => PingResult {
            success: true,
            rtt: Some(rtt),
        },
        Err(_) => PingResult {
            success: false,
            rtt: None,
        },
    }
}

/// TCP Ping 测试（使用标准库 TcpStream）
async fn test_tcp_ping(ip: IpAddr, ports: &[u16], timeout: Duration) -> TcpPingResult {
    for &port in ports {
        let addr = SocketAddr::new(ip, port);

        let result = tokio::task::spawn_blocking(move || {
            let start = std::time::Instant::now();
            match TcpStream::connect_timeout(&addr, timeout) {
                Ok(_) => {
                    let latency = start.elapsed();
                    Ok((latency, port))
                }
                Err(e) => Err(e),
            }
        })
        .await;

        match result {
            Ok(Ok((latency, port))) => {
                return TcpPingResult {
                    success: true,
                    latency: Some(latency),
                    port: Some(port),
                }
            }
            _ => continue,
        }
    }

    TcpPingResult {
        success: false,
        latency: None,
        port: None,
    }
}

/// 格式化测试结果
pub fn format_results(domain: &str, results: &[ConnectivityResult]) -> String {
    let mut output = format!("连通性测试结果 - {}\n", domain);
    output.push_str(&format!("{:-<60}\n", ""));

    for result in results {
        output.push_str(&format!("IP: {}\n", result.ip));

        if let Some(icmp_ping) = &result.icmp_ping {
            if icmp_ping.success {
                if let Some(rtt) = icmp_ping.rtt {
                    output.push_str(&format!("  ICMP Ping: 成功 (RTT: {:?})\n", rtt));
                } else {
                    output.push_str(&format!("  ICMP Ping: 成功\n"));
                }
            } else {
                output.push_str(&format!("  ICMP Ping: 失败\n"));
            }
        }

        if let Some(tcp_ping) = &result.tcp_ping {
            if tcp_ping.success {
                if let (Some(latency), Some(port)) = (tcp_ping.latency, tcp_ping.port) {
                    output.push_str(&format!(
                        "  TCP Ping: 成功 (端口: {}, 延迟: {:?})\n",
                        port, latency
                    ));
                } else {
                    output.push_str(&format!("  TCP Ping: 成功\n"));
                }
            } else {
                output.push_str(&format!("  TCP Ping: 失败\n"));
            }
        }

        output.push('\n');
    }

    output
}
