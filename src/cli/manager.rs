use std::collections::HashMap;
use std::sync::{Arc, LazyLock};
use tokio::sync::Mutex;

use crate::downloader::DownloadHandle;

/// 全局下载管理器
pub struct DownloadManager {
    /// 正在运行的下载句柄
    handles: HashMap<String, Arc<Mutex<DownloadHandle>>>,
}

impl DownloadManager {
    pub fn new() -> Self {
        Self {
            handles: HashMap::new(),
        }
    }

    /// 注册一个下载
    pub fn register(&mut self, id: String, handle: DownloadHandle) {
        self.handles.insert(id, Arc::new(Mutex::new(handle)));
    }

    /// 获取下载句柄
    pub fn get(&self, id: &str) -> Option<Arc<Mutex<DownloadHandle>>> {
        self.handles.get(id).cloned()
    }

    /// 移除下载
    pub fn remove(&mut self, id: &str) {
        self.handles.remove(id);
    }

    /// 列出所有正在运行的下载 ID
    pub fn list_running(&self) -> Vec<String> {
        self.handles.keys().cloned().collect()
    }
}

/// 全局下载管理器实例
pub static DOWNLOAD_MANAGER: LazyLock<Arc<Mutex<DownloadManager>>> =
    LazyLock::new(|| Arc::new(Mutex::new(DownloadManager::new())));
