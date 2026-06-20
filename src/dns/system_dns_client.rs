use std::net::{IpAddr, ToSocketAddrs};

use async_trait::async_trait;

use super::dns_client::{DnsClient, DnsConfig, DnsError, DnsResult};

/// 系统 DNS 客户端
/// 使用操作系统内置的 DNS 解析器
pub struct SystemDnsClient {
    config: DnsConfig,
}

impl SystemDnsClient {
    pub fn new(config: DnsConfig) -> Self {
        Self { config }
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
        let domain = domain.to_string();
        let _timeout = self.config.timeout;

        // 使用 tokio::task::spawn_blocking 在阻塞线程中执行系统 DNS 解析
        let result = tokio::task::spawn_blocking(move || {
            // ToSocketAddrs 会使用系统 DNS 解析
            // 使用端口 0 作为占位符
            let addr_str = format!("{}:0", domain);

            // 设置超时（通过系统调用可能无法直接控制，这里依赖系统设置）
            let addrs: Vec<IpAddr> = addr_str
                .to_socket_addrs()
                .map_err(|e| DnsError::Network(format!("系统 DNS 解析失败: {}", e)))?
                .map(|addr| addr.ip())
                .collect();

            if addrs.is_empty() {
                return Err(DnsError::ResolutionFailed("未找到 IP 地址".to_string()));
            }

            Ok(addrs)
        })
        .await
        .map_err(|e| DnsError::Network(e.to_string()))?;

        result
    }
}

#[async_trait]
impl DnsClient for SystemDnsClient {
    async fn resolve(&self, domain: &str) -> Result<DnsResult, DnsError> {
        let ips = self.resolve_inner(domain).await?;
        Ok(DnsResult::new(domain).with_ips(ips))
    }

    fn name(&self) -> &str {
        "System DNS"
    }
}
