using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace RDownloaderGUI
{
    /// <summary>
    /// 端点信息 UI 模型，封装 EndpointInfo 并提供 INotifyPropertyChanged。
    /// </summary>
    public class EndpointItem : INotifyPropertyChanged
    {
        private bool _isEnabled;

        /// <summary>
        /// 端点 ID（对应 rdown.exe 的 endpoint id）。
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// 端点 IP 地址。
        /// </summary>
        public string Ip { get; set; } = string.Empty;

        /// <summary>
        /// 代理地址（可为空，表示直连）。
        /// </summary>
        public string Proxy { get; set; }

        /// <summary>
        /// 所属任务的 TaskId，用于启用/禁用端点时定位任务。
        /// </summary>
        public string ParentTaskId { get; set; }

        /// <summary>
        /// 延迟（毫秒），null 表示未测量。
        /// </summary>
        public double? LatencyMs { get; set; }

        /// <summary>
        /// 速度（字节/秒），null 表示未测量。
        /// </summary>
        public double? Speed { get; set; }

        /// <summary>
        /// 是否启用此端点。
        /// </summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 探测测试结果：该端点是否可达。
        /// </summary>
        public bool IsAvailable { get; set; }

        /// <summary>
        /// 端点性能与信息描述文本。
        /// </summary>
        public string Description
        {
            get
            {
                var sb = new StringBuilder();

                // 可用性
                sb.Append(IsAvailable ? "✓ 可用" : "✗ 不可用");

                // 代理
                if (!string.IsNullOrEmpty(Proxy))
                    sb.Append("  |  代理: ").Append(Proxy);
                else
                    sb.Append("  |  直连");

                // 延迟
                sb.Append("  |  延迟: ");
                if (LatencyMs.HasValue)
                    sb.Append(LatencyMs.Value.ToString("F0")).Append(" ms");
                else
                    sb.Append("—");

                // 速度
                sb.Append("  |  速度: ");
                if (Speed.HasValue)
                    sb.Append(FormatSpeed(Speed.Value));
                else
                    sb.Append("—");

                return sb.ToString();
            }
        }

        private static string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec >= 1_000_000)
                return (bytesPerSec / 1_000_000).ToString("F1") + " MB/s";
            if (bytesPerSec >= 1_000)
                return (bytesPerSec / 1_000).ToString("F1") + " KB/s";
            return bytesPerSec.ToString("F0") + " B/s";
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
