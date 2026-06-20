using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace RDownloaderGUI
{
    /// <summary>
    /// SettingsPage.xaml 的交互逻辑
    /// </summary>
    public partial class SettingsPage : Page
    {
        private readonly MainWindow _parent;
        private GuiConfig _config;
        private bool _suppressAutoSave;

        private ObservableCollection<DnsItem> DnsItems { get; } = new ObservableCollection<DnsItem>();
        private ObservableCollection<KeyValueItem> HeaderItems { get; } = new ObservableCollection<KeyValueItem>();
        private ObservableCollection<KeyValueItem> ProxyItems { get; } = new ObservableCollection<KeyValueItem>();

        public SettingsPage(MainWindow parent)
        {
            _parent = parent;

            InitializeComponent();

            DnsListView.ItemsSource = DnsItems;
            HeadersListView.ItemsSource = HeaderItems;
            ProxyListView.ItemsSource = ProxyItems;

            // 选择变更 → 同步到数据模型
            DnsListView.SelectionChanged += DnsListView_SelectionChanged;
            ProxyListView.SelectionChanged += ProxyListView_SelectionChanged;

            LoadConfig();
            PopulateControls();
            SetupAutoSave();
        }

        // ==================== 配置加载 ====================

        private void LoadConfig()
        {
            _config = GuiConfig.Load();
        }

        private void PopulateControls()
        {
            _suppressAutoSave = true;
            try
            {
                PopulateControlsInternal();
                SyncSelectionFromModel();
            }
            finally
            {
                _suppressAutoSave = false;
            }
        }

        private void PopulateControlsInternal()
        {
            // IP 协议
            SelectComboBoxByTag(IpProtocolCombo, _config.IpProtocol);

            // 连接数
            ConnectionsBox.Value = _config.TotalConnections;

            // DNS 服务器（含启用/禁用状态）
            DnsItems.Clear();
            foreach (var item in _config.DnsServers ?? new List<DnsItem>())
            {
                DnsItems.Add(new DnsItem { Address = item.Address, IsEnabled = item.IsEnabled });
            }

            // 分块大小
            PopulateChunkSizeCombo();
            SelectChunkSize(_config.ChunkSize);

            // 速度阈值
            SpeedThresholdBox.Value = _config.SpeedThreshold;

            // 下载路径
            var configuredPath = _config.DownloadPath ?? "";
            DownloadPathBox.Text = configuredPath;
            UpdateDownloadPathDisplay(configuredPath);

            // 下载超时
            DownloadTimeoutBox.Value = _config.DownloadTimeoutSecs;
            ConnectivityTimeoutBox.Value = _config.ConnectivityTimeoutSecs;

            // 重试参数
            RetryCountBox.Value = _config.RetryCount;
            RetryReconnectBox.Value = _config.RetryReconnectCount;
            RetryRefetchEndpointBox.Value = _config.RetryRefetchEndpointCount;
            RetryRefetchUrlBox.Value = _config.RetryRefetchUrlCount;
            ContentLengthZeroRetryBox.Value = _config.ContentLengthZeroRetry;
            EndpointFailureThresholdBox.Value = _config.EndpointFailureThreshold;
            SpeedTestRetryCountBox.Value = _config.SpeedTestRetryCount;
            SpeedTestTimeoutBox.Value = _config.SpeedTestTimeoutSecs;
            DnsTimeoutBox.Value = _config.DnsTimeoutSecs;
            DnsRetryCountBox.Value = _config.DnsRetryCount;
            SchedulerCheckIntervalBox.Value = _config.SchedulerCheckIntervalSecs;
            SchedulerSlowThresholdBox.Value = _config.SchedulerSlowThreshold;
            SchedulerReallocateThresholdBox.Value = _config.SchedulerReallocateThreshold;

            // RPC 服务器
            RpcPortBox.Value = _config.RpcPort;
            RpcSecretBox.Text = _config.RpcSecret ?? "";

            // 调试
            DebugModeToggle.IsOn = _config.DebugMode;

            // 请求头
            HeaderItems.Clear();
            if (_config.Headers != null)
            {
                foreach (var kv in _config.Headers)
                {
                    HeaderItems.Add(new KeyValueItem { Key = kv.Key, Value = kv.Value });
                }
            }

            // 代理（含启用/禁用状态，IsSelected = 启用）
            ProxyItems.Clear();
            if (_config.Proxy != null)
            {
                foreach (var item in _config.Proxy)
                {
                    ProxyItems.Add(new KeyValueItem { Key = item.Key, Value = item.Value, IsSelected = item.IsSelected });
                }
            }
        }

        // ==================== 自动保存 ====================

        private void SetupAutoSave()
        {
            // NumberBox 控件
            ConnectionsBox.ValueChanged += (s, e) => AutoSave();
            SpeedThresholdBox.ValueChanged += (s, e) => AutoSave();
            DownloadTimeoutBox.ValueChanged += (s, e) => AutoSave();
            ConnectivityTimeoutBox.ValueChanged += (s, e) => AutoSave();
            RetryCountBox.ValueChanged += (s, e) => AutoSave();
            RetryReconnectBox.ValueChanged += (s, e) => AutoSave();
            RetryRefetchEndpointBox.ValueChanged += (s, e) => AutoSave();
            RetryRefetchUrlBox.ValueChanged += (s, e) => AutoSave();
            ContentLengthZeroRetryBox.ValueChanged += (s, e) => AutoSave();
            EndpointFailureThresholdBox.ValueChanged += (s, e) => AutoSave();
            SpeedTestRetryCountBox.ValueChanged += (s, e) => AutoSave();
            SpeedTestTimeoutBox.ValueChanged += (s, e) => AutoSave();
            DnsTimeoutBox.ValueChanged += (s, e) => AutoSave();
            DnsRetryCountBox.ValueChanged += (s, e) => AutoSave();
            SchedulerCheckIntervalBox.ValueChanged += (s, e) => AutoSave();
            SchedulerSlowThresholdBox.ValueChanged += (s, e) => AutoSave();
            SchedulerReallocateThresholdBox.ValueChanged += (s, e) => AutoSave();

            // RPC 服务器（含延迟保存，避免每输入一个字符都保存）
            RpcPortBox.ValueChanged += (s, e) => AutoSave();
            RpcSecretBox.TextChanged += (s, e) => AutoSave();

            // 调试模式
            DebugModeToggle.Toggled += (s, e) => AutoSave();
            DebugModeToggle.Toggled += (s, e) => AutoSave();

            // 下载路径
            DownloadPathBox.TextChanged += (s, e) =>
            {
                UpdateDownloadPathDisplay(DownloadPathBox.Text);
                AutoSave();
            };

            // ComboBox 控件
            IpProtocolCombo.SelectionChanged += (s, e) => AutoSave();
            ChunkSizeCombo.SelectionChanged += (s, e) => AutoSave();

            // 集合
            SetupCollectionAutoSave(DnsItems);
            SetupCollectionAutoSave(HeaderItems);
            SetupCollectionAutoSave(ProxyItems);
        }

        private void SetupCollectionAutoSave<T>(ObservableCollection<T> collection) where T : INotifyPropertyChanged
        {
            // 订阅已有项的 PropertyChanged（PopulateControls 在 SetupAutoSave 之前调用）
            foreach (T item in collection)
                item.PropertyChanged += OnItemChanged;

            collection.CollectionChanged += (s, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset)
                {
                    AutoSave();
                    return;
                }
                if (e.NewItems != null)
                {
                    foreach (T item in e.NewItems)
                        item.PropertyChanged += OnItemChanged;
                }
                if (e.OldItems != null)
                {
                    foreach (T item in e.OldItems)
                        item.PropertyChanged -= OnItemChanged;
                }
                AutoSave();
            };
        }

        private void OnItemChanged(object sender, PropertyChangedEventArgs e)
        {
            AutoSave();
        }

        private void AutoSave()
        {
            if (_suppressAutoSave) return;
            try
            {
                var oldPort = _config.RpcPort;
                var oldSecret = _config.RpcSecret;
                var oldDebugMode = _config.DebugMode;

                CollectConfigFromUI();
                _config.Save();

                // 实时同步 DNS 和代理条目到 NewListPage（仅条目，不同步启用/禁用）
                SyncEntriesToNewListPage();

                // RPC 端口或密钥变更 → 热重启
                if (_config.RpcPort != oldPort || _config.RpcSecret != oldSecret)
                {
                    _parent.RestartRpcServer(_config.RpcPort, _config.RpcSecret);
                }

                // 调试模式变更 → 即时生效
                if (_config.DebugMode != oldDebugMode)
                {
                    DebugLogger.Enabled = _config.DebugMode;
                }
            }
            catch
            {
                // 自动保存失败时静默忽略
            }
        }

        /// <summary>
        /// 从当前 UI 集合中提取 DNS 地址和代理条目，同步到 NewListPage。
        /// 只同步条目内容（地址/key/value），不传输启用/禁用状态。
        /// </summary>
        private void SyncEntriesToNewListPage()
        {
            var dnsAddresses = DnsItems
                .Where(d => !string.IsNullOrWhiteSpace(d.Address?.Trim()))
                .Select(d => d.Address.Trim())
                .ToList();

            var proxyEntries = ProxyItems
                .Where(p => !string.IsNullOrEmpty(p.Key?.Trim()) || !string.IsNullOrEmpty(p.Value?.Trim()))
                .Select(p => new KeyValueItem { Key = p.Key?.Trim() ?? "", Value = p.Value?.Trim() ?? "" })
                .ToList();

            _parent.SyncDnsProxyToNewListPage(dnsAddresses, proxyEntries);
        }

        // ==================== 重置 ====================

        private void ResetConfirmation_Click(object sender, RoutedEventArgs e)
        {
            _config = new GuiConfig();
            PopulateControls();

            // 保存默认配置
            try
            {
                _config.Save();
            }
            catch
            {
                // 静默忽略
            }

            // 关闭 Flyout
            ResetFlyout.Hide();
        }

        /// <summary>
        /// 清理所有相关文件后退出程序。
        /// </summary>
        private void CleanFilesConfirmation_Click(object sender, RoutedEventArgs e)
        {
            CleanFilesFlyout.Hide();

            var appDataDir = GuiConfig.GetConfigDir();
            var tempDir = Path.Combine(Path.GetTempPath(), "RDownloader");

            foreach (var dir in new[] { appDataDir, tempDir })
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }

            System.Windows.Application.Current.Shutdown();
        }

        // ==================== UI → Config ====================

        private void CollectConfigFromUI()
        {
            // IP 协议
            var selectedIp = IpProtocolCombo.SelectedItem as ComboBoxItem;
            _config.IpProtocol = selectedIp?.Tag?.ToString() ?? "OnlyIPv4";

            // 连接数
            _config.TotalConnections = ClampInt(NumValInt(ConnectionsBox, 64), 1, 256);

            // DNS 服务器（保留 IsEnabled 状态）
            _config.DnsServers = DnsItems
                .Where(d => !string.IsNullOrWhiteSpace(d.Address?.Trim()))
                .Select(d => new DnsItem { Address = d.Address?.Trim(), IsEnabled = d.IsEnabled })
                .ToList();

            // 分块大小
            _config.ChunkSize = GetSelectedChunkSize();

            // 速度阈值
            _config.SpeedThreshold = ClampDouble(NumVal(SpeedThresholdBox, 2.0), 0.1, 100.0);

            // 下载路径（无效路径保存为空，使用默认路径）
            var dpText = DownloadPathBox.Text?.Trim() ?? "";
            _config.DownloadPath = TryGetFullPath(dpText) != null ? dpText : "";

            // 下载超时
            _config.DownloadTimeoutSecs = ClampInt(NumValInt(DownloadTimeoutBox, 30), 1, 3600);

            // 重试参数
            _config.RetryCount = ClampInt(NumValInt(RetryCountBox, 3), 0, 100);
            _config.RetryReconnectCount = ClampInt(NumValInt(RetryReconnectBox, 3), 0, 100);
            _config.RetryRefetchEndpointCount = ClampInt(NumValInt(RetryRefetchEndpointBox, 2), 0, 100);
            _config.RetryRefetchUrlCount = ClampInt(NumValInt(RetryRefetchUrlBox, 1), 0, 100);
            _config.ContentLengthZeroRetry = ClampInt(NumValInt(ContentLengthZeroRetryBox, 3), 0, 100);
            _config.EndpointFailureThreshold = ClampInt(NumValInt(EndpointFailureThresholdBox, 3), 1, 1000);
            _config.SpeedTestRetryCount = ClampInt(NumValInt(SpeedTestRetryCountBox, 3), 0, 100);
            _config.SpeedTestTimeoutSecs = ClampInt(NumValInt(SpeedTestTimeoutBox, 15), 1, 3600);
            _config.ConnectivityTimeoutSecs = ClampInt(NumValInt(ConnectivityTimeoutBox, 3), 1, 300);
            _config.DnsTimeoutSecs = ClampInt(NumValInt(DnsTimeoutBox, 5), 1, 300);
            _config.DnsRetryCount = ClampInt(NumValInt(DnsRetryCountBox, 3), 0, 100);
            _config.SchedulerCheckIntervalSecs = ClampInt(NumValInt(SchedulerCheckIntervalBox, 3), 1, 3600);
            _config.SchedulerSlowThreshold = ClampDouble(NumVal(SchedulerSlowThresholdBox, 0.3), 0.0, 1.0);
            _config.SchedulerReallocateThreshold = ClampDouble(NumVal(SchedulerReallocateThresholdBox, 0.2), 0.0, 1.0);

            // RPC 服务器
            _config.RpcPort = ClampInt((int)(RpcPortBox.Value), 1, 65535);
            _config.RpcSecret = (RpcSecretBox.Text ?? "").Trim();

            // 调试
            _config.DebugMode = DebugModeToggle.IsOn == true;

            // 请求头：KeyValueItem → Dictionary
            _config.Headers = KvItemsToDict(HeaderItems);

            // 代理：保留 KeyValueItem（含 IsSelected 状态）
            _config.Proxy = ProxyItems
                .Where(item => !string.IsNullOrEmpty(item.Key?.Trim()) && !string.IsNullOrEmpty(item.Value?.Trim()))
                .Select(item => new KeyValueItem { Key = item.Key?.Trim(), Value = item.Value?.Trim(), IsSelected = item.IsSelected })
                .ToList();
        }

        // ==================== 选择同步 ====================

        private bool _syncingSelection;

        private void DnsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingSelection || _suppressAutoSave) return;

            // 取消选择 → IsEnabled = false
            foreach (DnsItem item in e.RemovedItems)
            {
                item.IsEnabled = false;
            }
            // 选中 → IsEnabled = true
            foreach (DnsItem item in e.AddedItems)
            {
                item.IsEnabled = true;
            }
        }

        private void ProxyListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingSelection || _suppressAutoSave) return;

            // 取消选择 → IsSelected = false
            foreach (KeyValueItem item in e.RemovedItems)
            {
                item.IsSelected = false;
            }
            // 选中 → IsSelected = true
            foreach (KeyValueItem item in e.AddedItems)
            {
                item.IsSelected = true;
            }
        }

        /// <summary>
        /// 根据数据模型同步 ListView 的选中状态。
        /// 在加载配置后调用。
        /// </summary>
        private void SyncSelectionFromModel()
        {
            _syncingSelection = true;
            try
            {
                // DNS
                DnsListView.SelectedItems.Clear();
                foreach (var item in DnsItems)
                {
                    if (item.IsEnabled)
                        DnsListView.SelectedItems.Add(item);
                }

                // 代理
                ProxyListView.SelectedItems.Clear();
                foreach (var item in ProxyItems)
                {
                    if (item.IsSelected)
                        ProxyListView.SelectedItems.Add(item);
                }
            }
            finally
            {
                _syncingSelection = false;
            }
        }

        // ==================== DNS 列表操作 ====================

        private void AddDns_Click(object sender, RoutedEventArgs e)
        {
            DnsItems.Add(new DnsItem());
        }

        private void DeleteDns_Click(object sender, RoutedEventArgs e)
        {
            var item = ((FrameworkElement)sender).DataContext as DnsItem;
            if (item != null) DnsItems.Remove(item);
        }

        private void MoveDnsUp_Click(object sender, RoutedEventArgs e)
        {
            var item = ((FrameworkElement)sender).DataContext as DnsItem;
            var idx = DnsItems.IndexOf(item);
            if (idx > 0) DnsItems.Move(idx, idx - 1);
        }

        private void MoveDnsDown_Click(object sender, RoutedEventArgs e)
        {
            var item = ((FrameworkElement)sender).DataContext as DnsItem;
            var idx = DnsItems.IndexOf(item);
            if (idx >= 0 && idx < DnsItems.Count - 1) DnsItems.Move(idx, idx + 1);
        }

        // ==================== 请求头 & 代理 列表操作（共享） ====================

        private void AddHeader_Click(object sender, RoutedEventArgs e)
        {
            HeaderItems.Add(new KeyValueItem());
        }

        private void AddProxy_Click(object sender, RoutedEventArgs e)
        {
            ProxyItems.Add(new KeyValueItem());
        }

        private void DeleteItem_Click(object sender, RoutedEventArgs e)
        {
            var item = ((FrameworkElement)sender).DataContext as KeyValueItem;
            if (item == null) return;
            HeaderItems.Remove(item);
            ProxyItems.Remove(item);
        }

        private void MoveItemUp_Click(object sender, RoutedEventArgs e)
        {
            var item = ((FrameworkElement)sender).DataContext as KeyValueItem;
            if (item == null) return;
            MoveItemUpIn(item, HeaderItems);
            MoveItemUpIn(item, ProxyItems);
        }

        private static bool MoveItemUpIn(KeyValueItem item, ObservableCollection<KeyValueItem> col)
        {
            var idx = col.IndexOf(item);
            if (idx > 0) { col.Move(idx, idx - 1); return true; }
            return false;
        }

        private void MoveItemDown_Click(object sender, RoutedEventArgs e)
        {
            var item = ((FrameworkElement)sender).DataContext as KeyValueItem;
            if (item == null) return;
            MoveItemDownIn(item, HeaderItems);
            MoveItemDownIn(item, ProxyItems);
        }

        private static bool MoveItemDownIn(KeyValueItem item, ObservableCollection<KeyValueItem> col)
        {
            var idx = col.IndexOf(item);
            if (idx >= 0 && idx < col.Count - 1) { col.Move(idx, idx + 1); return true; }
            return false;
        }

        // ==================== 分块大小 ComboBox ====================

        private void PopulateChunkSizeCombo()
        {
            ChunkSizeCombo.Items.Clear();
            foreach (var size in RdownConfig.ChunkSizeOptions)
            {
                ChunkSizeCombo.Items.Add(new ComboBoxItem
                {
                    Content = RdownConfig.FormatChunkSize(size),
                    Tag = size
                });
            }
        }

        private void SelectChunkSize(long bytes)
        {
            foreach (ComboBoxItem item in ChunkSizeCombo.Items)
            {
                if (item.Tag is long val && val == bytes)
                {
                    item.IsSelected = true;
                    return;
                }
            }
            // 值不在预设列表中，选中默认 64KB
            if (ChunkSizeCombo.Items.Count > 0)
                ((ComboBoxItem)ChunkSizeCombo.Items[0]).IsSelected = true;
        }

        private long GetSelectedChunkSize()
        {
            var selected = ChunkSizeCombo.SelectedItem as ComboBoxItem;
            if (selected?.Tag is long val)
                return val;
            return 65536; // 默认 64KB
        }

        // ==================== 浏览下载路径 / 显示有效路径 ====================

        private void BrowseDownloadPath_Click(object sender, RoutedEventArgs e)
        {
            var effectiveDir = GuiConfig.GetEffectiveDownloadDir();

            using (var dialog = new Microsoft.WindowsAPICodePack.Dialogs.CommonOpenFileDialog
            {
                Title = "选择下载文件的保存目录",
                IsFolderPicker = true,
                InitialDirectory = Directory.Exists(DownloadPathBox.Text)
                    ? DownloadPathBox.Text
                    : (Directory.Exists(effectiveDir) ? effectiveDir : "")
            })
            {
                if (dialog.ShowDialog() == Microsoft.WindowsAPICodePack.Dialogs.CommonFileDialogResult.Ok)
                {
                    DownloadPathBox.Text = dialog.FileName;
                }
            }
        }

        /// <summary>
        /// 更新下载路径描述文字，显示当前有效路径并验证目录是否存在。
        /// </summary>
        private static readonly string DefaultDownloadDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "RDownloader");

        /// <summary>
        /// 尝试规范化路径。成功返回有效路径；返回 null 表示目录不合法。
        /// </summary>
        private static string TryGetFullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                var full = Path.GetFullPath(path);
                // \\.\ 开头是设备命名空间（如 aux→\\.\aux），不是合法目录
                if (full.StartsWith(@"\\.\", StringComparison.Ordinal)) return null;
                return full;
            }
            catch { return null; }
        }

        private void UpdateDownloadPathDisplay(string configuredPath)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                DownloadPathDescription.Text = $"文件将保存到：{DefaultDownloadDir}";
                DownloadPathWarning.Visibility = Visibility.Collapsed;
                return;
            }

            var effectivePath = TryGetFullPath(configuredPath);
            if (effectivePath == null)
            {
                DownloadPathDescription.Text = $"文件将保存到：{DefaultDownloadDir}";
                DownloadPathWarning.Text = "⚠ 目录不合法，将使用默认路径。";
                DownloadPathWarning.Visibility = Visibility.Visible;
                return;
            }

            DownloadPathDescription.Text = $"文件将保存到：{effectivePath}";

            if (!Directory.Exists(effectivePath))
            {
                DownloadPathWarning.Text = "⚠ 此目录不存在，下载时会自动创建。";
                DownloadPathWarning.Visibility = Visibility.Visible;
            }
            else
            {
                DownloadPathWarning.Visibility = Visibility.Collapsed;
            }
        }

        // ==================== 辅助方法 ====================

        /// <summary>
        /// 读取 NumberBox 的 double 值，空值时返回默认值。
        /// </summary>
        private static double NumVal(iNKORE.UI.WPF.Modern.Controls.NumberBox box, double defaultValue)
        {
            return double.IsNaN(box.Value) ? defaultValue : box.Value;
        }

        /// <summary>
        /// 读取 NumberBox 的 int 值，空值时返回默认值。
        /// </summary>
        private static int NumValInt(iNKORE.UI.WPF.Modern.Controls.NumberBox box, int defaultValue)
        {
            return double.IsNaN(box.Value) ? defaultValue : (int)box.Value;
        }

        private static int ClampInt(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static double ClampDouble(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static void SelectComboBoxByTag(ComboBox combo, string tag)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (item.Tag?.ToString() == tag)
                {
                    item.IsSelected = true;
                    return;
                }
            }
            // fallback: select first item
            if (combo.Items.Count > 0)
                ((ComboBoxItem)combo.Items[0]).IsSelected = true;
        }

        /// <summary>
        /// KeyValueItem 集合 → Dictionary（过滤空 key）
        /// </summary>
        private static Dictionary<string, string> KvItemsToDict(ObservableCollection<KeyValueItem> items)
        {
            var dict = new Dictionary<string, string>();
            foreach (var item in items)
            {
                var k = item.Key?.Trim();
                var v = item.Value?.Trim();
                if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v))
                    dict[k] = v;
            }
            return dict;
        }
    }
}
