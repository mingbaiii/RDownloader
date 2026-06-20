use std::collections::HashMap;
use std::net::IpAddr;
use std::time::Duration;

use reqwest::Client;

use crate::endpoint_test::endpoint::EndpointList;

/// 速度测试结果
#[derive(Debug, Clone)]
pub struct SpeedResult {
    pub ip: IpAddr,
    pub success: bool,
    pub status_code: Option<u16>,
    pub header_size: Option<usize>,   // 响应头大小（字节）
    pub latency: Option<Duration>,    // 请求延迟
    pub speed: Option<f64>,           // 传输速度（字节/秒）
    pub response_headers: HashMap<String, String>, // 响应头
    pub final_url: Option<String>,    // 重定向后的最终 URL
}

/// 速度测试配置
#[derive(Debug, Clone)]
pub struct SpeedTestConfig {
    /// 超时时间
    pub timeout: Duration,
    /// HTTP 测试主机名
    pub host: String,
    /// HTTP 测试端口
    pub port: u16,
    /// HTTP 测试路径
    pub path: String,
    /// 是否使用 HTTPS
    pub https: bool,
    /// 自定义请求头
    pub headers: HashMap<String, String>,
    /// 重试次数
    pub retry_count: u32,
    /// 代理 URL（可选）
    pub proxy_url: Option<String>,
}

impl SpeedTestConfig {
    pub fn new(host: impl Into<String>, port: u16, path: impl Into<String>, https: bool) -> Self {
        Self {
            timeout: Duration::from_secs(10),
            host: host.into(),
            port,
            path: path.into(),
            https,
            headers: HashMap::new(),
            retry_count: 3,
            proxy_url: None,
        }
    }

    pub fn timeout(mut self, timeout: Duration) -> Self {
        self.timeout = timeout;
        self
    }

    pub fn headers(mut self, headers: HashMap<String, String>) -> Self {
        self.headers = headers;
        self
    }

    pub fn retry_count(mut self, count: u32) -> Self {
        self.retry_count = count;
        self
    }

    pub fn proxy_url(mut self, proxy: Option<String>) -> Self {
        self.proxy_url = proxy;
        self
    }
}

/// 测试单个 IP 的速度
pub async fn test_speed(ip: IpAddr, config: &SpeedTestConfig) -> SpeedResult {
    let scheme = if config.https { "https" } else { "http" };
    let port_str = if config.port == 443 && config.https || config.port == 80 && !config.https {
        String::new()
    } else {
        format!(":{}", config.port)
    };
    let url = format!("{}://{}{}{}", scheme, config.host, port_str, config.path);

    let addr = std::net::SocketAddr::new(ip, config.port);

    let mut client_builder = Client::builder()
        .timeout(config.timeout)
        .danger_accept_invalid_certs(true)
        .redirect(reqwest::redirect::Policy::limited(10))
        .resolve(&config.host, addr);

    if let Some(ref proxy_url) = config.proxy_url {
        match reqwest::Proxy::all(proxy_url) {
            Ok(proxy) => {
                client_builder = client_builder.proxy(proxy);
            }
            Err(e) => {
                eprintln!("Warning: Invalid proxy URL '{}' for speed test: {}", proxy_url, e);
            }
        }
    }

    let client = match client_builder.build()
    {
        Ok(c) => c,
        Err(_) => {
            return SpeedResult {
                ip,
                success: false,
                status_code: None,
                header_size: None,
                latency: None,
                speed: None,
                response_headers: HashMap::new(),
                final_url: None,
            }
        }
    };

    for attempt in 0..=config.retry_count {
        if attempt > 0 {
            let delay_ms = 500u64 * (1u64 << (attempt - 1)); // 500ms, 1s, 2s, ...
            tokio::time::sleep(Duration::from_millis(delay_ms)).await;
        }

        let start = std::time::Instant::now();
        let mut request = client.head(&url);

        for (key, value) in &config.headers {
            request = request.header(key, value);
        }

        let result = request.send().await;
        let header_received_time = start.elapsed();

        match result {
            Ok(response) => {
                let status_code = response.status().as_u16();
                let final_url = response.url().to_string();

                let is_error_page = response.headers()
                    .get("content-type")
                    .and_then(|v| v.to_str().ok())
                    .map(|ct| ct.contains("text/html"))
                    .unwrap_or(false);

                if is_error_page && status_code == 200 {
                    if attempt < config.retry_count {
                        continue;
                    }
                    return SpeedResult {
                        ip,
                        success: false,
                        status_code: Some(status_code),
                        header_size: None,
                        latency: Some(header_received_time),
                        speed: None,
                        response_headers: HashMap::new(),
                        final_url: Some(final_url),
                    };
                }

                let header_size = calculate_header_size(&response);

                let mut response_headers = HashMap::new();
                for (name, value) in response.headers() {
                    if let Ok(v) = value.to_str() {
                        // 归一化为小写，HTTP header 名称大小写不敏感
                        response_headers.insert(name.as_str().to_lowercase(), v.to_string());
                    }
                }

                let speed = if header_received_time.as_secs_f64() > 0.0 {
                    Some(header_size as f64 / header_received_time.as_secs_f64())
                } else {
                    None
                };

                return SpeedResult {
                    ip,
                    success: true,
                    status_code: Some(status_code),
                    header_size: Some(header_size),
                    latency: Some(header_received_time),
                    speed,
                    response_headers,
                    final_url: Some(final_url),
                };
            }
            Err(_) => {
                if attempt < config.retry_count {
                    continue;
                }
                return SpeedResult {
                    ip,
                    success: false,
                    status_code: None,
                    header_size: None,
                    latency: Some(header_received_time),
                    speed: None,
                    response_headers: HashMap::new(),
                    final_url: None,
                };
            }
        }
    }

    // Should never reach here, but just in case
    SpeedResult {
        ip,
        success: false,
        status_code: None,
        header_size: None,
        latency: None,
        speed: None,
        response_headers: HashMap::new(),
        final_url: None,
    }
}

/// 测试 EndpointList 中所有 IP 的速度（并行测试）
pub async fn test_speed_list(endpoint_list: &mut EndpointList, config: &SpeedTestConfig) {
    // 收集所有 IP
    let ips: Vec<IpAddr> = endpoint_list.endpoints.iter().map(|e| e.ip).collect();

    // 并行测试所有 IP
    let mut tasks = Vec::new();
    for ip in ips {
        let config = config.clone();
        tasks.push(tokio::spawn(async move { test_speed(ip, &config).await }));
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
        // 如果绑定了 RemoteFile，更新响应头
        if !result.response_headers.is_empty() {
            endpoint_list.update_remote_file_headers(result.response_headers.clone()).await;
        }

        if let Some(endpoint) = endpoint_list.endpoints.iter_mut().find(|e| e.ip == result.ip) {
            endpoint.speed = Some(result);
        }
    }
}

/// 计算响应头大小
fn calculate_header_size(response: &reqwest::Response) -> usize {
    let mut size = 0;

    // 状态行: "HTTP/1.1 200 OK\r\n"
    size += format!("HTTP/1.1 {}\r\n", response.status()).len();

    // 响应头
    for (name, value) in response.headers() {
        // "Name: Value\r\n"
        size += name.as_str().len();
        size += 2; // ": "
        if let Ok(v) = value.to_str() {
            size += v.len();
        }
        size += 2; // "\r\n"
    }

    size += 2; // 结尾空行 "\r\n"

    size
}

/// 格式化速度测试结果
pub fn format_speed_results(results: &[SpeedResult]) -> String {
    let mut output = String::from("速度测试结果\n");
    output.push_str(&format!("{:-<70}\n", ""));
    output.push_str(&format!("{:<20} {:<8} {:<12} {:<15} {:<15}\n", "IP", "状态", "头大小", "延迟", "速度"));
    output.push_str(&format!("{:-<70}\n", ""));

    for result in results {
        let ip_str = format!("{}", result.ip);
        let status_str = if result.success {
            format!("{}", result.status_code.unwrap_or(0))
        } else {
            "失败".to_string()
        };
        let header_str = result
            .header_size
            .map(|s| format!("{} B", s))
            .unwrap_or_else(|| "-".to_string());
        let latency_str = result
            .latency
            .map(|l| format!("{:?}", l))
            .unwrap_or_else(|| "-".to_string());
        let speed_str = result
            .speed
            .map(|s| format_speed(s))
            .unwrap_or_else(|| "-".to_string());

        output.push_str(&format!(
            "{:<20} {:<8} {:<12} {:<15} {:<15}\n",
            ip_str, status_str, header_str, latency_str, speed_str
        ));
    }

    output
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
