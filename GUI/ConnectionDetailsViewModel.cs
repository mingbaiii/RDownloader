using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;

namespace RDownloaderGUI
{
    /// <summary>
    /// Flyout ViewModel for the connection details panel.
    /// Populated from DownloadFullInfo (info all) data.
    /// </summary>
    public class ConnectionDetailsViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private int _connections;
        public int Connections
        {
            get => _connections;
            set { _connections = value; OnPropertyChanged(); }
        }

        private string _taskId;
        public string TaskId
        {
            get => _taskId;
            set { _taskId = value; OnPropertyChanged(); }
        }

        private string _fileName;
        public string FileName
        {
            get => _fileName;
            set { _fileName = value; OnPropertyChanged(); }
        }

        private double _totalSpeed;
        public double TotalSpeed
        {
            get => _totalSpeed;
            set { _totalSpeed = value; OnPropertyChanged(); OnPropertyChanged(nameof(TotalSpeedText)); }
        }

        public string TotalSpeedText => TotalSpeed > 0
            ? $"↓ {FormatBytes((long)TotalSpeed)}/s"
            : "";

        private string _totalProgressText;
        public string TotalProgressText
        {
            get => _totalProgressText;
            set { _totalProgressText = value; OnPropertyChanged(); }
        }

        /// <summary>Shown when no data is available (e.g. paused task).</summary>
        private string _noDataMessage;
        public string NoDataMessage
        {
            get => _noDataMessage;
            set { _noDataMessage = value; OnPropertyChanged(); }
        }

        /// <summary>All segments for the block grid.</summary>
        public ObservableCollection<SegmentViewModel> Segments { get; }
            = new ObservableCollection<SegmentViewModel>();

        /// <summary>All endpoints for the node list.</summary>
        public ObservableCollection<EndpointViewModel> Endpoints { get; }
            = new ObservableCollection<EndpointViewModel>();

        // ── Populating ────────────────────────────────

        /// <summary>
        /// Update the ViewModel in-place from a DownloadFullInfo snapshot.
        /// Uses incremental updates to avoid full UI rebuild on every refresh.
        /// </summary>
        public void UpdateFrom(DownloadFullInfo info)
        {
            if (info == null) return;

            Connections = info.Connections;
            TotalSpeed = info.Speed;
            NoDataMessage = ""; // data is available → clear placeholder

            var fileSize = info.FileSize;
            var progress = fileSize > 0 ? info.Downloaded / (double)fileSize : 0;
            TotalProgressText = fileSize > 0 ? $"{progress:P1}" : "";

            // ── Endpoints: update in-place ────────────
            var incomingEps = info.Endpoints ?? new List<EndpointInfo>();
            for (int i = 0; i < incomingEps.Count; i++)
            {
                var ep = incomingEps[i];
                var nodeColor = GetEndpointColor(ep.Ip);

                if (i < Endpoints.Count)
                {
                    // Update existing endpoint in-place
                    var existing = Endpoints[i];
                    existing.Id = ep.Id;
                    existing.Ip = ep.Ip;
                    // Don't overwrite if a user toggle API call is still in flight
                    if (!existing.PendingToggle)
                        existing.Enabled = ep.Enabled;
                    existing.Color = nodeColor;
                    existing.IcmpLatencyMs = ep.IcmpPing?.RttMs;
                    existing.TcpLatencyMs = ep.TcpPing?.LatencyMs;
                    existing.BestLatencyMs = ep.BestLatencyMs;
                    existing.SpeedTestBps = ep.SpeedTest?.SpeedBps;
                    existing.IsAvailable = ep.IsAvailable;
                    existing.Proxy = ep.Proxy;
                    existing.TotalSegmentSpeed = 0; // reset, sum below
                    existing.BuildTooltipText();
                }
                else
                {
                    // New endpoint
                    var vm = new EndpointViewModel
                    {
                        Id = ep.Id,
                        Ip = ep.Ip,
                        Enabled = ep.Enabled,
                        Color = nodeColor,
                        IcmpLatencyMs = ep.IcmpPing?.RttMs,
                        TcpLatencyMs = ep.TcpPing?.LatencyMs,
                        BestLatencyMs = ep.BestLatencyMs,
                        SpeedTestBps = ep.SpeedTest?.SpeedBps,
                        IsAvailable = ep.IsAvailable,
                        Proxy = ep.Proxy,
                        TotalSegmentSpeed = 0,
                    };
                    vm.BuildTooltipText();
                    Endpoints.Add(vm);
                }
            }
            // Remove stale endpoints
            while (Endpoints.Count > incomingEps.Count)
                Endpoints.RemoveAt(Endpoints.Count - 1);

            // Build endpoint lookups for segment coloring
            var endpointMap = Endpoints.ToDictionary(e => e.Id, e => e);
            var endpointColorMap = Endpoints.ToDictionary(e => e.Id, e => e.Color);

            // ── Segments: update in-place ─────────────
            var incomingSegs = info.Segments ?? new List<SegmentInfo>();
            for (int i = 0; i < incomingSegs.Count; i++)
            {
                var seg = incomingSegs[i];
                var segSize = seg.End - seg.Start;
                var segProgress = segSize > 0
                    ? (double)seg.Downloaded / segSize
                    : (seg.Completed ? 1.0 : 0.0);

                Color blockColor;
                string epIp = null;
                if (seg.EndpointId.HasValue && endpointColorMap.TryGetValue(seg.EndpointId.Value, out var epColor))
                {
                    blockColor = epColor;
                    if (endpointMap.TryGetValue(seg.EndpointId.Value, out var epVm))
                    {
                        epVm.TotalSegmentSpeed += seg.Speed;
                        epIp = epVm.Ip;
                    }
                }
                else
                {
                    blockColor = Color.FromRgb(0x66, 0x66, 0x66);
                }

                if (i < Segments.Count)
                {
                    // Update existing segment in-place
                    var existing = Segments[i];
                    existing.Progress = seg.Completed ? 1.0 : segProgress;
                    existing.BlockColor = blockColor;
                    existing.StartByte = seg.Start;
                    existing.EndByte = seg.End;
                    existing.Downloaded = seg.Downloaded;
                    existing.Speed = seg.Speed;
                    existing.EndpointIp = epIp;
                    existing.IsCompleted = seg.Completed;
                    existing.BuildTooltipText();
                }
                else
                {
                    // New segment
                    var svm = new SegmentViewModel
                    {
                        Progress = seg.Completed ? 1.0 : segProgress,
                        BlockColor = blockColor,
                        StartByte = seg.Start,
                        EndByte = seg.End,
                        Downloaded = seg.Downloaded,
                        Speed = seg.Speed,
                        EndpointIp = epIp,
                        IsCompleted = seg.Completed,
                    };
                    svm.BuildTooltipText();
                    Segments.Add(svm);
                }
            }
            // Remove stale segments
            while (Segments.Count > incomingSegs.Count)
                Segments.RemoveAt(Segments.Count - 1);
        }

        // ── Color generation ──────────────────────────

        /// <summary>
        /// Generate a deterministic, high-contrast color for an IP address.
        /// Uses SHA256 hash → hue in HSL space (S=85%, L=62%).
        /// </summary>
        public static Color GetEndpointColor(string ip)
        {
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(ip ?? "0.0.0.0"));
                // Use first 2 bytes as hue (0-359)
                var hue = (double)((hash[0] << 8) | hash[1]) % 360;
                return HslToColor(hue, 0.85, 0.62);
            }
        }

        private static byte ClampToByte(int value)
        {
            return (byte)(value < 0 ? 0 : (value > 255 ? 255 : value));
        }

        private static Color HslToColor(double h, double s, double l)
        {
            // h in [0,360), s/l in [0,1]
            var c = (1 - Math.Abs(2 * l - 1)) * s;
            var x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            var m = l - c / 2;

            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }

            return Color.FromRgb(
                ClampToByte((int)((r + m) * 255)),
                ClampToByte((int)((g + m) * 255)),
                ClampToByte((int)((b + m) * 255)));
        }

        // ── Helpers ────────────────────────────────────

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
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

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    // ── Sub ViewModels ──────────────────────────────────────

    /// <summary>
    /// Represents a single segment (connection block) in the grid.
    /// </summary>
    public class SegmentViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private double _progress;
        public double Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(); OnPropertyChanged(nameof(Opacity)); }
        }

        /// <summary>Opacity = progress, clamped to [0.05, 1.0] so blocks are always visible.</summary>
        public double Opacity => Math.Max(0.05, Progress);

        private Color _blockColor = Color.FromRgb(0x66, 0x66, 0x66);
        public Color BlockColor
        {
            get => _blockColor;
            set
            {
                if (_blockColor != value)
                {
                    _blockColor = value;
                    _blockBrush = new SolidColorBrush(value);
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(BlockBrush));
                }
            }
        }

        private SolidColorBrush _blockBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        public SolidColorBrush BlockBrush => _blockBrush;

        public long StartByte { get; set; }
        public long EndByte { get; set; }
        public long Downloaded { get; set; }
        public double Speed { get; set; }
        public string EndpointIp { get; set; }
        public bool IsCompleted { get; set; }

        private string _tooltipText;
        public string TooltipText
        {
            get => _tooltipText;
            set { _tooltipText = value; OnPropertyChanged(); }
        }

        public void BuildTooltipText()
        {
            var sb = new StringBuilder();
            sb.Append("范围: ").Append(FormatBytes(StartByte)).Append(" – ").Append(FormatBytes(EndByte));
            sb.Append('\n');
            sb.Append("已下载: ").Append(FormatBytes(Downloaded));
            if (IsCompleted) sb.Append(" (完成)");
            else sb.Append(" (").Append((Progress * 100).ToString("F0")).Append("%)");
            sb.Append('\n');
            if (!string.IsNullOrEmpty(EndpointIp))
                sb.Append("节点: ").Append(EndpointIp);
            else
                sb.Append("未分配节点");
            if (Speed > 0)
                sb.Append('\n').Append("速度: ↓ ").Append(FormatBytes((long)Speed)).Append("/s");
            TooltipText = sb.ToString();
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
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

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Represents an endpoint/node in the right panel.
    /// </summary>
    public class EndpointViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public int Id { get; set; }

        private string _ip;
        public string Ip
        {
            get => _ip;
            set { _ip = value; OnPropertyChanged(); }
        }

        private bool _enabled;
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled != value)
                {
                    _enabled = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// True while an enable/disable API call is in flight for this endpoint.
        /// When set, polling updates skip the Enabled field to avoid overwriting
        /// the user's toggle intent before the command takes effect on the server.
        /// </summary>
        public bool PendingToggle { get; set; }

        private Color _color = Color.FromRgb(0x66, 0x66, 0x66);
        public Color Color
        {
            get => _color;
            set
            {
                if (_color != value)
                {
                    _color = value;
                    _colorBrush = new SolidColorBrush(value);
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ColorBrush));
                }
            }
        }

        private SolidColorBrush _colorBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        public SolidColorBrush ColorBrush => _colorBrush;

        public double? IcmpLatencyMs { get; set; }
        public double? TcpLatencyMs { get; set; }
        public double? BestLatencyMs { get; set; }
        public double? SpeedTestBps { get; set; }
        public bool IsAvailable { get; set; }
        public string Proxy { get; set; }

        private double _totalSegmentSpeed;
        public double TotalSegmentSpeed
        {
            get => _totalSegmentSpeed;
            set { _totalSegmentSpeed = value; OnPropertyChanged(); OnPropertyChanged(nameof(SpeedText)); }
        }

        public string SpeedText => TotalSegmentSpeed > 0
            ? $"↓ {FormatBytes((long)TotalSegmentSpeed)}/s"
            : "";

        private string _tooltipText;
        public string TooltipText
        {
            get => _tooltipText;
            set { _tooltipText = value; OnPropertyChanged(); }
        }

        public void BuildTooltipText()
        {
            var sb = new StringBuilder();
            sb.Append("IP: ").Append(Ip);
            if (BestLatencyMs.HasValue)
                sb.Append('\n').Append("延迟: ").Append(BestLatencyMs.Value.ToString("F1")).Append(" ms");
            if (IcmpLatencyMs.HasValue)
                sb.Append('\n').Append("ICMP: ").Append(IcmpLatencyMs.Value.ToString("F1")).Append(" ms");
            if (TcpLatencyMs.HasValue)
                sb.Append('\n').Append("TCP: ").Append(TcpLatencyMs.Value.ToString("F1")).Append(" ms");
            if (SpeedTestBps.HasValue && SpeedTestBps.Value > 0)
                sb.Append('\n').Append("测速: ↓ ").Append(FormatBytes((long)SpeedTestBps.Value)).Append("/s");
            if (TotalSegmentSpeed > 0)
                sb.Append('\n').Append("当前速度: ↓ ").Append(FormatBytes((long)TotalSegmentSpeed)).Append("/s");
            if (!string.IsNullOrEmpty(Proxy))
                sb.Append('\n').Append("代理: ").Append(Proxy);
            if (!IsAvailable)
                sb.Append('\n').Append("⚠ 不可用");
            TooltipText = sb.ToString();
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
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

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
