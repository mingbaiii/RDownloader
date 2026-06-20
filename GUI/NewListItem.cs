using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace RDownloaderGUI
{
    /// <summary>
    /// 按照步骤 1a 或 1b 操作，然后执行步骤 2 以在 XAML 文件中使用此自定义控件。
    ///
    /// 步骤 1a) 在当前项目中存在的 XAML 文件中使用该自定义控件。
    /// 将此 XmlNamespace 特性添加到要使用该特性的标记文件的根
    /// 元素中:
    ///
    ///     xmlns:MyNamespace="clr-namespace:RDownloaderGUI"
    ///
    ///
    /// 步骤 1b) 在其他项目中存在的 XAML 文件中使用该自定义控件。
    /// 将此 XmlNamespace 特性添加到要使用该特性的标记文件的根
    /// 元素中:
    ///
    ///     xmlns:MyNamespace="clr-namespace:RDownloaderGUI;assembly=RDownloaderGUI"
    ///
    /// 您还需要添加一个从 XAML 文件所在的项目到此项目的项目引用，
    /// 并重新生成以避免编译错误:
    ///
    ///     在解决方案资源管理器中右击目标项目，然后依次单击
    ///     “添加引用”->“项目”->[浏览查找并选择此项目]
    ///
    ///
    /// 步骤 2)
    /// 继续操作并在 XAML 文件中使用控件。
    ///
    ///     <MyNamespace:NewListItem/>
    ///
    /// </summary>
    public class NewListItem : Control
    {
        private DownloadSettings _itemDownloadSettings;
        private GuiConfig _pendingConfig;
        private List<string> _pendingDnsAddresses;
        private List<KeyValueItem> _pendingProxyEntries;
        private string _pendingGlobalDownloadPath;

        static NewListItem()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(NewListItem), new FrameworkPropertyMetadata(typeof(NewListItem)));
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _itemDownloadSettings = GetTemplateChild("PART_ItemDownloadSettings") as DownloadSettings;

            // 模板渲染完毕，应用之前暂存的配置
            if (_pendingConfig != null && _itemDownloadSettings != null)
            {
                _itemDownloadSettings.PopulateFromConfig(_pendingConfig);
            }

            // 应用暂存的 DNS/Proxy 实时同步数据（覆盖 _pendingConfig 中可能过时的条目）
            if ((_pendingDnsAddresses != null || _pendingProxyEntries != null) && _itemDownloadSettings != null)
            {
                _itemDownloadSettings.SyncDnsProxyEntries(
                    _pendingDnsAddresses ?? new List<string>(),
                    _pendingProxyEntries ?? new List<KeyValueItem>());
                _pendingDnsAddresses = null;
                _pendingProxyEntries = null;
            }

            // 应用暂存的全局下载路径
            if (_pendingGlobalDownloadPath != null && _itemDownloadSettings != null)
            {
                _itemDownloadSettings.GlobalDownloadPath = _pendingGlobalDownloadPath;
                _pendingGlobalDownloadPath = null;
            }
        }

        /// <summary>
        /// 将全局配置填充到内嵌的 DownloadSettings。
        /// 若模板尚未渲染，先暂存配置待 OnApplyTemplate 时再应用。
        /// </summary>
        public void PopulateItemSettings(GuiConfig config)
        {
            _pendingConfig = config;
            if (_itemDownloadSettings != null)
            {
                _itemDownloadSettings.PopulateFromConfig(config);
            }
        }

        /// <summary>
        /// 仅同步 DNS 和代理条目到内嵌的 DownloadSettings（不同步启用/禁用）。
        /// 若模板尚未渲染，暂存数据待 OnApplyTemplate 时再应用。
        /// </summary>
        public void SyncDnsProxyEntries(List<string> dnsAddresses, List<KeyValueItem> proxyEntries)
        {
            if (_itemDownloadSettings != null)
            {
                _itemDownloadSettings.SyncDnsProxyEntries(dnsAddresses, proxyEntries);
            }
            else
            {
                // 模板尚未渲染，暂存待 OnApplyTemplate 应用
                _pendingDnsAddresses = dnsAddresses;
                _pendingProxyEntries = proxyEntries;
            }
        }

        /// <summary>
        /// 设置全局下载路径，供内嵌 DownloadSettings 在路径为空时显示回退提示。
        /// 若模板尚未渲染，暂存路径待 OnApplyTemplate 时再应用。
        /// </summary>
        internal void SetGlobalDownloadPath(string path)
        {
            if (_itemDownloadSettings != null)
            {
                _itemDownloadSettings.GlobalDownloadPath = path ?? string.Empty;
            }
            else
            {
                _pendingGlobalDownloadPath = path;
            }
        }

        /// <summary>
        /// 获取内嵌 DownloadSettings 的有效配置（Headers/Proxy/DnsServers）。
        /// 若模板未渲染则回退到全部默认选中。
        /// </summary>
        internal Dictionary<string, string> GetEffectiveHeaders()
            => _itemDownloadSettings?.GetEffectiveHeaders() ?? new Dictionary<string, string>();

        internal Dictionary<string, string> GetEffectiveProxy()
            => _itemDownloadSettings?.GetEffectiveProxy() ?? new Dictionary<string, string>();

        internal List<string> GetEffectiveDnsServers()
            => _itemDownloadSettings?.GetEffectiveDnsServers() ?? new List<string>();

        /// <summary>
        /// 获取内嵌 DownloadSettings 的有效 RdownConfig。
        /// </summary>
        internal RdownConfig GetEffectiveConfig()
            => _itemDownloadSettings?.BuildEffectiveConfig() ?? new RdownConfig();

        public static readonly DependencyProperty FileNameProperty =
            DependencyProperty.Register(
                nameof(FileName),
                typeof(string),
                typeof(NewListItem),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty LinkProperty =
            DependencyProperty.Register(
                nameof(Link),
                typeof(string),
                typeof(NewListItem),
                new FrameworkPropertyMetadata(
                    string.Empty,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public static readonly DependencyProperty TaskIdProperty =
            DependencyProperty.Register(
                nameof(TaskId),
                typeof(string),
                typeof(NewListItem),
                new PropertyMetadata(string.Empty));

        public string FileName
        {
            get => (string)GetValue(FileNameProperty);
            set => SetValue(FileNameProperty, value);
        }

        public string Link
        {
            get => (string)GetValue(LinkProperty);
            set => SetValue(LinkProperty, value);
        }

        /// <summary>
        /// rdown 探测返回的任务 ID。内部使用，不显示在 UI 上。
        /// </summary>
        public string TaskId
        {
            get => (string)GetValue(TaskIdProperty);
            set => SetValue(TaskIdProperty, value);
        }

        /// <summary>
        /// 上次 ProbeAsync 成功时使用的配置指纹（SHA256）。
        /// 重新解析时比较，相同则跳过。
        /// </summary>
        internal string LastProbeFingerprint { get; set; }

        public static readonly DependencyProperty UseGlobalSettingsProperty =
            DependencyProperty.Register(
                nameof(UseGlobalSettings),
                typeof(bool),
                typeof(NewListItem),
                new FrameworkPropertyMetadata(
                    true,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public bool UseGlobalSettings
        {
            get => (bool)GetValue(UseGlobalSettingsProperty);
            set => SetValue(UseGlobalSettingsProperty, value);
        }

        public static readonly DependencyProperty DownloadPathProperty =
            DependencyProperty.Register(
                nameof(DownloadPath),
                typeof(string),
                typeof(NewListItem),
                new FrameworkPropertyMetadata(
                    string.Empty,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public string DownloadPath
        {
            get => (string)GetValue(DownloadPathProperty);
            set => SetValue(DownloadPathProperty, value);
        }

        public static readonly DependencyProperty HeadersTextProperty =
            DependencyProperty.Register(
                nameof(HeadersText),
                typeof(string),
                typeof(NewListItem),
                new FrameworkPropertyMetadata(
                    string.Empty,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public string HeadersText
        {
            get => (string)GetValue(HeadersTextProperty);
            set => SetValue(HeadersTextProperty, value);
        }

        public static readonly DependencyProperty DeleteCommandProperty =
            DependencyProperty.Register(
                nameof(DeleteCommand),
                typeof(ICommand),
                typeof(NewListItem),
                new PropertyMetadata(null));

        public ICommand DeleteCommand
        {
            get => (ICommand)GetValue(DeleteCommandProperty);
            set => SetValue(DeleteCommandProperty, value);
        }
    }
}
