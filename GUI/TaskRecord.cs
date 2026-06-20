using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RDownloaderGUI
{
    /// <summary>
    /// 单个下载任务的持久化记录，存入 %AppData%/RDownloader/tasks/{TaskId}.json。
    /// 此记录在 rdown.exe 删除 .rdown.json 后依然存在。
    /// </summary>
    public class TaskRecord
    {
        [JsonPropertyName("task_id")]
        public string TaskId { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("file_name")]
        public string FileName { get; set; }

        [JsonPropertyName("download_dir")]
        public string DownloadDir { get; set; }

        /// <summary>.rdown.json 的路径（rdown 完成后可能已被删除）</summary>
        [JsonPropertyName("task_json_path")]
        public string TaskJsonPath { get; set; }

        /// <summary>临时 rdown_config.json 的路径</summary>
        [JsonPropertyName("config_path")]
        public string ConfigPath { get; set; }

        /// <summary>任务的自定义 Headers（Config 被删除后用于重建）</summary>
        [JsonPropertyName("headers")]
        public Dictionary<string, string> Headers { get; set; }

        /// <summary>任务的代理设置（Config 被删除后用于重建）</summary>
        [JsonPropertyName("proxy")]
        public Dictionary<string, string> Proxy { get; set; }

        /// <summary>任务的 DNS 服务器列表（Config 被删除后用于重建）</summary>
        [JsonPropertyName("dns_servers")]
        public List<string> DnsServers { get; set; }

        /// <summary>服务器响应头（从 probe 阶段获取，用于后续参考如 Content-Disposition）</summary>
        [JsonPropertyName("response_headers")]
        public Dictionary<string, string> ResponseHeaders { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("total_size")]
        public long TotalSize { get; set; }

        [JsonPropertyName("downloaded")]
        public long Downloaded { get; set; }

        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>下载速度（B/s）</summary>
        [JsonPropertyName("speed")]
        public double Speed { get; set; }

        /// <summary>进度百分比（0.0 ~ 1.0）</summary>
        [JsonIgnore]
        public double Progress => TotalSize > 0 ? (double)Downloaded / TotalSize : 0;

        /// <summary>进度百分比文本</summary>
        [JsonIgnore]
        public string ProgressText => TotalSize > 0 ? $"{Progress:P1}" : "";

        /// <summary>文件大小可读文本（ContentLength 未知时显示 ?）</summary>
        [JsonIgnore]
        public string TotalSizeText => TotalSize > 0 ? FormatBytes(TotalSize) : (Downloaded > 0 ? "?" : "");

        /// <summary>已下载可读文本</summary>
        [JsonIgnore]
        public string DownloadedText => FormatBytes(Downloaded);

        /// <summary>下载速度可读文本（Running 时显示）</summary>
        [JsonIgnore]
        public string SpeedText => Speed > 0 ? $"↓ {FormatBytes((long)Speed)}/s" : "";
        [JsonIgnore]
        public bool ShowSpeed => Status == "Running" && Speed > 0;

        /// <summary>进度条状态：Parsing→Indeterminate, Paused→Paused, Failed→Error, Running→Normal</summary>
        [JsonIgnore]
        public bool IsIndeterminate => Status == "Parsing";
        [JsonIgnore]
        public bool ShowPaused => Status == "Paused";
        [JsonIgnore]
        public bool ShowError => Status == "Failed" || Status == "Cancelled";

        /// <summary>按钮可见性控制</summary>
        [JsonIgnore]
        public bool ShowPauseResume => Status == "Running" || Status == "Paused";
        [JsonIgnore]
        public bool ShowCancel => Status == "Running" || Status == "Paused" || Status == "Parsing";
        [JsonIgnore]
        public bool IsPauseMode => Status == "Running";        // Running→显示暂停图标
        [JsonIgnore]
        public bool IsResumeMode => Status == "Paused";        // Paused→显示恢复图标
        [JsonIgnore]
        public bool CanOpenFile => Status == "Completed";

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "";
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:0.#} {sizes[order]}";
        }
    }
}
