use std::net::{IpAddr, SocketAddr, UdpSocket};
use std::time::Duration;

use async_trait::async_trait;

use super::dns_client::{DnsClient, DnsConfig, DnsError, DnsResult};
use super::dns_message::{build_query, parse_response, RecordType};

const MAX_CNAME_DEPTH: u32 = 10;

/// UDP DNS 客户端
pub struct UdpDnsClient {
    server: SocketAddr,
    name: String,
    config: DnsConfig,
}

impl UdpDnsClient {
    pub fn new(server: SocketAddr, config: DnsConfig) -> Result<Self, DnsError> {
        Ok(Self {
            server,
            name: format!("UDP {}", server),
            config,
        })
    }

    async fn resolve_inner(&self, domain: &str) -> Result<Vec<IpAddr>, DnsError> {
        let mut last_err = None;

        for attempt in 0..=self.config.retry_count {
            if attempt > 0 {
                tokio::time::sleep(self.config.retry_interval).await;
            }

            match self.try_resolve(domain).await {
                Ok(ips) => return Ok(ips),
                Err(e) => {
                    last_err = Some(e);
                }
            }
        }

        Err(last_err.unwrap_or(DnsError::Timeout))
    }

    async fn try_resolve(&self, domain: &str) -> Result<Vec<IpAddr>, DnsError> {
        let server = self.server;
        let timeout = self.config.timeout;
        let domain = domain.to_string();

        tokio::task::spawn_blocking(move || {
            Self::resolve_blocking(&domain, server, timeout)
        })
        .await
        .map_err(|e| DnsError::Network(e.to_string()))?
    }

    fn resolve_blocking(
        domain: &str,
        server: SocketAddr,
        timeout: Duration,
    ) -> Result<Vec<IpAddr>, DnsError> {
        let socket = UdpSocket::bind("0.0.0.0:0")
            .map_err(|e| DnsError::Network(format!("绑定 UDP socket 失败: {}", e)))?;
        socket
            .set_read_timeout(Some(timeout))
            .map_err(|e| DnsError::Network(format!("设置超时失败: {}", e)))?;

        let mut all_ips = Vec::new();
        let mut visited = Vec::new();
        Self::resolve_recursive(&socket, server, domain, &mut all_ips, &mut visited, 0)?;

        Ok(all_ips)
    }

    fn resolve_recursive(
        socket: &UdpSocket,
        server: SocketAddr,
        domain: &str,
        all_ips: &mut Vec<IpAddr>,
        visited: &mut Vec<String>,
        depth: u32,
    ) -> Result<(), DnsError> {
        if depth >= MAX_CNAME_DEPTH {
            return Err(DnsError::ResolutionFailed("CNAME 链过长".to_string()));
        }

        if visited.contains(&domain.to_string()) {
            return Ok(());
        }
        visited.push(domain.to_string());

        // 查询 A 记录
        let query = build_query(domain, RecordType::A);
        socket
            .send_to(&query, server)
            .map_err(|e| DnsError::Network(format!("发送查询失败: {}", e)))?;

        let mut buf = [0u8; 512];
        let (len, _) = socket
            .recv_from(&mut buf)
            .map_err(|e| DnsError::Network(format!("接收响应失败: {}", e)))?;

        let response =
            parse_response(&buf[..len]).map_err(|e| DnsError::Network(format!("解析响应失败: {}", e)))?;

        let mut cname_target: Option<String> = None;

        for answer in &response.answers {
            match answer {
                super::dns_message::DnsRecord::A { addr, .. } => {
                    all_ips.push(IpAddr::V4(*addr));
                }
                super::dns_message::DnsRecord::CNAME { target, .. } => {
                    cname_target = Some(target.clone());
                }
                _ => {}
            }
        }

        // 查询 AAAA 记录（失败时忽略）
        let query = build_query(domain, RecordType::AAAA);
        if socket.send_to(&query, server).is_ok() {
            let mut buf = [0u8; 512];
            if let Ok((len, _)) = socket.recv_from(&mut buf) {
                if let Ok(response) = parse_response(&buf[..len]) {
                    for answer in &response.answers {
                        match answer {
                            super::dns_message::DnsRecord::AAAA { addr, .. } => {
                                all_ips.push(IpAddr::V6(*addr));
                            }
                            super::dns_message::DnsRecord::CNAME { target, .. } => {
                                cname_target = Some(target.clone());
                            }
                            _ => {}
                        }
                    }
                }
            }
        }

        // 如果有 CNAME，递归解析
        if let Some(cname) = cname_target {
            Self::resolve_recursive(socket, server, &cname, all_ips, visited, depth + 1)?;
        }

        Ok(())
    }
}

#[async_trait]
impl DnsClient for UdpDnsClient {
    async fn resolve(&self, domain: &str) -> Result<DnsResult, DnsError> {
        let ips = self.resolve_inner(domain).await?;
        Ok(DnsResult::new(domain).with_ips(ips))
    }

    fn name(&self) -> &str {
        &self.name
    }
}
