use std::collections::HashMap;
use std::net::IpAddr;
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::RwLock;

use crate::config::filter_ips_by_config;
use crate::dns::{create_mix_dns_client_with_proxy_and_config, DnsConfig};
use crate::downloader::DownloadHandle;
use crate::endpoint_test::{
    test_connectivity, test_speed_list, EndpointList, SpeedTestConfig, TestConfig,
};
use crate::utils::parse_url;

/// 远程文件信息
#[derive(Debug, Clone)]
pub struct RemoteFile {
    /// URL（重定向后的 URL，保持 hostname 格式）
    pub url: String,
    /// 原始 URL（用户提供的，不随重定向改变）
    pub original_url: String,
    /// 请求头
    pub request_headers: HashMap<String, String>,
    /// 响应头
    pub response_headers: HashMap<String, String>,
    /// 代理设置: key 为 IP 或 "dns:xxx"，value 为代理地址
    pub proxy_map: HashMap<String, String>,
    /// Endpoint 列表
    pub endpoint_list: Option<EndpointList>,
}

impl RemoteFile {
    /// 创建新的 RemoteFile
    pub fn new(url: impl Into<String>) -> Self {
        let url_str = url.into();
        Self {
            url: url_str.clone(),
            original_url: url_str,
            request_headers: HashMap::new(),
            response_headers: HashMap::new(),
            proxy_map: HashMap::new(),
            endpoint_list: None,
        }
    }

    /// 设置代理
    pub fn with_proxy(mut self, key: impl Into<String>, proxy: impl Into<String>) -> Self {
        self.proxy_map.insert(key.into(), proxy.into());
        self
    }

    /// 设置全局代理（所有 endpoint 使用同一个代理）
    pub fn with_global_proxy(mut self, proxy: impl Into<String>) -> Self {
        self.proxy_map.insert("*".to_string(), proxy.into());
        self
    }

    /// 设置所有 DNS 代理
    pub fn with_all_dns_proxy(mut self, proxy: impl Into<String>) -> Self {
        self.proxy_map.insert("dns:*".to_string(), proxy.into());
        self
    }

    /// 设置单个 DNS 代理
    pub fn with_dns_proxy(mut self, dns_server: impl Into<String>, proxy: impl Into<String>) -> Self {
        let key = format!("dns:{}", dns_server.into());
        self.proxy_map.insert(key, proxy.into());
        self
    }

    /// 添加请求头
    pub fn with_request_header(mut self, key: impl Into<String>, value: impl Into<String>) -> Self {
        self.request_headers.insert(key.into(), value.into());
        self
    }

    /// 设置请求头
    pub fn set_request_header(&mut self, key: impl Into<String>, value: impl Into<String>) {
        self.request_headers.insert(key.into(), value.into());
    }

    /// 获取响应头
    pub fn get_response_header(&self, key: &str) -> Option<&String> {
        self.response_headers.get(key)
    }

    /// 更新响应头
    pub fn update_response_headers(&mut self, headers: HashMap<String, String>) {
        self.response_headers.extend(headers);
    }

    /// 获取 endpoint 的代理
    pub fn get_endpoint_proxy(&self, ip: &IpAddr) -> Option<&String> {
        // 先查找特定 IP 的代理
        if let Some(proxy) = self.proxy_map.get(&ip.to_string()) {
            return Some(proxy);
        }
        // 再查找全局代理
        self.proxy_map.get("*")
    }

    /// 获取 DNS 代理
    pub fn get_dns_proxy(&self, dns_server: &str) -> Option<&String> {
        let key = format!("dns:{}", dns_server);
        self.proxy_map.get(&key)
    }

    /// 创建速度测试配置（使用全局配置中的重试次数和超时）
    async fn create_speed_test_config(&self, host: &str, port: u16, path: &str, https: bool) -> SpeedTestConfig {
        let gcfg = crate::config::get_global_config().await;
        // 获取下载代理（全局代理 "*"）
        let download_proxy = self.proxy_map.get("*").cloned();
        SpeedTestConfig::new(host, port, path, https)
            .timeout(Duration::from_secs(gcfg.speed_test_timeout_secs))
            .retry_count(gcfg.speed_test_retry_count)
            .headers(self.request_headers.clone())
            .proxy_url(download_proxy)
    }

    /// 获取信息：DNS 查询，连通性测试，速度测试
    pub async fn get_information(&mut self) -> Result<(), String> {
        let url_info = parse_url(&self.url)?;

        // 读取配置中的 DNS 服务器列表
        let config = crate::config::get_global_config().await;
        let dns_servers: Vec<String> = config.dns_servers.clone();
        let dns_servers_ref: Vec<&str> = dns_servers.iter().map(|s| s.as_str()).collect();
        let connectivity_timeout = config.connectivity_timeout_secs;
        let dns_timeout = config.dns_timeout_secs;
        let dns_retry = config.dns_retry_count;
        drop(config);

        // 检查 URL 的 host 是否是 IP
        let is_ip_url = url_info.host.parse::<std::net::IpAddr>().is_ok();

        // 创建 EndpointList
        let mut endpoint_list = if is_ip_url {
            // IP URL：直接使用该 IP 作为唯一的 endpoint
            let ip: std::net::IpAddr = url_info.host.parse().unwrap();
            let mut list = EndpointList::new(&self.url);
            list.add_endpoint(ip);
            list
        } else {
            // hostname URL：进行 DNS 解析

            // 构建 DNS 代理映射
            let mut dns_proxy_map = HashMap::new();
            let mut all_dns_proxy = None;

            for (key, proxy) in &self.proxy_map {
                if key == "dns:*" {
                    // 所有 DNS 使用同一个代理
                    all_dns_proxy = Some(proxy.clone());
                } else if let Some(dns_server) = key.strip_prefix("dns:") {
                    // 特定 DNS 使用代理
                    dns_proxy_map.insert(dns_server.to_string(), proxy.clone());
                }
            }

            // 如果设置了 dns:*，为所有 DNS 服务器设置代理
            if let Some(proxy) = all_dns_proxy {
                for server in &dns_servers {
                    dns_proxy_map.entry(server.clone()).or_insert_with(|| proxy.clone());
                }
            }

            // DNS 解析
            let dns_base_config = crate::dns::DnsConfig::default()
                .timeout(Duration::from_secs(dns_timeout))
                .retry_count(dns_retry);
            let dns_client = create_mix_dns_client_with_proxy_and_config(
                &dns_servers_ref,
                &dns_proxy_map,
                dns_base_config,
            )
            .map_err(|e| format!("创建 DNS 客户端失败: {}", e))?;

            let mut dns_result = dns_client
                .resolve(&url_info.host)
                .await
                .map_err(|e| format!("DNS 解析失败: {}", e))?;

            // 根据全局配置过滤 IP
            dns_result.ips = filter_ips_by_config(&dns_result.ips).await;

            if dns_result.ips.is_empty() {
                return Err("DNS 解析未返回任何 IP".to_string());
            }

            EndpointList::from_dns_result(&dns_result)
        };

        // 连通性测试
        let conn_config = TestConfig::new()
            .timeout(Duration::from_secs(connectivity_timeout))
            .test_icmp_ping(true)
            .test_tcp_ping(true)
            .tcp_ports(url_info.ports.clone());

        test_connectivity(&mut endpoint_list, &conn_config).await;

        // 速度测试（使用代理）
        let speed_config = self.create_speed_test_config(
            &url_info.host,
            url_info.ports.first().copied().unwrap_or(if url_info.https { 443 } else { 80 }),
            &url_info.path,
            url_info.https,
        ).await;

        test_speed_list(&mut endpoint_list, &speed_config).await;

        // 检查是否有真正的重定向（host 不同，不是 IP vs hostname 的区别）
        let original_host = parse_url(&self.url).map(|u| u.host).unwrap_or_default();
        let mut final_url = None;
        for ep in &endpoint_list.endpoints {
            if let Some(speed) = &ep.speed {
                if let Some(url) = &speed.final_url {
                    if url != &self.url {
                        // 检查是否是真正的重定向（host 不同）
                        if let Ok(final_info) = parse_url(url) {
                            let final_host = &final_info.host;
                            // 如果 host 不同（排除 IP vs hostname 的情况）
                            if final_host != &original_host
                                && final_host.parse::<std::net::IpAddr>().is_err()
                                && original_host.parse::<std::net::IpAddr>().is_err()
                            {
                                final_url = Some(url.clone());
                                break;
                            }
                            // 如果两个都是 hostname 且不同，是真正的重定向
                            // 如果一个是 IP 一个是 hostname，不是真正的重定向
                        }
                    }
                }
            }
        }

        // 如果有重定向，更新 URL 并重新解析
        if let Some(new_url) = final_url {
            eprintln!("重定向到: {}", new_url);
            self.url = new_url;

            // 重新解析 URL
            let new_url_info = parse_url(&self.url)?;

            // 检查重定向后的 URL 是否是 IP
            let new_is_ip = new_url_info.host.parse::<std::net::IpAddr>().is_ok();

            if new_is_ip {
                // IP URL：直接使用该 IP
                let ip: std::net::IpAddr = new_url_info.host.parse().unwrap();
                let mut new_endpoint_list = EndpointList::new(&self.url);
                new_endpoint_list.add_endpoint(ip);

                // 速度测试
                let speed_config = self.create_speed_test_config(
                    &new_url_info.host,
                    new_url_info.ports.first().copied().unwrap_or(if new_url_info.https { 443 } else { 80 }),
                    &new_url_info.path,
                    new_url_info.https,
                ).await;

                test_speed_list(&mut new_endpoint_list, &speed_config).await;

                // 从第一个成功的 endpoint 获取响应头
                if let Some(endpoint) = new_endpoint_list.endpoints.first() {
                    if let Some(speed) = &endpoint.speed {
                        self.response_headers.extend(speed.response_headers.clone());
                    }
                }

                self.endpoint_list = Some(new_endpoint_list);
                return Ok(());
            }

            // hostname URL：进行 DNS 解析
            // 重新构建 DNS 客户端
            let mut dns_proxy_map = HashMap::new();
            let mut all_dns_proxy = None;
            for (key, proxy) in &self.proxy_map {
                if key == "dns:*" {
                    all_dns_proxy = Some(proxy.clone());
                } else if let Some(dns_server) = key.strip_prefix("dns:") {
                    dns_proxy_map.insert(dns_server.to_string(), proxy.clone());
                }
            }
            if let Some(proxy) = all_dns_proxy {
                for server in &dns_servers {
                    dns_proxy_map.entry(server.clone()).or_insert_with(|| proxy.clone());
                }
            }
            let dns_base_config = crate::dns::DnsConfig::default()
                .timeout(Duration::from_secs(dns_timeout))
                .retry_count(dns_retry);
            let dns_client = create_mix_dns_client_with_proxy_and_config(
                &dns_servers_ref,
                &dns_proxy_map,
                dns_base_config,
            )
            .map_err(|e| format!("创建 DNS 客户端失败: {}", e))?;

            // 重新 DNS 解析
            let new_dns_result = dns_client
                .resolve(&new_url_info.host)
                .await
                .map_err(|e| format!("DNS 解析失败: {}", e))?;

            let mut new_dns_result = new_dns_result;
            new_dns_result.ips = filter_ips_by_config(&new_dns_result.ips).await;

            if !new_dns_result.ips.is_empty() {
                // 创建新的 EndpointList
                let mut new_endpoint_list = EndpointList::from_dns_result(&new_dns_result);

                // 连通性测试
                let conn_config = TestConfig::new()
                    .timeout(Duration::from_secs(connectivity_timeout))
                    .test_icmp_ping(true)
                    .test_tcp_ping(true)
                    .tcp_ports(new_url_info.ports.clone());

                test_connectivity(&mut new_endpoint_list, &conn_config).await;

                // 速度测试
                let speed_config = self.create_speed_test_config(
                    &new_url_info.host,
                    new_url_info.ports.first().copied().unwrap_or(if new_url_info.https { 443 } else { 80 }),
                    &new_url_info.path,
                    new_url_info.https,
                ).await;

                test_speed_list(&mut new_endpoint_list, &speed_config).await;

                // 按延迟排序
                new_endpoint_list.sort_by_latency();

                // 从第一个成功的 endpoint 获取响应头
                if let Some(endpoint) = new_endpoint_list.endpoints.first() {
                    if let Some(speed) = &endpoint.speed {
                        self.response_headers.extend(speed.response_headers.clone());
                    }
                }

                self.endpoint_list = Some(new_endpoint_list);
                return Ok(());
            }
        }

        // 按延迟排序
        endpoint_list.sort_by_latency();

        // 从第一个成功的 endpoint 获取响应头
        if let Some(endpoint) = endpoint_list.endpoints.first() {
            if let Some(speed) = &endpoint.speed {
                self.response_headers.extend(speed.response_headers.clone());
            }
        }

        self.endpoint_list = Some(endpoint_list);

        Ok(())
    }

    /// 开始下载文件
    pub async fn download(
        &self,
        output_path: &str,
        file_size: u64,
        download_id: &str,
        saved_segments: Option<Vec<crate::cli::state::SegmentProgress>>,
        saved_connections: Option<usize>,
        original_url: &str,
    ) -> Result<DownloadHandle, String> {
        let endpoint_list = self.endpoint_list.as_ref()
            .ok_or("Endpoint list not available. Call get_information() first.")?;

        crate::downloader::start_download(file_size, output_path, self, endpoint_list, download_id, saved_segments, saved_connections, None, original_url, &self.url).await
    }
}

/// RemoteFile 的共享引用类型
pub type SharedRemoteFile = Arc<RwLock<RemoteFile>>;

/// 创建共享的 RemoteFile
pub fn create_shared_remote_file(url: impl Into<String>) -> SharedRemoteFile {
    Arc::new(RwLock::new(RemoteFile::new(url)))
}
