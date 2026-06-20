using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ContentDialog = iNKORE.UI.WPF.Modern.Controls.ContentDialog;
using ContentDialogButton = iNKORE.UI.WPF.Modern.Controls.ContentDialogButton;
using ContentDialogResult = iNKORE.UI.WPF.Modern.Controls.ContentDialogResult;

namespace RDownloaderGUI
{
    /// <summary>
    /// NewListPage.xaml 的交互逻辑
    /// </summary>
    public partial class NewListPage : Page
    {
        private MainWindow parent;
        private bool _isReprobing;
        private System.Windows.Threading.DispatcherTimer _fingerprintCheckTimer;
        private string _baselineFingerprint; // 解析开始时的 Headers 指纹快照

        public NewListPage(MainWindow parent)
        {
            this.parent = parent;
            InitializeComponent();

            // 绑定到 MainWindow 的探测结果集合
            ProbeItemsControl.ItemsSource = parent.ProbeResults;

            // 全局下载路径变化时，传播到所有子项以更新显示
            GlobalSettings.SettingsChanged += () =>
            {
                Dispatcher.BeginInvoke(new Action(PropagateGlobalDownloadPath));
            };

            // 定时检查指纹变化，有变化则显示「重新解析」链接
            _fingerprintCheckTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _fingerprintCheckTimer.Tick += (s, e) =>
            {
                // 进度文字可见时（后台解析中或重解析完成后），检测到设置变化即显示链接
                if (ReprobeProgressText.Visibility == Visibility.Visible)
                    ReparseHyperlinkButton.Visibility = AnySettingsChanged() ? Visibility.Visible: Visibility.Collapsed;
            };
        }

        /// <summary>
        /// 全局下载设置控件（供 MainWindow 读取有效设置用于重新解析）。
        /// </summary>
        internal DownloadSettings GlobalSettingsControl => GlobalSettings;

        /// <summary>
        /// 从全局配置文件刷新 Headers / Proxy / DNS 到控件中。
        /// 由 NewPage 的"解析"按钮触发。
        /// </summary>
        public void RefreshConfig()
        {
            try
            {
                var config = GuiConfig.Load();
                ApplyConfig(config);
            }
            catch
            {
                // 刷新失败静默忽略
            }
        }

        /// <summary>
        /// 将指定配置应用到全局下载设置，以及所有已探测的 NewListItem。
        /// </summary>
        public void ApplyConfig(GuiConfig config)
        {
            if (config == null) return;

            // 抑制 SettingsChanged 事件，防止配置加载时误触发「重新解析」链接
            GlobalSettings.SuppressSettingsChanged = true;
            try
            {
                // 全局下载设置
                GlobalSettings.PopulateFromConfig(config);

                // 所有探测结果中的 NewListItem（使用 _pendingConfig 模式，模板渲染后自动生效）
                foreach (var item in parent.ProbeResults)
                {
                    item.PopulateItemSettings(config);
                }
            }
            finally
            {
                GlobalSettings.SuppressSettingsChanged = false;

            }

            PropagateGlobalDownloadPath();
        }

        /// <summary>
        /// 页面被导航到时刷新配置。
        /// </summary>
        public void OnNavigatedTo()
        {
            RefreshConfig();
        }

        /// <summary>
        /// 从 SettingsPage 实时同步 DNS 和代理条目到全局设置及每个子条目（仅条目内容，不同步启用/禁用）。
        /// </summary>
        public void SyncDnsProxyEntries(List<string> dnsAddresses, List<KeyValueItem> proxyEntries)
        {
            // 全局设置
            GlobalSettings.SyncDnsProxyEntries(dnsAddresses, proxyEntries);

            // 每个探测结果的内嵌 DownloadSettings
            foreach (var item in parent.ProbeResults)
            {
                item.SyncDnsProxyEntries(dnsAddresses, proxyEntries);
            }
        }

        // ── 后台解析进度 ──────────────────────────────

        /// <summary>
        /// 设置底部进度文字的可见性。
        /// </summary>
        private bool _globalSettingsWired;

        public void SetProgressVisible(bool visible)
        {
            var vis = visible ? Visibility.Visible : Visibility.Collapsed;
            ReprobeProgressText.Visibility = vis;
            if (visible)
            {
                // 首次显示进度时订阅全局设置变化（此时 config 已完全加载到 UI）
                if (!_globalSettingsWired)
                {
                    _globalSettingsWired = true;

                    // 全局 Headers 变化 → 立即显示
                    GlobalSettings.SettingsChanged += () =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (ReprobeProgressText.Visibility == Visibility.Visible)
                                ReparseHyperlinkButton.Visibility = Visibility.Visible;
                        }));
                    };

                    // 全局 DNS ListView 选中变化 → 立即显示
                    GlobalSettings.DnsItems.CollectionChanged += (s, e) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (ReprobeProgressText.Visibility == Visibility.Visible)
                                ReparseHyperlinkButton.Visibility = Visibility.Visible;
                        }));
                    };

                    // 全局 Proxy 各条目 IsSelected 变化 → 立即显示
                    void OnProxyItemChanged(object s, System.ComponentModel.PropertyChangedEventArgs e)
                    {
                        if (e.PropertyName == nameof(KeyValueItem.IsSelected))
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (ReprobeProgressText.Visibility == Visibility.Visible)
                                    ReparseHyperlinkButton.Visibility = Visibility.Visible;
                            }));
                        }
                    }
                    foreach (var item in GlobalSettings.ProxyItems)
                        item.PropertyChanged += OnProxyItemChanged;
                    GlobalSettings.ProxyItems.CollectionChanged += (s, e) =>
                    {
                        if (e.NewItems != null)
                            foreach (KeyValueItem item in e.NewItems)
                                item.PropertyChanged += OnProxyItemChanged;
                    };
                }

                // 基线截取延迟到条目模板渲染完成后，
                // 由 CaptureBaselineFingerprint() 显式调用
            }
            else
            {
                _fingerprintCheckTimer.Stop();
                // 进度隐藏后，根据设置是否变化决定是否显示「重新解析」链接
                ReparseHyperlinkButton.Visibility = AnySettingsChanged() ? Visibility.Visible : Visibility.Collapsed;
                // 清空基线，确保下次进入时重新截取
                _baselineFingerprint = null;
            }
        }

        /// <summary>
        /// 截取当前条目配置的指纹作为基线，并启动定时检查。
        /// 必须在条目模板渲染完成后调用，否则指纹为不完整状态。
        /// </summary>
        public void CaptureBaselineFingerprint()
        {
            _baselineFingerprint = ComputeCurrentFingerprint();
            _fingerprintCheckTimer.Start();
        }

        /// <summary>
        /// 设置底部进度文字内容。
        /// </summary>
        public void SetProgressText(string text)
        {
            ReprobeProgressText.Text = text;
        }

        /// <summary>
        /// 将全局 DownloadSettings 的有效下载路径传播到所有子项的 DownloadSettings，
        /// 使子项在路径为空时能显示正确的回退路径。
        /// </summary>
        private void PropagateGlobalDownloadPath()
        {
            var globalPath = GlobalSettings.GetEffectiveDownloadDir();
            foreach (var item in parent.ProbeResults)
            {
                item.SetGlobalDownloadPath(globalPath);
            }
        }

        // ── 按钮事件 ────────────────────────────────────

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            // 先取消后台解析，避免返回后仍在运行
            parent.CancelBackgroundParse();
            SetProgressVisible(false);
            parent.ClearProbeResults();
            parent.NowNewTaskStep = MainWindow.NewTaskStep.InputURLs;
        }

        private async void EndpointButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isReprobing) return;
            if (parent.ProbeResults.Count == 0)
            {
                await ShowMessageAsync("提示", "没有可用的文件列表。");
                return;
            }

            // 如果后台解析仍在运行
            if (parent.IsBackgroundParseRunning)
            {
                // 检查设置是否有变化
                bool settingsChanged = AnySettingsChanged();
                if (settingsChanged)
                {
                    // 终止当前解析，重新解析已变化的条目
                    CaptureBaselineFingerprint();
                    try
                    {
                        await ReprobeWithProgressAsync("正在重新解析");
                        // 重新解析完成后跳转到端点选择页
                        await parent.NavigateToEndpointSelectsAsync();
                    }
                    catch (OperationCanceledException)
                    {
                        // 取消 → 留在当前页面
                    }
                }
                else
                {
                    // 设置未变化 → 提示用户等待
                    await ShowMessageAsync("提示", "正在解析中，请稍后再试。");
                }
                return;
            }

            // 后台解析已完成 → 原有流程
            await ReprobeAndThenAsync("配置节点", async () =>
            {
                await parent.NavigateToEndpointSelectsAsync();
            });
        }

        private async void StartDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isReprobing) return;
            if (parent.ProbeResults.Count == 0)
            {
                await ShowMessageAsync("提示", "没有可用的文件列表。");
                return;
            }

            // 如果后台解析仍在运行 → 终止后由 StartDownloadsInBackground 自行处理重解析
            if (parent.IsBackgroundParseRunning)
            {
                parent.CancelBackgroundParse();
            }

            // 立即跳转到下载页，后台重新解析并开始下载
            parent.StartDownloadsInBackground();
        }

        /// <summary>
        /// 重新解析所有 item，成功后执行后续动作。
        /// </summary>
        private async System.Threading.Tasks.Task ReprobeAndThenAsync(string actionName, Func<System.Threading.Tasks.Task> onSuccess)
        {
            if (_isReprobing) return;
            if (parent.ProbeResults.Count == 0)
            {
                await ShowMessageAsync("提示", "没有可用的文件列表。");
                return;
            }

            // 先等待后台解析完成（由「高级选项」触发的解析可能还在运行）
            await parent.WaitForBackgroundParseAsync();
            _isReprobing = true;
            EndpointButton.IsEnabled = false;
            StartDownloadButton.IsEnabled = false;
            ReprobeProgressText.Visibility = Visibility.Visible;
            ReprobeProgressText.Text = $"正在重新解析... (0/{parent.ProbeResults.Count})";
            CaptureBaselineFingerprint();

            var progress = new Progress<(int done, int total)>(p =>
            {
                ReprobeProgressText.Text = $"正在重新解析... ({p.done}/{p.total})";
            });

            List<string> failedUrls;
            try
            {
                failedUrls = await parent.ReprobeAllAsync(progress);
            }
            catch (OperationCanceledException)
            {
                // 取消 → 恢复 UI，不进入后续步骤
                _isReprobing = false;
                EndpointButton.IsEnabled = true;
                StartDownloadButton.IsEnabled = true;
                SetProgressVisible(false);
                return;
            }

            // 恢复 UI
            _isReprobing = false;
            EndpointButton.IsEnabled = true;
            StartDownloadButton.IsEnabled = true;
            SetProgressVisible(false);

            if (failedUrls.Count == 0)
            {
                // 全部成功（或无变化跳过）→ 执行后续动作
                await onSuccess();
            }
            else if (failedUrls.Count == parent.ProbeResults.Count)
            {
                // 全部失败
                await ShowMessageAsync("解析失败", "所有链接重新解析均失败，请检查设置后重试。");
            }
            else
            {
                // 部分成功
                var failedText = string.Join("\n", failedUrls);
                var dialog = new ContentDialog
                {
                    Title = $"部分链接解析失败（{failedUrls.Count}/{parent.ProbeResults.Count}）",
                    Content = $"以下链接解析失败：\n\n{failedText}\n\n{parent.ProbeResults.Count - failedUrls.Count} 个链接解析成功。",
                    PrimaryButtonText = "继续",
                    SecondaryButtonText = "重试",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary
                };

                var result = await dialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    // 继续 → 执行后续动作（仅成功的）
                    await onSuccess();
                }
                else if (result == ContentDialogResult.Secondary)
                {
                    // 重试 → 留在当前页面，用户调整设置后重新点击按钮
                }
                // 取消 → 留在当前页面
            }
        }

        private async System.Threading.Tasks.Task ShowMessageAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                PrimaryButtonText = "确定",
                DefaultButton = ContentDialogButton.Primary
            };
            await dialog.ShowAsync();
        }

        /// <summary>
        /// HyperlinkButton 点击：终止当前解析并以进度条重新解析已变化的条目。
        /// </summary>
        private async void ReparseHyperlinkButton_Click(object sender, RoutedEventArgs e)
        {
            if (!AnySettingsChanged()) return;
            CaptureBaselineFingerprint();

            try
            {
                await ReprobeWithProgressAsync("正在重新解析");
            }
            catch (OperationCanceledException)
            {
                // 取消 → 留在当前页面
            }
        }

        /// <summary>
        /// 终止后台解析并带进度条重新解析已变化的条目。
        /// </summary>
        private async System.Threading.Tasks.Task ReprobeWithProgressAsync(string prefix)
        {
            _isReprobing = true;
            EndpointButton.IsEnabled = false;
            StartDownloadButton.IsEnabled = false;
            ReprobeProgressText.Visibility = Visibility.Visible;
            ReparseHyperlinkButton.Visibility = Visibility.Collapsed; // 开始重解析先隐藏，等进度回调再显示
            ReprobeProgressText.Text = $"{prefix}... (0/{parent.ProbeResults.Count})";

            var progress = new Progress<(int done, int total)>(p =>
            {
                ReprobeProgressText.Text = $"{prefix}... ({p.done}/{p.total})";
            });

            try
            {
                await parent.CancelParseAndReprobeAsync(progress);
            }
            catch (OperationCanceledException)
            {
                // 取消 → 重新抛出，让调用方决定是否继续后续步骤
                throw;
            }
            catch (Exception ex)
            {
                ReprobeProgressText.Text = $"重新解析出错：{ex.Message}";
                return;
            }
            finally
            {
                _isReprobing = false;
                EndpointButton.IsEnabled = true;
                StartDownloadButton.IsEnabled = true;
            }

            // 重新解析完成 → 重启 timer（确保干净周期，避免残留 tick）
            ReparseHyperlinkButton.Visibility = AnySettingsChanged() ? Visibility.Visible : Visibility.Collapsed;
            _fingerprintCheckTimer.Stop();
            _fingerprintCheckTimer.Start();
        }

        /// <summary>
        /// 检查当前所有条目的 Headers 指纹是否与基线不同。
        /// </summary>
        private bool AnySettingsChanged()
        {
            if (_baselineFingerprint == null) return false;
            return ComputeCurrentFingerprint() != _baselineFingerprint;
        }

        /// <summary>
        /// 拼接所有条目的完整有效配置指纹（Headers + Proxy + DNS + UseGlobal 标志）。
        /// 基线在 UI 完全加载后截取，此时 ListView 选中态已还原，Proxy/DNS 指纹稳定。
        /// </summary>
        private string ComputeCurrentFingerprint()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var item in parent.ProbeResults)
            {
                sb.Append(item.UseGlobalSettings ? 'G' : 'L');
                sb.Append('|');

                var headers = item.UseGlobalSettings
                    ? GlobalSettings.GetEffectiveHeaders()
                    : item.GetEffectiveHeaders();
                var proxy = item.UseGlobalSettings
                    ? GlobalSettings.GetEffectiveProxy()
                    : item.GetEffectiveProxy();
                var dns = item.UseGlobalSettings
                    ? GlobalSettings.GetEffectiveDnsServers()
                    : item.GetEffectiveDnsServers();

                sb.Append(GuiConfig.ComputeFingerprint(headers, proxy, dns));
                sb.Append('|');
            }
            return sb.ToString();
        }
    }
}
