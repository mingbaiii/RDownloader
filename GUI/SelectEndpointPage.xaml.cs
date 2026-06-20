using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using iNKORE.UI.WPF.Modern.Controls;
using Page = System.Windows.Controls.Page;

namespace RDownloaderGUI
{
    /// <summary>
    /// SelectEndpointPage.xaml 的交互逻辑
    /// </summary>
    public partial class SelectEndpointPage : Page
    {
        private readonly MainWindow _parent;
        private bool _suppressToggle;

        public SelectEndpointPage(MainWindow parent)
        {
            _parent = parent;
            InitializeComponent();
        }

        /// <summary>
        /// 从所有探测结果中加载端点列表，为每个文件创建一个 SettingsExpander，内部使用 SettingsCard 展示各端点。
        /// </summary>
        public async Task LoadEndpointsAsync()
        {
            _suppressToggle = true;
            try
            {
                EndpointStackPanel.Children.Clear();

                var probeResults = _parent.ProbeResults;
                if (probeResults == null || probeResults.Count == 0)
                    return;

                foreach (var probeItem in probeResults)
                {
                    if (string.IsNullOrEmpty(probeItem.TaskId))
                        continue;

                    // 获取该文件的端点列表
                    List<EndpointInfo> endpoints;
                    try
                    {
                        endpoints = await _parent.DownloadManager.GetEndpointsAsync(probeItem.TaskId);
                    }
                    catch
                    {
                        endpoints = new List<EndpointInfo>();
                    }

                    // 为每个文件创建一个 SettingsExpander
                    var expander = new SettingsExpander
                    {
                        Header = probeItem.FileName ?? "(未知文件)",
                        Description = probeItem.Link ?? "",
                        IsExpanded = false,
                        Margin = new Thickness(0, 4, 0, 0),
                        HeaderIcon = new FontIcon { Glyph = "" },
                    };

                    foreach (var ep in endpoints)
                    {
                        if (string.IsNullOrWhiteSpace(ep.Ip))
                            continue;

                        var epItem = new EndpointItem
                        {
                            Id = ep.Id,
                            Ip = ep.Ip.Trim(),
                            Proxy = ep.Proxy,
                            IsEnabled = ep.Enabled,
                            IsAvailable = ep.IsAvailable,
                            ParentTaskId = probeItem.TaskId,
                            LatencyMs = ep.BestLatencyMs,
                            Speed = ep.SpeedTest?.SpeedBps,
                        };

                        var card = new SettingsCard
                        {
                            Header = epItem.Ip,
                            Description = epItem.Description,
                            HeaderIcon = new FontIcon { Glyph = "" },
                            IsClickEnabled = false,
                        };

                        var toggle = new ToggleSwitch
                        {
                            IsOn = epItem.IsEnabled,
                        };

                        // Toggle 切换 → 尝试调用 API 启用/禁用端点，失败不阻塞 UI
                        toggle.Toggled += async (s, e) =>
                        {
                            if (_suppressToggle) return;

                            var taskId = probeItem.TaskId;
                            var ip = epItem.Ip;
                            var newState = toggle.IsOn;
                            epItem.IsEnabled = newState;

                            // 回写 probe 中存储的 EndpointInfo，防止重新加载时丢失
                            if (_parent.DownloadManager.TryGetProbeResult(taskId, out var probe))
                            {
                                var epInfo = probe.Endpoints?.FirstOrDefault(i => i.Ip?.Trim() == ip);
                                if (epInfo != null)
                                    epInfo.Enabled = newState;
                            }

                            try
                            {
                                if (newState)
                                    await _parent.DownloadManager.EnableEndpointAsync(taskId, ip);
                                else
                                    await _parent.DownloadManager.DisableEndpointAsync(taskId, ip);
                            }
                            catch
                            {
                                // 任务尚未启动（Class 1 probe 阶段）API 不可用，
                                // 仅更新本地状态，实际端点状态在下载启动时生效
                            }
                        };

                        card.Content = toggle;
                        expander.Items.Add(card);
                    }

                    EndpointStackPanel.Children.Add(expander);
                }
            }
            finally
            {
                _suppressToggle = false;
            }
        }

        /// <summary>
        /// 返回按钮 → 回到下载设置页。
        /// </summary>
        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            _parent.NowNewTaskStep = MainWindow.NewTaskStep.DownloadSettings;
        }

        /// <summary>
        /// 确认按钮 → 启动下载并跳转到下载页。
        /// </summary>
        private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            await _parent.StartDownloadsAsync();
        }
    }
}
