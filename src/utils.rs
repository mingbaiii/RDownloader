use std::net::IpAddr;
use std::sync::LazyLock;
use std::time::Duration;

use url::Url;

use crate::config::filter_ips_by_config;
use crate::dns::create_mix_dns_client;
use crate::endpoint_test::{
    test_connectivity, test_speed_list, EndpointList, SpeedTestConfig, TestConfig,
};

/// 默认 DNS 服务器列表
pub static DEFAULT_DNS_SERVERS: LazyLock<Vec<&'static str>> = LazyLock::new(|| {
    vec![
        "system",
        "114.114.114.114",
        "https://223.5.5.5/dns-query"
    ]
});

/// Endpoint 信息
#[derive(Debug, Clone)]
pub struct Endpoint {
    pub ip: IpAddr,
    pub latency: Option<Duration>,
}

/// URL 解析结果
pub struct UrlInfo {
    pub host: String,
    pub ports: Vec<u16>,
    pub path: String,
    pub https: bool,
}

/// 从 URL 中提取主机名、端口和路径
pub fn parse_url(url: &str) -> Result<UrlInfo, String> {
    let parsed = Url::parse(url).map_err(|e| format!("URL 解析失败: {}", e))?;

    let host = parsed
        .host_str()
        .ok_or_else(|| "URL 中没有主机名".to_string())?
        .to_string();

    // 根据 URL 和协议确定测试端口
    let ports = if let Some(port) = parsed.port() {
        vec![port]
    } else {
        match parsed.scheme() {
            "https" => vec![443],
            "http" => vec![80],
            _ => vec![80, 443],
        }
    };

    // 提取路径（包含查询参数）
    let path = if parsed.path().is_empty() {
        "/".to_string()
    } else {
        let mut path = parsed.path().to_string();
        if let Some(query) = parsed.query() {
            path.push('?');
            path.push_str(query);
        }
        path
    };

    let https = parsed.scheme() == "https";

    Ok(UrlInfo {
        host,
        ports,
        path,
        https,
    })
}

/// 查找所有可用的 endpoint
/// 传入 URL，自动提取主机名，解析 DNS，测试连通性，按延迟排序返回
pub async fn find_all_available_endpoints(url: &str) -> Result<EndpointList, String> {
    let url_info = parse_url(url)?;

    // 检查是否是 IP 地址
    if let Ok(_ip) = url_info.host.parse::<IpAddr>() {
        // TODO: 处理直接 IP 的情况
        return Err("暂不支持直接 IP 地址".to_string());
    }

    // 使用 DNS 解析域名
    let dns_client = create_mix_dns_client(&DEFAULT_DNS_SERVERS)
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

    // 创建 EndpointList
    let mut endpoint_list = EndpointList::from_dns_result(&dns_result);

    // 连通性测试和速度测试的全局配置
    let (conn_timeout, speed_timeout, speed_retry, custom_headers, download_proxy) = {
        let gcfg = crate::config::get_global_config().await;
        (gcfg.connectivity_timeout_secs, gcfg.speed_test_timeout_secs, gcfg.speed_test_retry_count, gcfg.headers.clone(), gcfg.proxy.get("*").cloned())
    };

    let conn_config = TestConfig::new()
        .timeout(Duration::from_secs(conn_timeout))
        .test_icmp_ping(true)
        .test_tcp_ping(true)
        .tcp_ports(url_info.ports.clone());

    test_connectivity(&mut endpoint_list, &conn_config).await;

    // 速度测试
    let speed_config = SpeedTestConfig::new(
        &url_info.host,
        url_info.ports.first().copied().unwrap_or(if url_info.https { 443 } else { 80 }),
        &url_info.path,
        url_info.https,
    )
    .timeout(Duration::from_secs(speed_timeout))
    .retry_count(speed_retry)
    .headers(custom_headers)
    .proxy_url(download_proxy);

    test_speed_list(&mut endpoint_list, &speed_config).await;

    // 按延迟排序
    endpoint_list.sort_by_latency();

    Ok(endpoint_list)
}

/// 从 Content-Disposition 响应头中提取 filename
/// 支持两种格式:
///   Content-Disposition: attachment; filename="file.zip"
///   Content-Disposition: attachment; filename=file.zip
pub fn extract_filename_from_content_disposition(header_value: &str) -> Option<String> {
    // 查找 filename= 或 filename*= (RFC 5987)
    let lower = header_value.to_lowercase();

    // 优先处理 filename*= (RFC 5987 编码)
    if let Some(pos) = lower.find("filename*=") {
        let rest = &header_value[pos + 10..];
        // 格式: UTF-8''filename
        if let Some(quote_end) = rest.find('\'') {
            if let Some(second_quote) = rest[quote_end + 1..].find('\'') {
                let encoded = &rest[quote_end + second_quote + 2..];
                let value = encoded.trim_matches('"').trim();
                if !value.is_empty() {
                    return Some(urlencoding(value.to_string()));
                }
            }
        }
    }

    // 普通 filename=
    if let Some(pos) = lower.find("filename=") {
        let rest = &header_value[pos + 9..];
        let value = rest.trim();

        // 去掉引号
        let value = if value.starts_with('"') {
            let end = value[1..].find('"').map(|i| i + 1).unwrap_or(value.len());
            &value[1..end]
        } else {
            // 分号分隔
            if let Some(semi) = value.find(';') {
                &value[..semi]
            } else {
                value
            }
        };

        let value = value.trim();
        if !value.is_empty() {
            return Some(value.to_string());
        }
    }

    None
}

/// URL 解码（简单版本，处理 %20 等编码）
fn urlencoding(s: String) -> String {
    let mut result = String::with_capacity(s.len());
    let bytes = s.as_bytes();
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' && i + 2 < bytes.len() {
            if let Ok(hex) = u8::from_str_radix(&s[i + 1..i + 3], 16) {
                result.push(hex as char);
                i += 3;
                continue;
            }
        }
        result.push(bytes[i] as char);
        i += 1;
    }
    result
}
