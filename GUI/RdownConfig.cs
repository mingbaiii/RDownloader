using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RDownloaderGUI
{
    /// <summary>
    /// 对应 rdown_config.json 的 JSON 配置模型，字段名与 rdownloader 保持一致。
    /// </summary>
    public class RdownConfig
    {
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        [JsonPropertyName("ip_protocol")]
        public string IpProtocol { get; set; } = "OnlyIPv4";

        [JsonPropertyName("total_connections")]
        public int TotalConnections { get; set; } = 64;

        [JsonPropertyName("retry_count")]
        public int RetryCount { get; set; } = 3;

        [JsonPropertyName("chunk_size")]
        public long ChunkSize { get; set; } = 65536; // 64 KB

        [JsonPropertyName("speed_threshold")]
        public double SpeedThreshold { get; set; } = 2.0;

        [JsonPropertyName("download_timeout_secs")]
        public int DownloadTimeoutSecs { get; set; } = 30;

        [JsonPropertyName("dns_servers")]
        public List<string> DnsServers { get; set; } = new List<string> { "system", "114.114.114.114", "https://223.5.5.5/dns-query" };

        [JsonPropertyName("headers")]
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();

        [JsonPropertyName("proxy")]
        public Dictionary<string, string> Proxy { get; set; } = new Dictionary<string, string>();

        [JsonPropertyName("endpoint_failure_threshold")]
        public int EndpointFailureThreshold { get; set; } = 3;

        [JsonPropertyName("retry_reconnect_count")]
        public int RetryReconnectCount { get; set; } = 3;

        [JsonPropertyName("retry_refetch_endpoint_count")]
        public int RetryRefetchEndpointCount { get; set; } = 2;

        [JsonPropertyName("retry_refetch_url_count")]
        public int RetryRefetchUrlCount { get; set; } = 1;

        [JsonPropertyName("content_length_zero_retry_count")]
        public int ContentLengthZeroRetry { get; set; } = 3;

        [JsonPropertyName("speed_test_retry_count")]
        public int SpeedTestRetryCount { get; set; } = 3;

        [JsonPropertyName("speed_test_timeout_secs")]
        public int SpeedTestTimeoutSecs { get; set; } = 15;

        [JsonPropertyName("connectivity_timeout_secs")]
        public int ConnectivityTimeoutSecs { get; set; } = 3;

        [JsonPropertyName("dns_timeout_secs")]
        public int DnsTimeoutSecs { get; set; } = 5;

        [JsonPropertyName("dns_retry_count")]
        public int DnsRetryCount { get; set; } = 3;

        [JsonPropertyName("scheduler_check_interval_secs")]
        public int SchedulerCheckIntervalSecs { get; set; } = 3;

        [JsonPropertyName("scheduler_slow_threshold")]
        public double SchedulerSlowThreshold { get; set; } = 0.3;

        [JsonPropertyName("scheduler_reallocate_threshold")]
        public double SchedulerReallocateThreshold { get; set; } = 0.2;

        /// <summary>
        /// IP 协议选项列表（用于 ComboBox）
        /// </summary>
        public static readonly string[] IpProtocolOptions = { "Both", "OnlyIPv4", "OnlyIPv6" };

        /// <summary>
        /// 分块大小预设选项
        /// </summary>
        public static readonly long[] ChunkSizeOptions =
        {
            16384,   // 16 KB
            32768,   // 32 KB
            65536,   // 64 KB
            131072,  // 128 KB
            262144,  // 256 KB
            524288,  // 512 KB
            1048576, // 1 MB
            2097152, // 2 MB
            4194304  // 4 MB
        };

        /// <summary>
        /// 格式化字节为可读字符串
        /// </summary>
        public static string FormatChunkSize(long bytes)
        {
            if (bytes >= 1048576) return $"{bytes / 1048576} MB ({bytes:N0} bytes)";
            if (bytes >= 1024) return $"{bytes / 1024} KB ({bytes:N0} bytes)";
            return $"{bytes} bytes";
        }

        // ==================== 持久化 ====================

        /// <summary>
        /// 从 JSON 文件加载配置，文件不存在时返回默认值。
        /// </summary>
        public static RdownConfig Load(string path)
        {
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    return JsonSerializer.Deserialize<RdownConfig>(json, SerializerOptions) ?? new RdownConfig();
                }
                catch
                {
                    // 文件损坏时返回默认配置
                    return new RdownConfig();
                }
            }

            return new RdownConfig();
        }

        /// <summary>
        /// 保存配置到 JSON 文件，自动创建目录。
        /// </summary>
        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(this, SerializerOptions);
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

    }
}
