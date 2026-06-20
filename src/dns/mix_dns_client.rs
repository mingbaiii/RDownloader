use std::collections::HashSet;
use std::net::IpAddr;
use std::sync::Arc;

use async_trait::async_trait;

use super::dns_client::{DnsClient, DnsError, DnsResult};

/// 混合 DNS 客户端
/// 使用多个不同的 DNS 客户端进行解析，合并去重结果
pub struct MixDnsClient {
    clients: Vec<Arc<dyn DnsClient>>,
}

impl MixDnsClient {
    pub fn new(clients: Vec<Arc<dyn DnsClient>>) -> Self {
        Self { clients }
    }

    async fn resolve_inner(&self, domain: &str) -> Result<Vec<IpAddr>, DnsError> {
        let mut tasks = Vec::new();

        for client in &self.clients {
            let client = client.clone();
            let domain = domain.to_string();
            tasks.push(tokio::spawn(async move {
                let name = client.name().to_string();
                let result = client.resolve(&domain).await;
                (name, result)
            }));
        }

        let mut all_ips = HashSet::new();
        let mut errors = Vec::new();

        for task in tasks {
            match task.await {
                Ok((name, Ok(result))) => {
                    all_ips.extend(result.ips);
                }
                Ok((name, Err(e))) => {
                    errors.push(e);
                }
                Err(e) => {
                    errors.push(DnsError::Network(e.to_string()));
                }
            }
        }

        // 如果所有客户端都失败，返回错误
        if all_ips.is_empty() && !errors.is_empty() {
            return Err(errors.into_iter().next().unwrap());
        }

        Ok(all_ips.into_iter().collect())
    }
}

#[async_trait]
impl DnsClient for MixDnsClient {
    async fn resolve(&self, domain: &str) -> Result<DnsResult, DnsError> {
        let ips = self.resolve_inner(domain).await?;
        Ok(DnsResult::new(domain).with_ips(ips))
    }

    fn name(&self) -> &str {
        "Mix DNS"
    }
}
