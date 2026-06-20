using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using iNKORE.UI.WPF.Modern.Controls;
using iNKORE.UI.WPF.Modern.Controls.Helpers;

namespace RDownloaderGUI
{
    public class DownloadSettings : Control
    {
        private NavigationView _headersNavView;
        private System.Windows.Controls.ListView _headersListView;
        private System.Windows.Controls.ListView _proxyListView;
        private System.Windows.Controls.ListView _dnsListView;
        private System.Windows.Controls.Expander _rootExpander;
        private System.Windows.Controls.Expander _headersExpander;
        private System.Windows.Controls.Expander _proxyExpander;
        private System.Windows.Controls.Expander _dnsExpander;
        private TextBox _downloadPathBox;
        private Button _browsePathButton;
        private TextBlock _downloadPathDescription;
        private TextBlock _downloadPathWarning;
        private bool _isSyncingHeaders;
        private HashSet<string> _enabledDnsSet;

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

        private void UpdateDownloadPathDisplay()
        {
            var path = DownloadPath ?? "";
            if (_downloadPathDescription == null) return;

            if (string.IsNullOrWhiteSpace(path))
            {
                // 全局控件：空路径 → 显示默认目录
                // 子项控件：空路径 → 显示全局设置的有效路径
                var fallback = !IsGlobal && !string.IsNullOrWhiteSpace(GlobalDownloadPath)
                    ? GlobalDownloadPath
                    : DefaultDownloadDir;
                _downloadPathDescription.Text = $"文件将保存到：{fallback}";
                if (_downloadPathWarning != null)
                    _downloadPathWarning.Visibility = Visibility.Collapsed;
                return;
            }

            var effectivePath = TryGetFullPath(path);
            if (effectivePath == null)
            {
                _downloadPathDescription.Text = $"文件将保存到：{DefaultDownloadDir}";
                if (_downloadPathWarning != null)
                {
                    _downloadPathWarning.Text = "⚠ 目录不合法，将使用默认路径。";
                    _downloadPathWarning.Visibility = Visibility.Visible;
                }
                return;
            }

            _downloadPathDescription.Text = $"文件将保存到：{effectivePath}";

            if (!Directory.Exists(effectivePath))
            {
                if (_downloadPathWarning != null)
                {
                    _downloadPathWarning.Text = "⚠ 此目录不存在，下载时会自动创建。";
                    _downloadPathWarning.Visibility = Visibility.Visible;
                }
            }
            else
            {
                if (_downloadPathWarning != null)
                    _downloadPathWarning.Visibility = Visibility.Collapsed;
            }
        }

        internal string GetEffectiveDownloadDir()
        {
            var path = DownloadPath ?? "";
            var valid = TryGetFullPath(path);
            if (valid != null) return valid;
            return GuiConfig.GetEffectiveDownloadDir();
        }

        private void BrowsePath_Click(object sender, RoutedEventArgs e)
        {
            var initialDir = !string.IsNullOrWhiteSpace(DownloadPath) && Directory.Exists(DownloadPath)
                ? DownloadPath
                : (Directory.Exists(DefaultDownloadDir) ? DefaultDownloadDir : "");

            using (var dialog = new Microsoft.WindowsAPICodePack.Dialogs.CommonOpenFileDialog
            {
                Title = "选择下载文件的保存目录",
                IsFolderPicker = true,
                InitialDirectory = initialDir
            })
            {
                if (dialog.ShowDialog() == Microsoft.WindowsAPICodePack.Dialogs.CommonFileDialogResult.Ok)
                {
                    DownloadPath = dialog.FileName;
                }
            }
        }

        private void DownloadPathBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateDownloadPathDisplay();
        }

        /// <summary>
        /// 当任何设置（Headers/Proxy/DNS）发生更改时触发，供外部监听以刷新 UI。
        /// </summary>
        public event Action SettingsChanged;

        /// <summary>
        /// 设为 true 可抑制 SettingsChanged 触发（用于配置加载期间的批量操作）。
        /// </summary>
        internal bool SuppressSettingsChanged { get; set; }

        private void FireSettingsChanged()
        {
            if (!SuppressSettingsChanged)
                SettingsChanged?.Invoke();
        }

        static DownloadSettings()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(DownloadSettings), new FrameworkPropertyMetadata(typeof(DownloadSettings)));
        }

        public DownloadSettings()
        {
            HeaderItems = new ObservableCollection<KeyValueItem>();
            ProxyItems = new ObservableCollection<KeyValueItem>();
            DnsItems = new ObservableCollection<string>();

            DeleteItemCommand = new RelayCommand(param =>
            {
                var item = param as KeyValueItem;
                if (item == null) return;
                HeaderItems.Remove(item);
                ProxyItems.Remove(item);
            });

            GoToSettingsCommand = new RelayCommand(_ =>
            {
                var window = Window.GetWindow(this);
                if (window is MainWindow mainWindow)
                {
                    mainWindow.NavigateToSettings();
                }
            });

            AddHeaderItemCommand = new RelayCommand(_ =>
            {
                HeaderItems.Add(new KeyValueItem());
            });
        }

        #region DownloadPath

        public static readonly DependencyProperty DownloadPathProperty =
            DependencyProperty.Register(
                nameof(DownloadPath),
                typeof(string),
                typeof(DownloadSettings),
                new FrameworkPropertyMetadata(
                    string.Empty,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnDownloadPathChanged));

        public string DownloadPath
        {
            get => (string)GetValue(DownloadPathProperty);
            set => SetValue(DownloadPathProperty, value);
        }

        private static void OnDownloadPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ds = (DownloadSettings)d;
            ds.UpdateDownloadPathDisplay();
            if (ds.IsGlobal)
                ds.FireSettingsChanged();
        }

        #endregion

        #region GlobalDownloadPath

        public static readonly DependencyProperty GlobalDownloadPathProperty =
            DependencyProperty.Register(
                nameof(GlobalDownloadPath),
                typeof(string),
                typeof(DownloadSettings),
                new FrameworkPropertyMetadata(
                    string.Empty,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnGlobalDownloadPathChanged));

        public string GlobalDownloadPath
        {
            get => (string)GetValue(GlobalDownloadPathProperty);
            set => SetValue(GlobalDownloadPathProperty, value);
        }

        private static void OnGlobalDownloadPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((DownloadSettings)d).UpdateDownloadPathDisplay();
        }

        #endregion

        #region HeadersText (with Text ↔ Items sync)

        public static readonly DependencyProperty HeadersTextProperty =
            DependencyProperty.Register(
                nameof(HeadersText),
                typeof(string),
                typeof(DownloadSettings),
                new FrameworkPropertyMetadata(
                    string.Empty,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnHeadersTextChanged));

        public string HeadersText
        {
            get => (string)GetValue(HeadersTextProperty);
            set => SetValue(HeadersTextProperty, value);
        }

        private static void OnHeadersTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ds = (DownloadSettings)d;
            ds.SyncTextToHeaderItems();
            ds.FireSettingsChanged();
        }

        #endregion

        #region HeaderItems (with collection change tracking)

        public static readonly DependencyProperty HeaderItemsProperty =
            DependencyProperty.Register(
                nameof(HeaderItems),
                typeof(ObservableCollection<KeyValueItem>),
                typeof(DownloadSettings),
                new PropertyMetadata(null, OnHeaderItemsPropertyChanged));

        public ObservableCollection<KeyValueItem> HeaderItems
        {
            get => (ObservableCollection<KeyValueItem>)GetValue(HeaderItemsProperty);
            set => SetValue(HeaderItemsProperty, value);
        }

        private static void OnHeaderItemsPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (DownloadSettings)d;
            if (e.OldValue is ObservableCollection<KeyValueItem> oldCol)
            {
                oldCol.CollectionChanged -= control.OnHeaderItemsCollectionChanged;
                control.UnsubscribeItemPropertyChanged(oldCol);
            }
            if (e.NewValue is ObservableCollection<KeyValueItem> newCol)
            {
                newCol.CollectionChanged += control.OnHeaderItemsCollectionChanged;
                control.SubscribeItemPropertyChanged(newCol);
            }
        }

        #endregion

        #region ProxyItems

        public static readonly DependencyProperty ProxyItemsProperty =
            DependencyProperty.Register(
                nameof(ProxyItems),
                typeof(ObservableCollection<KeyValueItem>),
                typeof(DownloadSettings),
                new PropertyMetadata(null));

        public ObservableCollection<KeyValueItem> ProxyItems
        {
            get => (ObservableCollection<KeyValueItem>)GetValue(ProxyItemsProperty);
            set => SetValue(ProxyItemsProperty, value);
        }

        #endregion

        #region DnsItems

        public static readonly DependencyProperty DnsItemsProperty =
            DependencyProperty.Register(
                nameof(DnsItems),
                typeof(ObservableCollection<string>),
                typeof(DownloadSettings),
                new PropertyMetadata(null));

        public ObservableCollection<string> DnsItems
        {
            get => (ObservableCollection<string>)GetValue(DnsItemsProperty);
            set => SetValue(DnsItemsProperty, value);
        }

        #endregion

        #region IsHeadersTextMode (read-only, toggled by NavigationView)

        private static readonly DependencyPropertyKey IsHeadersTextModePropertyKey =
            DependencyProperty.RegisterReadOnly(
                nameof(IsHeadersTextMode),
                typeof(bool),
                typeof(DownloadSettings),
                new PropertyMetadata(true));

        public static readonly DependencyProperty IsHeadersTextModeProperty =
            IsHeadersTextModePropertyKey.DependencyProperty;

        public bool IsHeadersTextMode
        {
            get => (bool)GetValue(IsHeadersTextModeProperty);
            private set => SetValue(IsHeadersTextModePropertyKey, value);
        }

        #endregion

        #region Effective settings (for re-probe fingerprint & temp config)

        /// <summary>
        /// 获取所有非空的 Headers（Dictionary 形式）。
        /// Headers 不使用 IsSelected 机制——所有项均视为有效。
        /// </summary>
        public Dictionary<string, string> GetEffectiveHeaders()
        {
            var dict = new Dictionary<string, string>();
            if (HeaderItems != null)
            {
                foreach (var item in HeaderItems)
                {
                    if (!string.IsNullOrEmpty(item.Key) || !string.IsNullOrEmpty(item.Value))
                        dict[item.Key ?? ""] = item.Value ?? "";
                }
            }
            return dict;
        }

        /// <summary>
        /// 获取选中的代理规则（Dictionary 形式）。通过 ListView 选中项获取。
        /// </summary>
        public Dictionary<string, string> GetEffectiveProxy()
        {
            var dict = new Dictionary<string, string>();
            if (_proxyListView != null)
            {
                foreach (KeyValueItem item in _proxyListView.SelectedItems)
                {
                    if (!string.IsNullOrEmpty(item.Key) || !string.IsNullOrEmpty(item.Value))
                        dict[item.Key ?? ""] = item.Value ?? "";
                }
            }
            else
            {
                // 模板未应用 → 回退到 IsSelected
                foreach (var item in ProxyItems ?? Enumerable.Empty<KeyValueItem>())
                {
                    if (item.IsSelected && (!string.IsNullOrEmpty(item.Key) || !string.IsNullOrEmpty(item.Value)))
                        dict[item.Key ?? ""] = item.Value ?? "";
                }
            }
            return dict;
        }

        /// <summary>
        /// 获取选中的 DNS 服务器列表。
        /// 若模板未应用（_dnsListView 为 null），回退到全部 DnsItems。
        /// </summary>
        public List<string> GetEffectiveDnsServers()
        {
            if (_dnsListView != null)
                return _dnsListView.SelectedItems.Cast<string>().ToList();
            // 模板未应用 → 默认全选
            return DnsItems?.ToList() ?? new List<string>();
        }

        /// <summary>
        /// 从当前控件状态构建一个 RdownConfig（仅填充 Headers/Proxy/DnsServers）。
        /// </summary>
        public RdownConfig BuildEffectiveConfig()
        {
            return new RdownConfig
            {
                Headers = GetEffectiveHeaders(),
                Proxy = GetEffectiveProxy(),
                DnsServers = GetEffectiveDnsServers()
            };
        }

        /// <summary>
        /// 将当前有效设置保存为临时 rdown_config.json，返回文件路径。
        /// 保存到 %TEMP%\RDownloader\reprobe\<guid>\rdown_config.json。
        /// </summary>
        public string SaveEffectiveConfigToTemp()
        {
            var config = BuildEffectiveConfig();
            var dir = Path.Combine(Path.GetTempPath(), "RDownloader", "reprobe", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "rdown_config.json");
            config.Save(path);
            return path;
        }

        #endregion

        #region Header text ↔ items sync

        private void SyncTextToHeaderItems()
        {
            if (_isSyncingHeaders) return;
            _isSyncingHeaders = true;

            var text = HeadersText ?? string.Empty;

            // unsubscribe old items
            UnsubscribeItemPropertyChanged(HeaderItems);
            HeaderItems.Clear();

            if (!string.IsNullOrWhiteSpace(text))
            {
                foreach (var line in text.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;
                    var colonIndex = trimmed.IndexOf(':');
                    if (colonIndex > 0)
                    {
                        var item = new KeyValueItem
                        {
                            Key = trimmed.Substring(0, colonIndex).Trim(),
                            Value = trimmed.Substring(colonIndex + 1).Trim()
                        };
                        HeaderItems.Add(item);
                    }
                }
            }

            // subscribe new items
            SubscribeItemPropertyChanged(HeaderItems);

            _isSyncingHeaders = false;
        }

        private void OnHeaderItemsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
                foreach (KeyValueItem item in e.NewItems)
                    item.PropertyChanged += OnHeaderItemPropertyChanged;
            if (e.OldItems != null)
                foreach (KeyValueItem item in e.OldItems)
                    item.PropertyChanged -= OnHeaderItemPropertyChanged;

            SyncHeaderItemsToText();
            FireSettingsChanged();
        }

        private void OnHeaderItemPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            SyncHeaderItemsToText();

            // 最后一行非空 → 自动添加空行
            if (_isSyncingHeaders) return;
            var item = (KeyValueItem)sender;
            if (item == HeaderItems.LastOrDefault()
                && (!string.IsNullOrEmpty(item.Key) || !string.IsNullOrEmpty(item.Value)))
            {
                _isSyncingHeaders = true;
                HeaderItems.Add(new KeyValueItem());
                _isSyncingHeaders = false;
            }

            FireSettingsChanged();
        }

        private void SyncHeaderItemsToText()
        {
            if (_isSyncingHeaders) return;
            _isSyncingHeaders = true;

            var sb = new StringBuilder();
            foreach (var item in HeaderItems)
            {
                if (string.IsNullOrEmpty(item.Key) && string.IsNullOrEmpty(item.Value))
                    continue;
                sb.AppendLine(item.Key + ": " + item.Value);
            }
            HeadersText = sb.ToString();

            _isSyncingHeaders = false;
        }

        private void SubscribeItemPropertyChanged(ObservableCollection<KeyValueItem> collection)
        {
            foreach (var item in collection)
                item.PropertyChanged += OnHeaderItemPropertyChanged;
        }

        private void UnsubscribeItemPropertyChanged(ObservableCollection<KeyValueItem> collection)
        {
            foreach (var item in collection)
                item.PropertyChanged -= OnHeaderItemPropertyChanged;
        }

        #endregion

        #region IsGlobal

        public static readonly DependencyProperty IsGlobalProperty =
            DependencyProperty.Register(
                nameof(IsGlobal),
                typeof(bool),
                typeof(DownloadSettings),
                new FrameworkPropertyMetadata(
                    false,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public bool IsGlobal
        {
            get => (bool)GetValue(IsGlobalProperty);
            set => SetValue(IsGlobalProperty, value);
        }

        #endregion

        #region Commands

        public ICommand DeleteItemCommand { get; }
        public ICommand GoToSettingsCommand { get; }
        public ICommand AddHeaderItemCommand { get; }

        #endregion

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            if (_headersNavView != null)
            {
                _headersNavView.SelectionChanged -= HeadersNavView_SelectionChanged;
            }

            if (_headersListView != null)
            {
                _headersListView.SelectionChanged -= HeadersListView_SelectionChanged;
            }

            if (_headersExpander != null)
            {
                _headersExpander.Expanded -= OnInnerExpanderToggled;
                _headersExpander.Collapsed -= OnInnerExpanderToggled;
            }
            if (_proxyExpander != null)
            {
                _proxyExpander.Expanded -= OnInnerExpanderToggled;
                _proxyExpander.Collapsed -= OnInnerExpanderToggled;
            }
            if (_dnsExpander != null)
            {
                _dnsExpander.Expanded -= OnInnerExpanderToggled;
                _dnsExpander.Collapsed -= OnInnerExpanderToggled;
            }
            if (_rootExpander != null)
            {
                _rootExpander.Expanded -= OnInnerExpanderToggled;
                _rootExpander.Collapsed -= OnInnerExpanderToggled;
            }

            _headersNavView = GetTemplateChild("PART_HeadersNavView") as NavigationView;
            _headersListView = GetTemplateChild("HeadersListView") as System.Windows.Controls.ListView;
            _proxyListView = GetTemplateChild("ProxyListView") as System.Windows.Controls.ListView;
            _dnsListView = GetTemplateChild("DnsListView") as System.Windows.Controls.ListView;
            _headersExpander = GetTemplateChild("PART_HeadersExpander") as System.Windows.Controls.Expander;
            _proxyExpander = GetTemplateChild("PART_ProxyExpander") as System.Windows.Controls.Expander;
            _dnsExpander = GetTemplateChild("PART_DnsExpander") as System.Windows.Controls.Expander;
            _rootExpander = GetTemplateChild("PART_RootExpander") as System.Windows.Controls.Expander;

            // ── 下载路径 ──
            if (_downloadPathBox != null)
                _downloadPathBox.TextChanged -= DownloadPathBox_TextChanged;
            if (_browsePathButton != null)
                _browsePathButton.Click -= BrowsePath_Click;

            _downloadPathBox = GetTemplateChild("PART_DownloadPathBox") as TextBox;
            _browsePathButton = GetTemplateChild("PART_BrowsePathButton") as Button;
            _downloadPathDescription = GetTemplateChild("PART_DownloadPathDescription") as TextBlock;
            _downloadPathWarning = GetTemplateChild("PART_DownloadPathWarning") as TextBlock;

            if (_downloadPathBox != null)
            {
                _downloadPathBox.TextChanged += DownloadPathBox_TextChanged;
                ControlHelper.SetPlaceholderText(_downloadPathBox,
                    IsGlobal ? "留空使用默认路径" : "留空使用全局路径");
            }
            if (_browsePathButton != null)
                _browsePathButton.Click += BrowsePath_Click;

            // 全局设置且路径为空 → 预填充默认路径
            if (IsGlobal && string.IsNullOrWhiteSpace(DownloadPath))
            {
                DownloadPath = DefaultDownloadDir;
            }
            UpdateDownloadPathDisplay();

            if (_headersNavView != null)
            {
                _headersNavView.SelectionChanged += HeadersNavView_SelectionChanged;

                // 默认选中"文本"
                if (_headersNavView.MenuItems.Count > 0)
                {
                    _headersNavView.SelectedItem = _headersNavView.MenuItems[0];
                }
            }

            if (_headersListView != null)
            {
                _headersListView.SelectionChanged += HeadersListView_SelectionChanged;
            }

            if (_proxyListView != null)
            {
                _proxyListView.SelectionChanged += (s, e) =>
                {
                    if (!SuppressSettingsChanged) SettingsChanged?.Invoke();
                };
            }
            if (_dnsListView != null)
            {
                _dnsListView.SelectionChanged += (s, e) =>
                {
                    if (!SuppressSettingsChanged) SettingsChanged?.Invoke();
                };
            }

            if (_headersExpander != null)
            {
                _headersExpander.Expanded += OnInnerExpanderToggled;
                _headersExpander.Collapsed += OnInnerExpanderToggled;
            }
            if (_proxyExpander != null)
            {
                _proxyExpander.Expanded += OnInnerExpanderToggled;
                _proxyExpander.Collapsed += OnInnerExpanderToggled;
            }
            if (_dnsExpander != null)
            {
                _dnsExpander.Expanded += OnInnerExpanderToggled;
                _dnsExpander.Collapsed += OnInnerExpanderToggled;
            }
            if (_rootExpander != null)
            {
                _rootExpander.Expanded += OnInnerExpanderToggled;
                _rootExpander.Collapsed += OnInnerExpanderToggled;
            }

            // 模板首次应用：根据数据状态选择
            if (_proxyListView != null && ProxyItems.Count > 0)
            {
                foreach (var item in ProxyItems)
                {
                    if (item.IsSelected)
                        _proxyListView.SelectedItems.Add(item);
                }
            }
            if (_dnsListView != null && DnsItems.Count > 0)
            {
                if (_enabledDnsSet != null && _enabledDnsSet.Count > 0)
                {
                    // 只选中启用的 DNS
                    foreach (var addr in _enabledDnsSet)
                    {
                        if (DnsItems.Contains(addr))
                            _dnsListView.SelectedItems.Add(addr);
                    }
                }
                else
                {
                    _dnsListView.SelectAll();
                }
            }
        }

        private void HeadersNavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is NavigationViewItem item)
            {
                IsHeadersTextMode = item.Tag?.ToString() != "List";
            }
        }

        private void HeadersListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is System.Windows.Controls.ListView listView)
                listView.SelectedIndex = -1;
        }

        private void OnInnerExpanderToggled(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
        }

        #region PopulateFromConfig

        /// <summary>
        /// 从 GUI 配置填充 Headers / Proxy / DNS 到控件集合中。
        /// 仅填充启用的代理和 DNS。调用前会清空现有数据。
        /// </summary>
        public void PopulateFromConfig(GuiConfig config)
        {
            if (config == null) return;

            // 请求头：全量填充（无启用/禁用）
            UnsubscribeItemPropertyChanged(HeaderItems);
            HeaderItems.Clear();
            if (config.Headers != null)
            {
                foreach (var kv in config.Headers)
                {
                    HeaderItems.Add(new KeyValueItem { Key = kv.Key, Value = kv.Value });
                }
            }
            SubscribeItemPropertyChanged(HeaderItems);

            // 代理规则：全量填充，IsSelected 保持原值（禁用的 = false）
            ProxyItems.Clear();
            if (config.Proxy != null)
            {
                foreach (var item in config.Proxy)
                {
                    ProxyItems.Add(new KeyValueItem { Key = item.Key, Value = item.Value, IsSelected = item.IsSelected });
                }
            }

            // DNS 服务器：全量填充，记录启用集合供模板渲染后选择
            DnsItems.Clear();
            _enabledDnsSet = new HashSet<string>();
            if (config.DnsServers != null)
            {
                foreach (var item in config.DnsServers)
                {
                    if (!string.IsNullOrWhiteSpace(item.Address))
                    {
                        DnsItems.Add(item.Address);
                        if (item.IsEnabled)
                            _enabledDnsSet.Add(item.Address);
                    }
                }
            }

            // 延迟同步选择状态：只选中启用的项
            if (_dnsListView != null && _enabledDnsSet != null)
            {
                var enabledSet = new HashSet<string>(_enabledDnsSet);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _dnsListView.SelectedItems.Clear();
                    foreach (var addr in enabledSet)
                    {
                        if (DnsItems.Contains(addr))
                            _dnsListView.SelectedItems.Add(addr);
                    }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            if (_proxyListView != null)
            {
                // 只选中 IsSelected == true 的代理项
                var selectedProxies = ProxyItems.Where(x => x.IsSelected).ToList();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _proxyListView.SelectedItems.Clear();
                    foreach (var item in selectedProxies)
                    {
                        if (ProxyItems.Contains(item))
                            _proxyListView.SelectedItems.Add(item);
                    }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        #endregion

        #region SyncDnsProxyEntries (SettingsPage → NewListPage 实时同步)

        /// <summary>
        /// 仅同步 DNS 地址列表和代理条目（key/value），保留启用/禁用状态不变。
        /// 由 SettingsPage 在用户增删改条目时实时调用。
        /// </summary>
        /// <param name="dnsAddresses">来自 SettingsPage 的 DNS 地址列表</param>
        /// <param name="proxyEntries">来自 SettingsPage 的代理条目列表（仅 key/value 有效）</param>
        public void SyncDnsProxyEntries(List<string> dnsAddresses, List<KeyValueItem> proxyEntries)
        {
            // ── DNS：只同步地址，保留当前 ListView 选中态 ──
            if (dnsAddresses != null)
            {
                // 记录当前启用的地址
                var enabledSet = new HashSet<string>();
                if (_dnsListView != null)
                    foreach (string addr in _dnsListView.SelectedItems)
                        enabledSet.Add(addr);
                else if (_enabledDnsSet != null)
                    enabledSet = new HashSet<string>(_enabledDnsSet);
                else
                    enabledSet = new HashSet<string>(DnsItems);

                DnsItems.Clear();
                foreach (var addr in dnsAddresses)
                    DnsItems.Add(addr);

                _enabledDnsSet = enabledSet;

                // 恢复选中
                if (_dnsListView != null)
                {
                    var set = new HashSet<string>(enabledSet);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _dnsListView.SelectedItems.Clear();
                        foreach (var addr in set)
                            if (DnsItems.Contains(addr))
                                _dnsListView.SelectedItems.Add(addr);
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }

            // ── 代理：只同步 key/value，保留 IsSelected ──
            if (proxyEntries != null)
            {
                // 记录当前选中的 key
                var oldSelectedKeys = new HashSet<string>();
                foreach (var item in ProxyItems)
                    if (item.IsSelected)
                        oldSelectedKeys.Add(item.Key ?? "");

                ProxyItems.Clear();
                foreach (var item in proxyEntries)
                {
                    ProxyItems.Add(new KeyValueItem
                    {
                        Key = item.Key ?? "",
                        Value = item.Value ?? "",
                        IsSelected = oldSelectedKeys.Contains(item.Key ?? "")
                    });
                }

                // 恢复选中
                if (_proxyListView != null)
                {
                    var selected = ProxyItems.Where(x => x.IsSelected).ToList();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _proxyListView.SelectedItems.Clear();
                        foreach (var item in selected)
                            if (ProxyItems.Contains(item))
                                _proxyListView.SelectedItems.Add(item);
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
        }

        #endregion
    }
}
