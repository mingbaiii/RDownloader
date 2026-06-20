using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RDownloaderGUI
{
    /// <summary>
    /// GUI 专用配置模型，存储所有设置到 gui_config.json。
    /// 包含启用/禁用状态，rdown.exe 通过 ToRdownConfig() 获取临时配置。
    /// </summary>
    public class GuiConfig
    {
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // ── 网络 ──

        [JsonPropertyName("ip_protocol")]
        public string IpProtocol { get; set; } = "OnlyIPv4";

        [JsonPropertyName("total_connections")]
        public int TotalConnections { get; set; } = 64;

        [JsonPropertyName("dns_servers")]
        public List<DnsItem> DnsServers { get; set; } = new List<DnsItem>
        {
            new DnsItem { Address = "system", IsEnabled = true },
            new DnsItem { Address = "114.114.114.114", IsEnabled = true },
            new DnsItem { Address = "https://223.5.5.5/dns-query", IsEnabled = true },
        };

        // ── 下载调优 ──

        [JsonPropertyName("chunk_size")]
        public long ChunkSize { get; set; } = 65536;

        [JsonPropertyName("speed_threshold")]
        public double SpeedThreshold { get; set; } = 2.0;

        [JsonPropertyName("download_timeout_secs")]
        public int DownloadTimeoutSecs { get; set; } = 30;

        // ── 重试设置 ──

        [JsonPropertyName("retry_count")]
        public int RetryCount { get; set; } = 3;

        [JsonPropertyName("retry_reconnect_count")]
        public int RetryReconnectCount { get; set; } = 3;

        [JsonPropertyName("retry_refetch_endpoint_count")]
        public int RetryRefetchEndpointCount { get; set; } = 2;

        [JsonPropertyName("retry_refetch_url_count")]
        public int RetryRefetchUrlCount { get; set; } = 1;

        [JsonPropertyName("content_length_zero_retry")]
        public int ContentLengthZeroRetry { get; set; } = 3;

        [JsonPropertyName("endpoint_failure_threshold")]
        public int EndpointFailureThreshold { get; set; } = 3;

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

        // ── 请求头（无启用/禁用）──

        [JsonPropertyName("headers")]
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();

        // ── 代理规则（KeyValueItem.IsSelected = 启用/禁用）──

        [JsonPropertyName("proxy")]
        public List<KeyValueItem> Proxy { get; set; } = new List<KeyValueItem>();

        // ── RPC ──

        [JsonPropertyName("rpc_port")]
        public int RpcPort { get; set; } = 6800;

        [JsonPropertyName("rpc_secret")]
        public string RpcSecret { get; set; } = "";

        // ── 调试 ──

        [JsonPropertyName("debug_mode")]
        public bool DebugMode { get; set; } = false;

        // ── 下载路径 ──

        [JsonPropertyName("download_path")]
        public string DownloadPath { get; set; } = "";

        // ── 持久化 ────────────────────────────────────

        /// <summary>
        /// 获取有效的下载目录路径。
        /// 优先级：config.DownloadPath → 默认 %USERPROFILE%\Downloads\RDownloader
        /// </summary>
        public static string GetEffectiveDownloadDir()
        {
            var config = Load();
            if (!string.IsNullOrWhiteSpace(config.DownloadPath))
                return config.DownloadPath;

            return Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                "Downloads",
                "RDownloader");
        }

        public static string GetConfigDir()
        {
            return Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "RDownloader");
        }

        public static string GetConfigPath()
        {
            return Path.Combine(GetConfigDir(), "gui_config.json");
        }

        public static GuiConfig Load()
        {
            var path = GetConfigPath();
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    return JsonSerializer.Deserialize<GuiConfig>(json, SerializerOptions) ?? new GuiConfig();
                }
                catch
                {
                    return new GuiConfig();
                }
            }
            return new GuiConfig();
        }

        public void Save()
        {
            var path = GetConfigPath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(this, SerializerOptions);
            File.WriteAllText(path, json, Encoding.UTF8);
        }

        // ── 生成临时 RdownConfig（只含启用的条目）──

        /// <summary>
        /// 从当前 GuiConfig 生成一个 RdownConfig，只包含启用的 DNS/代理。
        /// 用于写入临时 json 供 rdown.exe --config 使用。
        /// </summary>
        public RdownConfig ToRdownConfig()
        {
            // 迁移旧值 → rdown 期望的格式
            var ipProtocol = this.IpProtocol;
            if (ipProtocol == "Ipv4") ipProtocol = "OnlyIPv4";
            else if (ipProtocol == "Ipv6") ipProtocol = "OnlyIPv6";

            return new RdownConfig
            {
                IpProtocol = ipProtocol,
                TotalConnections = this.TotalConnections,
                ChunkSize = this.ChunkSize,
                SpeedThreshold = this.SpeedThreshold,
                DownloadTimeoutSecs = this.DownloadTimeoutSecs,
                RetryCount = this.RetryCount,
                RetryReconnectCount = this.RetryReconnectCount,
                RetryRefetchEndpointCount = this.RetryRefetchEndpointCount,
                RetryRefetchUrlCount = this.RetryRefetchUrlCount,
                ContentLengthZeroRetry = this.ContentLengthZeroRetry,
                EndpointFailureThreshold = this.EndpointFailureThreshold,
                SpeedTestRetryCount = this.SpeedTestRetryCount,
                SpeedTestTimeoutSecs = this.SpeedTestTimeoutSecs,
                ConnectivityTimeoutSecs = this.ConnectivityTimeoutSecs,
                DnsTimeoutSecs = this.DnsTimeoutSecs,
                DnsRetryCount = this.DnsRetryCount,
                SchedulerCheckIntervalSecs = this.SchedulerCheckIntervalSecs,
                SchedulerSlowThreshold = this.SchedulerSlowThreshold,
                SchedulerReallocateThreshold = this.SchedulerReallocateThreshold,
                Headers = this.Headers ?? new Dictionary<string, string>(),
                Proxy = (this.Proxy ?? Enumerable.Empty<KeyValueItem>())
                    .Where(x => x.IsSelected)
                    .ToDictionary(x => x.Key ?? "", x => x.Value ?? ""),
                DnsServers = (this.DnsServers ?? Enumerable.Empty<DnsItem>())
                    .Where(x => x.IsEnabled)
                    .Select(x => x.Address)
                    .ToList()
            };
        }

        // ── 确定性指纹 ────────────────────────────────

        /// <summary>
        /// 计算 Headers / Proxy / DNS 的确定性指纹（SHA256），用于跳过无变化项的重新解析。
        /// </summary>
        public static string ComputeFingerprint(
            IEnumerable<KeyValuePair<string, string>> headers,
            IEnumerable<KeyValuePair<string, string>> proxy,
            IEnumerable<string> dnsServers)
        {
            var sb = new StringBuilder();

            // Headers: 按 Key 排序
            if (headers != null)
            {
                foreach (var kv in headers.OrderBy(x => x.Key))
                {
                    if (!string.IsNullOrEmpty(kv.Key) || !string.IsNullOrEmpty(kv.Value))
                        sb.Append(kv.Key).Append(':').Append(kv.Value).Append('\n');
                }
            }
            sb.Append('|');

            // Proxy: 按 Key 排序
            if (proxy != null)
            {
                foreach (var kv in proxy.OrderBy(x => x.Key))
                {
                    if (!string.IsNullOrEmpty(kv.Key) || !string.IsNullOrEmpty(kv.Value))
                        sb.Append(kv.Key).Append(':').Append(kv.Value).Append('\n');
                }
            }
            sb.Append('|');

            // DNS: 排序
            if (dnsServers != null)
            {
                foreach (var addr in dnsServers.OrderBy(x => x))
                {
                    if (!string.IsNullOrWhiteSpace(addr))
                        sb.Append(addr).Append('\n');
                }
            }

            var input = sb.ToString();
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
                return System.BitConverter.ToString(hash).Replace("-", "");
            }
        }
    }
}
