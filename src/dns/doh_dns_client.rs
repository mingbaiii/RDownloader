use std::net::IpAddr;

use async_trait::async_trait;
use base64::Engine;
use reqwest::Client;

use super::dns_client::{DnsClient, DnsConfig, DnsError, DnsResult};
use super::dns_message::{build_query, parse_response, RecordType};

const MAX_CNAME_DEPTH: u32 = 10;

/// DNS over HTTPS 客户端
pub struct DohDnsClient {
    url: String,
    client: Client,
    name: String,
    config: DnsConfig,
}

impl DohDnsClient {
    pub fn new(url: String, config: DnsConfig) -> Result<Self, DnsError> {
        let mut client_builder = Client::builder()
            .timeout(config.timeout)
            .danger_accept_invalid_certs(true);

        // 如果配置了代理，使用代理
        if let Some(proxy_url) = &config.proxy {
            let proxy = reqwest::Proxy::all(proxy_url)
                .map_err(|e| DnsError::Network(e.to_string()))?;
            client_builder = client_builder.proxy(proxy);
        }

        let client = client_builder
            .build()
            .map_err(|e| DnsError::Network(e.to_string()))?;

        // 从 URL 提取主机名作为名称，添加 DOH 前缀
        let host = url
            .trim_start_matches("https://")
            .trim_start_matches("http://")
            .split('/')
            .next()
            .unwrap_or(&url);
        let name = format!("DOH {}", host);

        Ok(Self {
            url,
            client,
            name,
            config,
        })
    }

    /// 使用 GET 方法查询 DNS 记录
    async fn query_record_get(
        &self,
        domain: &str,
        record_type: RecordType,
    ) -> Result<Vec<IpAddr>, DnsError> {
        let query = build_query(domain, record_type);
        let encoded = base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(&query);

        let response = self
            .client
            .get(&self.url)
            .header("Accept", "application/dns-message")
            .query(&[("dns", &encoded)])
            .send()
            .await
            .map_err(|e| DnsError::Network(e.to_string()))?;

        if !response.status().is_success() {
            return Err(DnsError::Network(format!("HTTP 错误: {}", response.status())));
        }

        let body = response
            .bytes()
            .await
            .map_err(|e| DnsError::Network(e.to_string()))?;

        let dns_response =
            parse_response(&body).map_err(|e| DnsError::Network(format!("解析 DNS 响应失败: {}", e)))?;

        let mut ips = Vec::new();
        for answer in &dns_response.answers {
            match answer {
                super::dns_message::DnsRecord::A { addr, .. } => {
                    ips.push(IpAddr::V4(*addr));
                }
                super::dns_message::DnsRecord::AAAA { addr, .. } => {
                    ips.push(IpAddr::V6(*addr));
                }
                _ => {}
            }
        }

        Ok(ips)
    }

    /// 使用 POST 方法查询 DNS 记录
    async fn query_record_post(
        &self,
        domain: &str,
        record_type: RecordType,
    ) -> Result<Vec<IpAddr>, DnsError> {
        let query = build_query(domain, record_type);

        let response = self
            .client
            .post(&self.url)
            .header("Accept", "application/dns-message")
            .header("Content-Type", "application/dns-message")
            .body(query)
            .send()
            .await
            .map_err(|e| DnsError::Network(e.to_string()))?;

        if !response.status().is_success() {
            return Err(DnsError::Network(format!("HTTP 错误: {}", response.status())));
        }

        let body = response
            .bytes()
            .await
            .map_err(|e| DnsError::Network(e.to_string()))?;

        let dns_response =
            parse_response(&body).map_err(|e| DnsError::Network(format!("解析 DNS 响应失败: {}", e)))?;

        let mut ips = Vec::new();
        for answer in &dns_response.answers {
            match answer {
                super::dns_message::DnsRecord::A { addr, .. } => {
                    ips.push(IpAddr::V4(*addr));
                }
                super::dns_message::DnsRecord::AAAA { addr, .. } => {
                    ips.push(IpAddr::V6(*addr));
                }
                _ => {}
            }
        }

        Ok(ips)
    }

    /// 查询 DNS 记录（先尝试 GET，失败则尝试 POST）
    async fn query_record(
        &self,
        domain: &str,
        record_type: RecordType,
    ) -> Result<Vec<IpAddr>, DnsError> {
        // 先尝试 GET 方法
        match self.query_record_get(domain, record_type).await {
            Ok(ips) => return Ok(ips),
            Err(_) => {
                // GET 失败，尝试 POST 方法
                self.query_record_post(domain, record_type).await
            }
        }
    }

    async fn query_cname(&self, domain: &str) -> Result<Option<String>, DnsError> {
        let query = build_query(domain, RecordType::A);
        let encoded = base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(&query);

        let response = self
            .client
            .get(&self.url)
            .header("Accept", "application/dns-message")
            .query(&[("dns", &encoded)])
            .send()
            .await
            .map_err(|e| DnsError::Network(e.to_string()))?;

        if !response.status().is_success() {
            return Err(DnsError::Network(format!("HTTP 错误: {}", response.status())));
        }

        let body = response
            .bytes()
            .await
            .map_err(|e| DnsError::Network(e.to_string()))?;

        let dns_response =
            parse_response(&body).map_err(|e| DnsError::Network(format!("解析 DNS 响应失败: {}", e)))?;

        for answer in &dns_response.answers {
            if let super::dns_message::DnsRecord::CNAME { target, .. } = answer {
                return Ok(Some(target.clone()));
            }
        }

        Ok(None)
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
        let mut all_ips = Vec::new();
        let mut visited = Vec::new();
        self.resolve_recursive(domain, &mut all_ips, &mut visited, 0)
            .await?;

        Ok(all_ips)
    }

    async fn resolve_recursive(
        &self,
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
        let a_ips = self.query_record(domain, RecordType::A).await?;
        all_ips.extend(a_ips);

        // 查询 AAAA 记录（失败时忽略）
        if let Ok(aaaa_ips) = self.query_record(domain, RecordType::AAAA).await {
            all_ips.extend(aaaa_ips);
        }

        // 查询 CNAME
        if let Some(cname) = self.query_cname(domain).await? {
            Box::pin(self.resolve_recursive(&cname, all_ips, visited, depth + 1)).await?;
        }

        Ok(())
    }
}

#[async_trait]
impl DnsClient for DohDnsClient {
    async fn resolve(&self, domain: &str) -> Result<DnsResult, DnsError> {
        let ips = self.resolve_inner(domain).await?;
        Ok(DnsResult::new(domain).with_ips(ips))
    }

    fn name(&self) -> &str {
        &self.name
    }
}
