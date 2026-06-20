use std::collections::HashMap;
use std::sync::atomic::Ordering;
use std::sync::Arc;

use tokio::sync::Mutex;

use crate::config::GLOBAL_CONFIG;
use crate::endpoint_test::EndpointList;
use crate::remote_file::RemoteFile;

use super::state::SharedDownloadState;

/// 重试结果
pub enum RetryResult {
    /// 重试成功，返回新的 endpoint 列表
    Success(EndpointList),
    /// 所有重试都失败
    Failed(String),
}

/// 重试管理器
pub struct RetryManager {
    /// 下载状态
    state: SharedDownloadState,
    /// 原始 URL
    original_url: String,
    /// 当前实际 URL
    current_url: String,
    /// RemoteFile
    remote_file: Arc<Mutex<RemoteFile>>,
}

impl RetryManager {
    /// 创建新的重试管理器
    pub fn new(
        state: SharedDownloadState,
        original_url: String,
        current_url: String,
        remote_file: Arc<Mutex<RemoteFile>>,
    ) -> Self {
        Self {
            state,
            original_url,
            current_url,
            remote_file,
        }
    }

    /// 检查是否所有 endpoint 都失败
    pub async fn all_endpoints_failed(&self) -> bool {
        let state = self.state.lock().await;

        // 如果全部完成，不需要重试
        let all_completed = state.segments.iter().all(|s| {
            s.status == super::state::SegmentStatus::Completed
        });
        if all_completed {
            return false;
        }

        // 如果有正在下载的 segment，不触发重试
        let has_active = state.segments.iter().any(|s| {
            s.status == super::state::SegmentStatus::Downloading
        });
        if has_active {
            return false;
        }

        // 检查是否所有 endpoint 都已被禁用
        let all_ips: std::collections::HashSet<std::net::IpAddr> = state.connections.iter()
            .map(|c| c.endpoint)
            .collect();

        if all_ips.is_empty() {
            return false;
        }

        all_ips.iter().all(|ip| state.disabled_endpoints.contains(ip))
    }

    /// 执行重试
    pub async fn retry(&self) -> RetryResult {
        let config = GLOBAL_CONFIG.read().await;

        // 步骤 1：重新连接
        eprintln!("Retry step 1: Reconnecting...");
        for i in 0..config.retry_reconnect_count {
            eprintln!("  Reconnect attempt {}/{}", i + 1, config.retry_reconnect_count);
            match self.try_reconnect().await {
                Ok(endpoint_list) => return RetryResult::Success(endpoint_list),
                Err(e) => {
                    eprintln!("  Reconnect failed: {}", e);
                }
            }
        }

        // 步骤 2：重新获取 endpoint
        eprintln!("Retry step 2: Re-fetching endpoints...");
        for i in 0..config.retry_refetch_endpoint_count {
            eprintln!("  Refetch endpoint attempt {}/{}", i + 1, config.retry_refetch_endpoint_count);
            match self.try_refetch_endpoints().await {
                Ok(endpoint_list) => return RetryResult::Success(endpoint_list),
                Err(e) => {
                    eprintln!("  Refetch endpoint failed: {}", e);
                }
            }
        }

        // 步骤 3：重新获取 actual URL
        eprintln!("Retry step 3: Re-fetching actual URL...");
        for i in 0..config.retry_refetch_url_count {
            eprintln!("  Refetch URL attempt {}/{}", i + 1, config.retry_refetch_url_count);
            match self.try_refetch_url().await {
                Ok(endpoint_list) => return RetryResult::Success(endpoint_list),
                Err(e) => {
                    eprintln!("  Refetch URL failed: {}", e);
                }
            }
        }

        RetryResult::Failed("All retry steps failed".to_string())
    }

    /// 保存原始 RemoteFile 中的代理和请求头，用于恢复
    async fn save_proxy_and_headers(&self) -> (HashMap<String, String>, HashMap<String, String>) {
        let rf = self.remote_file.lock().await;
        (rf.proxy_map.clone(), rf.request_headers.clone())
    }

    /// 将保存的代理和请求头恢复到新的 RemoteFile
    fn restore_proxy_and_headers(remote_file: &mut RemoteFile, proxy_map: HashMap<String, String>, headers: HashMap<String, String>) {
        remote_file.proxy_map = proxy_map;
        for (k, v) in headers {
            remote_file.request_headers.entry(k).or_insert(v);
        }
    }

    /// 尝试重新连接
    async fn try_reconnect(&self) -> Result<EndpointList, String> {
        // 清空禁用状态，重新启用所有 endpoint
        {
            let mut state = self.state.lock().await;
            state.disabled_endpoints.clear();
            for conn in state.connections.iter_mut() {
                conn.failure_count = 0;
            }
        }

        // 保存原始代理和请求头
        let (proxy_map, headers) = self.save_proxy_and_headers().await;

        // 使用当前 URL 获取 endpoint
        let mut remote_file = RemoteFile::new(&self.current_url);
        Self::restore_proxy_and_headers(&mut remote_file, proxy_map, headers);
        remote_file.get_information().await
            .map_err(|e| format!("Get information error: {}", e))?;

        let endpoint_list = remote_file.endpoint_list.clone()
            .ok_or("No endpoint list available")?;

        // 更新 remote_file
        {
            let mut rf = self.remote_file.lock().await;
            *rf = remote_file;
        }

        Ok(endpoint_list)
    }

    /// 尝试重新获取 endpoint
    async fn try_refetch_endpoints(&self) -> Result<EndpointList, String> {
        // 保存原始代理和请求头
        let (proxy_map, headers) = self.save_proxy_and_headers().await;

        // 使用 RemoteFile 重新获取 endpoint
        let mut remote_file = RemoteFile::new(&self.current_url);
        Self::restore_proxy_and_headers(&mut remote_file, proxy_map, headers);
        remote_file.get_information().await
            .map_err(|e| format!("Get information error: {}", e))?;

        let endpoint_list = remote_file.endpoint_list.clone()
            .ok_or("No endpoint list available")?;

        // 更新 remote_file
        {
            let mut rf = self.remote_file.lock().await;
            *rf = remote_file;
        }

        // 清空禁用状态
        {
            let mut state = self.state.lock().await;
            state.disabled_endpoints.clear();
            for conn in state.connections.iter_mut() {
                conn.failure_count = 0;
            }
        }

        Ok(endpoint_list)
    }

    /// 尝试重新获取 actual URL
    async fn try_refetch_url(&self) -> Result<EndpointList, String> {
        // 保存原始代理和请求头
        let (proxy_map, headers) = self.save_proxy_and_headers().await;

        // 从原始 URL 重新获取
        let mut remote_file = RemoteFile::new(&self.original_url);
        Self::restore_proxy_and_headers(&mut remote_file, proxy_map, headers);
        remote_file.get_information().await
            .map_err(|e| format!("Get information error: {}", e))?;

        let endpoint_list = remote_file.endpoint_list.clone()
            .ok_or("No endpoint list available")?;

        // 检查是否有重定向
        if remote_file.url != self.original_url {
            eprintln!("  New actual URL: {}", remote_file.url);
            // 更新 current_url
            // 注意：这里需要修改 self.current_url，但 self 是不可变的
            // 所以我们需要返回新的 URL 信息
        }

        // 更新 remote_file
        {
            let mut rf = self.remote_file.lock().await;
            *rf = remote_file;
        }

        // 清空禁用状态
        {
            let mut state = self.state.lock().await;
            state.disabled_endpoints.clear();
            for conn in state.connections.iter_mut() {
                conn.failure_count = 0;
            }
        }

        Ok(endpoint_list)
    }
}
