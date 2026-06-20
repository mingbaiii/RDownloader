using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Clipboard = System.Windows.Clipboard;
using ContentDialog = iNKORE.UI.WPF.Modern.Controls.ContentDialog;
using ContentDialogButton = iNKORE.UI.WPF.Modern.Controls.ContentDialogButton;
using ContentDialogResult = iNKORE.UI.WPF.Modern.Controls.ContentDialogResult;
using Flyout = iNKORE.UI.WPF.Modern.Controls.Flyout;
using ToggleSwitch = iNKORE.UI.WPF.Modern.Controls.ToggleSwitch;

namespace RDownloaderGUI
{
    /// <summary>
    /// DownloadPage.xaml 的交互逻辑
    /// </summary>
    public partial class DownloadPage : Page
    {
        private MainWindow _parent;
        private ObservableCollection<TaskRecord> TaskRecords { get; } = new ObservableCollection<TaskRecord>();

        // Flyout management: per-task ViewModel + refresh timer
        private readonly Dictionary<string, ConnectionDetailsViewModel> _flyoutViewModels
            = new Dictionary<string, ConnectionDetailsViewModel>();
        private readonly Dictionary<string, DispatcherTimer> _flyoutTimers
            = new Dictionary<string, DispatcherTimer>();
        private bool _suppressToggleEvent;

        public DownloadPage(MainWindow parent)
        {
            _parent = parent;
            InitializeComponent();

            TaskListView.ItemsSource = TaskRecords;

            // 订阅任务状态变化事件
            _parent.DownloadManager.TaskStatusChanged += OnTaskStatusChanged;

            LoadTaskList();
        }

        /// <summary>
        /// 从磁盘加载所有任务记录并显示。
        /// </summary>
        public void RefreshTaskList()
        {
            LoadTaskList();
        }

        private void LoadTaskList()
        {
            var records = TaskRecordManager.ListAll();

            // 应用重启后，所有 Running 的任务实际上进程已退出，应改为 Paused
            foreach (var r in records)
            {
                if (r.Status == "Running")
                {
                    r.Status = "Paused";
                    TaskRecordManager.Save(r);
                }
            }

            TaskRecords.Clear();
            foreach (var r in records)
                TaskRecords.Add(r);

            UpdateVisibility();
        }

        /// <summary>
        /// 实时更新：任务状态变化时刷新对应记录。
        /// </summary>
        private void OnTaskStatusChanged(object sender, TaskStatusChangedEventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                // 从磁盘重新加载该任务的最新记录
                var updated = TaskRecordManager.Load(e.TaskId);
                if (updated == null) return;

                var existing = TaskRecords.FirstOrDefault(r => r.TaskId == e.TaskId);
                if (existing != null)
                {
                    var idx = TaskRecords.IndexOf(existing);
                    TaskRecords[idx] = updated;
                }
                else
                {
                    TaskRecords.Insert(0, updated);
                }

                UpdateVisibility();
            });
        }

        private void UpdateVisibility()
        {
            var hasItems = TaskRecords.Count > 0;
            EmptyPlaceholder.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
            TaskListView.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── 按钮事件 ────────────────────────────────

        private async void PauseResume_Click(object sender, RoutedEventArgs e)
        {
            var record = GetRecordFromSender(sender);
            if (record == null) return;

            try
            {
                if (record.Status == "Running")
                {
                    if (_parent.DownloadManager.HasTask(record.TaskId))
                    {
                        await _parent.DownloadManager.PauseAsync(record.TaskId);
                    }
                    else
                    {
                        // 重启后进程已退出，直接标记为 Paused
                        TaskRecordManager.UpdateStatus(record.TaskId, "Paused", 0, 0, 0);
                    }
                }
                else if (record.Status == "Paused")
                {
                    // 重启后任务可能未注册 → 从 TaskRecord 重新注册
                    if (!_parent.DownloadManager.HasTask(record.TaskId))
                    {
                        _parent.DownloadManager.RegisterPersistedTask(
                            record.TaskId, record.FileName, record.DownloadDir, record.ConfigPath);
                    }
                    // 暂停时进程已退出，必须先 AttachAsync 重新启动进程
                    await _parent.DownloadManager.AttachAsync(record.TaskId);
                    await _parent.DownloadManager.ResumeAsync(record.TaskId);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"PauseResume [{record.TaskId}] 失败: {ex.Message}");
            }
        }

        private async void Cancel_Click(object sender, RoutedEventArgs e)
        {
            var record = GetRecordFromSender(sender);
            if (record == null) return;

            if (record.Status == "Parsing")
            {
                // Parsing 任务：直接结束 rdown.exe 进程
                var confirmed = await ShowConfirmDialogAsync("取消解析", "确定要取消此解析任务吗？");
                if (!confirmed) return;

                _parent.CancelParsingTask(record.TaskId);
                TaskRecordManager.Delete(record.TaskId);
                TaskRecords.Remove(record);
                UpdateVisibility();
                return;
            }

            var confirmed2 = await ShowConfirmDialogAsync("取消任务", "确定要取消此任务吗？\n已下载的进度将丢失。");
            if (!confirmed2) return;

            try
            {
                if (_parent.DownloadManager.HasTask(record.TaskId))
                {
                    await _parent.DownloadManager.CancelAsync(record.TaskId);
                }
                else
                {
                    // 重启后任务不在内存，直接清理
                    TaskRecordManager.CleanupStaleTask(record.TaskId, deleteResidual: true);
                    TaskRecords.Remove(record);
                    UpdateVisibility();
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"Cancel [{record.TaskId}] 失败: {ex.Message}");
            }
        }

        // ── 详情 Flyout ──────────────────────────────

        private async void Details_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button)) return;
            if (!(button.DataContext is TaskRecord record)) return;

            var taskId = record.TaskId;
            if (string.IsNullOrEmpty(taskId)) return;

            // Guard: if Flyout already open for this task, don't open a second one
            if (_flyoutViewModels.ContainsKey(taskId))
                return;

            var isRunning = record.Status == "Running" || record.Status == "Parsing";

            var vm = new ConnectionDetailsViewModel
            {
                TaskId = taskId,
                FileName = record.FileName ?? ""
            };
            if (!isRunning)
            {
                vm.NoDataMessage = "任务未运行";
            }
            _flyoutViewModels[taskId] = vm;

            // Load initial data.
            // ExecuteCommandAsync uses stdin when process is running,
            // and direct one-shot rdown.exe when process has exited.
            await RefreshFlyoutDataAsync(taskId, vm);

            // Build content AFTER data is loaded so the initial render has real values.
            var content = BuildFlyoutContent(vm);

            // Force Measure/Arrange so all bindings resolve BEFORE the Flyout becomes visible.
            // This eliminates the "empty first frame" flicker.
            content.Measure(new Size(480, 380));
            content.Arrange(new Rect(0, 0, 480, 380));

            var flyout = new Flyout
            {
                Placement = iNKORE.UI.WPF.Modern.Controls.Primitives.FlyoutPlacementMode.Bottom,
                Content = content
            };

            flyout.Closed += (s, args) =>
            {
                if (_flyoutTimers.TryGetValue(taskId, out var t))
                {
                    t.Stop();
                    _flyoutTimers.Remove(taskId);
                }
                _flyoutViewModels.Remove(taskId);
            };

            // Suppress ToggleSwitch events during Flyout initialization.
            // iNKORE's ToggleSwitch fires Toggled on ANY IsOn change (including
            // the initial binding push), not only on user interaction.
            _suppressToggleEvent = true;
            flyout.ShowAt(button);

            // Re-enable toggle events once the Flyout is fully loaded.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _suppressToggleEvent = false;
            }));

            // Start auto-refresh timer only for running tasks
            if (isRunning)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                timer.Tick += async (s, args) =>
                {
                    await RefreshFlyoutDataAsync(taskId, vm);
                };
                timer.Start();
                _flyoutTimers[taskId] = timer;
            }
        }

        /// <summary>
        /// Build the Flyout UI content tree and bind it to the given ViewModel.
        /// </summary>
        private FrameworkElement BuildFlyoutContent(ConnectionDetailsViewModel vm)
        {
            var secBrush = (System.Windows.Media.Brush)FindResource(
                iNKORE.UI.WPF.Modern.ThemeKeys.TextFillColorSecondaryBrushKey);
            var terBrush = (System.Windows.Media.Brush)FindResource(
                iNKORE.UI.WPF.Modern.ThemeKeys.TextFillColorTertiaryBrushKey);
            var accentBrush = (System.Windows.Media.Brush)FindResource(
                iNKORE.UI.WPF.Modern.ThemeKeys.AccentTextFillColorPrimaryBrushKey);
            var strokeBrush = (System.Windows.Media.Brush)FindResource(
                iNKORE.UI.WPF.Modern.ThemeKeys.CardStrokeColorDefaultBrushKey);

            // ── Pre-build DataTemplates via XamlReader ──
            // Short tooltip delay + long duration so tooltips feel responsive
            var segTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
                @"<DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                                xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
                                xmlns:local=""clr-namespace:RDownloaderGUI""
                                DataType=""local:SegmentViewModel"">
                    <Border Width=""18"" Height=""18"" CornerRadius=""3"" Margin=""1.5""
                            Background=""{Binding BlockBrush}""
                            Opacity=""{Binding Opacity}""
                            ToolTip=""{Binding TooltipText}""
                            ToolTipService.InitialShowDelay=""200""
                            ToolTipService.BetweenShowDelay=""100""
                            ToolTipService.ShowDuration=""10000""/>
                </DataTemplate>");

            var epTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
                @"<DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                                xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
                                xmlns:ui=""http://schemas.inkore.net/lib/ui/wpf/modern""
                                xmlns:local=""clr-namespace:RDownloaderGUI""
                                DataType=""local:EndpointViewModel"">
                    <Grid Margin=""0,3"">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width=""48""/>
                            <ColumnDefinition Width=""*""/>
                        </Grid.ColumnDefinitions>
                        <ui:ToggleSwitch Grid.Column=""0"" IsOn=""{Binding Enabled, Mode=OneWay}""
                                         Tag=""{Binding Id}"" VerticalAlignment=""Center"" OffContent="""" OnContent=""""/>
                        <StackPanel Grid.Column=""1"" Orientation=""Vertical""
                                    VerticalAlignment=""Center""
                                    ToolTip=""{Binding TooltipText}"">
                            <TextBlock Text=""{Binding Ip}"" FontFamily=""Consolas"" FontSize=""13""
                                       Foreground=""{Binding ColorBrush}"" VerticalAlignment=""Center""/>
                            <TextBlock Text=""{Binding SpeedText}"" FontSize=""11""
                                       Foreground=""{DynamicResource {x:Static ui:ThemeKeys.TextFillColorSecondaryBrushKey}}"">
                                <TextBlock.Style>
                                    <Style TargetType=""TextBlock"">
                                        <Style.Triggers>
                                            <DataTrigger Binding=""{Binding SpeedText}"" Value="""">
                                                <Setter Property=""Visibility"" Value=""Collapsed""/>
                                            </DataTrigger>
                                            <DataTrigger Binding=""{Binding SpeedText}"" Value=""{x:Null}"">
                                                <Setter Property=""Visibility"" Value=""Collapsed""/>
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </TextBlock.Style>
                            </TextBlock>
                        </StackPanel>

                    </Grid>
                </DataTemplate>");

            // Toggled is a routed event (bubbling) — handle at ItemsControl level
            var endpointsItemsControl = new ItemsControl
            {
                ItemTemplate = epTemplate
            };
            endpointsItemsControl.AddHandler(ToggleSwitch.ToggledEvent,
                new RoutedEventHandler(EndpointToggle_Toggled));
            endpointsItemsControl.SetBinding(ItemsControl.ItemsSourceProperty,
                new System.Windows.Data.Binding("Endpoints"));

            var endpointScrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = endpointsItemsControl
            };

            // ── Title row ──────────────────────────────
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            titleRow.Children.Add(new System.Windows.Controls.TextBlock
            { Text = "任务: ", FontSize = 12, Foreground = secBrush });
            var fileNameTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 200
            };
            fileNameTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty,
                new System.Windows.Data.Binding("FileName"));
            titleRow.Children.Add(fileNameTb);
            titleRow.Children.Add(new System.Windows.Controls.TextBlock
            { Text = "  |  ", FontSize = 12, Foreground = terBrush });
            titleRow.Children.Add(new System.Windows.Controls.TextBlock
            { Text = "连接数: ", FontSize = 12, Foreground = secBrush });
            var connTb = new System.Windows.Controls.TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold };
            connTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty,
                new System.Windows.Data.Binding("Connections"));
            titleRow.Children.Add(connTb);
            titleRow.Children.Add(new System.Windows.Controls.TextBlock
            { Text = "  |  ", FontSize = 12, Foreground = terBrush });
            var speedTb = new System.Windows.Controls.TextBlock { FontSize = 12, Foreground = accentBrush };
            speedTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty,
                new System.Windows.Data.Binding("TotalSpeedText"));
            titleRow.Children.Add(speedTb);
            titleRow.Children.Add(new System.Windows.Controls.TextBlock { Text = "  ", FontSize = 12 });
            var progTb = new System.Windows.Controls.TextBlock { FontSize = 12, Foreground = secBrush };
            progTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty,
                new System.Windows.Data.Binding("TotalProgressText"));
            titleRow.Children.Add(progTb);

            // ── Left panel: segment blocks ────────────
            var segmentsItemsControl = new ItemsControl { ItemTemplate = segTemplate };
            segmentsItemsControl.SetBinding(ItemsControl.ItemsSourceProperty,
                new System.Windows.Data.Binding("Segments"));
            segmentsItemsControl.ItemsPanel = new ItemsPanelTemplate(
                new FrameworkElementFactory(typeof(WrapPanel)));

            var segmentScrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = segmentsItemsControl
            };

            // Placeholder shown when no segment data is available
            var noDataPlaceholder = new System.Windows.Controls.TextBlock
            {
                FontSize = 13,
                Foreground = secBrush,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new Thickness(8, 20, 8, 0)
            };
            noDataPlaceholder.SetBinding(System.Windows.Controls.TextBlock.TextProperty,
                new System.Windows.Data.Binding("NoDataMessage"));

            var leftGrid = new Grid();
            leftGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            leftGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            leftGrid.Children.Add(new System.Windows.Controls.TextBlock
            { Text = "连接", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            Grid.SetRow(segmentScrollViewer, 1);
            leftGrid.Children.Add(segmentScrollViewer);
            Grid.SetRow(noDataPlaceholder, 1);
            leftGrid.Children.Add(noDataPlaceholder);

            // ── Right panel: endpoints ─────────────────
            var rightGrid = new Grid();
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rightGrid.Children.Add(new System.Windows.Controls.TextBlock
            { Text = "节点", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            Grid.SetRow(endpointScrollViewer, 1);
            rightGrid.Children.Add(endpointScrollViewer);

            // ── Body: left | separator | right ─────────
            var bodyGrid = new Grid();
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,GridUnitType.Star) });
            Grid.SetColumn(leftGrid, 0);
            bodyGrid.Children.Add(leftGrid);
            var sep = new Border { Width = 1, Margin = new Thickness(8, 4, 8, 4), Background = strokeBrush };
            Grid.SetColumn(sep, 1);
            bodyGrid.Children.Add(sep);
            Grid.SetColumn(rightGrid, 2);
            bodyGrid.Children.Add(rightGrid);

            // ── Root grid ──────────────────────────────
            var root = new Grid { Width = 380, Height = 380, DataContext = vm };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(titleRow, 0);
            root.Children.Add(titleRow);
            Grid.SetRow(bodyGrid, 1);
            root.Children.Add(bodyGrid);

            return root;
        }

        private async Task RefreshFlyoutDataAsync(string taskId, ConnectionDetailsViewModel vm)
        {
            try
            {
                var fullInfo = _parent.DownloadManager.GetLastFullInfo(taskId);
                if (fullInfo == null)
                {
                    // InfoAllAsync uses stdin when process is running,
                    // and direct one-shot rdown.exe when process has exited.
                    try { fullInfo = await _parent.DownloadManager.InfoAllAsync(taskId); }
                    catch { /* leave fullInfo null */ }
                }

                if (fullInfo != null)
                {
                    _suppressToggleEvent = true;
                    vm.UpdateFrom(fullInfo);
                    _suppressToggleEvent = false;
                }
            }
            catch { }
        }

        private async void EndpointToggle_Toggled(object sender, RoutedEventArgs e)
        {
            // Suppress spurious Toggled events: during polling updates (_suppressToggleEvent),
            // OR while a previous toggle API call is still in flight for this endpoint.
            if (_suppressToggleEvent) return;

            // Toggled is a routed event handled at ItemsControl level — the
            // original source is the ToggleSwitch, not the ItemsControl.
            if (!(e.OriginalSource is ToggleSwitch toggle)) return;
            if (!(toggle.DataContext is EndpointViewModel ep)) return;

            if (ep.PendingToggle) return; // API call already in flight for this endpoint

            string taskId = null;
            foreach (var kv in _flyoutViewModels)
            {
                if (kv.Value.Endpoints.Contains(ep))
                {
                    taskId = kv.Key;
                    break;
                }
            }
            if (string.IsNullOrEmpty(taskId)) return;

            // Mark as pending so polling updates don't overwrite while API call is in flight
            ep.PendingToggle = true;

            try
            {
                if (toggle.IsOn)
                    await _parent.DownloadManager.EnableEndpointAsync(taskId, ep.Ip);
                else
                    await _parent.DownloadManager.DisableEndpointAsync(taskId, ep.Ip);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"EndpointToggle [{taskId}] [{ep.Ip}] 失败: {ex.Message}");
                // Revert the toggle on failure
                _suppressToggleEvent = true;
                ep.Enabled = !toggle.IsOn;
                _suppressToggleEvent = false;
            }
            finally
            {
                ep.PendingToggle = false;
            }
        }

        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            var record = GetRecordFromSender(sender);
            if (record == null) return;

            var taskId = record.TaskId;
            var isParsing = record.Status == "Parsing";
            var wasRunning = record.Status == "Running" || record.Status == "Paused";

            if (isParsing)
            {
                // Parsing 任务：直接结束 rdown.exe 进程，然后删除记录
                var confirmed = await ShowConfirmDialogAsync("删除任务", "确定要删除此解析任务吗？\n解析进程将被终止。");
                if (!confirmed) return;

                _parent.CancelParsingTask(taskId);
                TaskRecordManager.Delete(taskId);

                var toRemove = TaskRecords.FirstOrDefault(r => r.TaskId == taskId);
                if (toRemove != null)
                    TaskRecords.Remove(toRemove);

                UpdateVisibility();
                return;
            }

            var message = wasRunning
                ? "此任务正在下载中，将先被取消然后删除。\n已下载的文件将被删除。"
                : "确定要删除此任务记录吗？\n已下载的文件将被删除。";

            var confirmed2 = await ShowConfirmDialogAsync("删除任务", message);
            if (!confirmed2) return;

            try
            {
                // 如果任务正在运行，先取消再删除
                if (wasRunning)
                {
                    try
                    {
                        await _parent.DownloadManager.CancelAsync(taskId);
                        // CancelAsync 会触发 FireStatusChanged → OnTaskStatusChanged
                        // 替换集合中的对象，所以不能继续用旧的 record 引用
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Error($"Delete先取消 [{taskId}] 失败: {ex.Message}");
                    }
                }

                // 删除持久化记录和临时配置
                TaskRecordManager.Delete(taskId);

                // 用 TaskId 重新查找（原对象可能已被 OnTaskStatusChanged 替换）
                var toRemove2 = TaskRecords.FirstOrDefault(r => r.TaskId == taskId);
                if (toRemove2 != null)
                    TaskRecords.Remove(toRemove2);

                UpdateVisibility();
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"Delete [{taskId}] 失败: {ex.Message}");
            }
        }

        // ── 剪贴板辅助（Win32 兜底） ─────────────────

        // Win32 clipboard API – 当 OLE/WPF 层被锁死时作为兜底
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr hMem);

        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;

        /// <summary>通过 Win32 API 直接写入剪贴板（绕过 OLE 层）。</summary>
        private static bool TrySetClipboardViaWin32(string text)
        {
            if (!OpenClipboard(IntPtr.Zero))
                return false;

            try
            {
                if (!EmptyClipboard())
                    return false;

                int byteCount = (text.Length + 1) * sizeof(char);
                IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)byteCount);
                if (hGlobal == IntPtr.Zero)
                    return false;

                IntPtr pText = GlobalLock(hGlobal);
                if (pText == IntPtr.Zero)
                    return false;

                try
                {
                    Marshal.Copy(text.ToCharArray(), 0, pText, text.Length);
                    Marshal.WriteInt16(pText + text.Length * sizeof(char), 0); // null terminator
                }
                finally
                {
                    GlobalUnlock(hGlobal);
                }

                return SetClipboardData(CF_UNICODETEXT, hGlobal) != IntPtr.Zero;
            }
            finally
            {
                CloseClipboard();
            }
        }

        // ── 右键菜单事件 ─────────────────────────────

        /// <summary>
        /// WPF ContextMenu 在数据绑定 Visibility 变化后可能不会自动重测布局，
        /// 导致已 Collapsed 的项残留空白区域。在 Loaded 时强制刷新布局。
        /// </summary>
        private void ContextMenu_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ContextMenu cm)
            {
                cm.InvalidateMeasure();
                cm.InvalidateArrange();
            }
        }

        private static void SetClipboardText(string text)
        {
            // 1) 确保在 UI 线程（STA）
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(() => SetClipboardText(text));
                return;
            }
            TrySetClipboardViaWin32(text);
        }

        private async void CopyLink_Click(object sender, RoutedEventArgs e)
        {
            var record = GetRecordFromSender(sender);
            if (record == null) return;
            try
            {
                SetClipboardText(record.Url);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"CopyLink [{record.TaskId}] 失败: {ex.Message}");
                await ShowMessageAsync("复制失败", "复制链接失败，请稍后重试。");
            }
        }

        private async void CopyFileName_Click(object sender, RoutedEventArgs e)
        {
            var record = GetRecordFromSender(sender);
            if (record == null) return;
            try
            {
                SetClipboardText(record.FileName);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"CopyFileName [{record.TaskId}] 失败: {ex.Message}");
                await ShowMessageAsync("复制失败", "复制文件名失败，请稍后重试。");
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var record = GetRecordFromSender(sender);
            if (record == null) return;

            try
            {
                var path = !string.IsNullOrEmpty(record.TaskJsonPath) && File.Exists(record.TaskJsonPath)
                    ? record.TaskJsonPath
                    : record.DownloadDir;

                if (Directory.Exists(path))
                {
                    Process.Start("explorer.exe", $"\"{path}\"");
                }
                else if (File.Exists(path))
                {
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
                }
                else if (Directory.Exists(record.DownloadDir))
                {
                    Process.Start("explorer.exe", $"\"{record.DownloadDir}\"");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"OpenFolder [{record.TaskId}] 失败: {ex.Message}");
            }
        }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            var record = GetRecordFromSender(sender);
            if (record == null) return;

            try
            {
                var filePath = Path.Combine(record.DownloadDir, record.FileName);
                if (File.Exists(filePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = filePath,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"OpenFile [{record.TaskId}] 失败: {ex.Message}");
            }
        }

        private async void CtxPauseResume_Click(object sender, RoutedEventArgs e)
        {
            var record = GetRecordFromSender(sender);
            if (record == null) return;

            try
            {
                if (record.Status == "Running")
                {
                    if (_parent.DownloadManager.HasTask(record.TaskId))
                    {
                        await _parent.DownloadManager.PauseAsync(record.TaskId);
                    }
                    else
                    {
                        // 重启后进程已退出，直接标记为 Paused
                        TaskRecordManager.UpdateStatus(record.TaskId, "Paused", 0, 0, 0);
                    }
                }
                else if (record.Status == "Paused")
                {
                    if (!_parent.DownloadManager.HasTask(record.TaskId))
                    {
                        _parent.DownloadManager.RegisterPersistedTask(
                            record.TaskId, record.FileName, record.DownloadDir, record.ConfigPath);
                    }
                    await _parent.DownloadManager.AttachAsync(record.TaskId);
                    await _parent.DownloadManager.ResumeAsync(record.TaskId);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"CtxPauseResume [{record.TaskId}] 失败: {ex.Message}");
            }
        }

        private async void CtxCancel_Click(object sender, RoutedEventArgs e)
        {
            var record = GetRecordFromSender(sender);
            if (record == null) return;

            if (record.Status == "Parsing")
            {
                // Parsing 任务：直接结束 rdown.exe 进程
                var confirmed = await ShowConfirmDialogAsync("取消解析", "确定要取消此解析任务吗？");
                if (!confirmed) return;

                _parent.CancelParsingTask(record.TaskId);
                TaskRecordManager.Delete(record.TaskId);
                TaskRecords.Remove(record);
                UpdateVisibility();
                return;
            }

            var confirmed2 = await ShowConfirmDialogAsync("取消任务", "确定要取消此任务吗？\n已下载的进度将丢失。");
            if (!confirmed2) return;

            try
            {
                if (_parent.DownloadManager.HasTask(record.TaskId))
                {
                    await _parent.DownloadManager.CancelAsync(record.TaskId);
                }
                else
                {
                    // 重启后任务不在内存，直接清理
                    TaskRecordManager.CleanupStaleTask(record.TaskId, deleteResidual: true);
                    TaskRecords.Remove(record);
                    UpdateVisibility();
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"CtxCancel [{record.TaskId}] 失败: {ex.Message}");
            }
        }

        private async void CtxDelete_Click(object sender, RoutedEventArgs e)
        {
            var record = GetRecordFromSender(sender);
            if (record == null) return;

            var taskId = record.TaskId;
            var isParsing = record.Status == "Parsing";
            var wasRunning = record.Status == "Running" || record.Status == "Paused";

            if (isParsing)
            {
                // Parsing 任务：直接结束 rdown.exe 进程，然后删除记录
                var confirmed = await ShowConfirmDialogAsync("删除任务", "确定要删除此解析任务吗？\n解析进程将被终止。");
                if (!confirmed) return;

                _parent.CancelParsingTask(taskId);
                TaskRecordManager.Delete(taskId);

                var toRemove = TaskRecords.FirstOrDefault(r => r.TaskId == taskId);
                if (toRemove != null)
                    TaskRecords.Remove(toRemove);

                UpdateVisibility();
                return;
            }

            var message = wasRunning
                ? "此任务正在下载中，将先被取消然后删除。\n已下载的文件将被删除。"
                : "确定要删除此任务记录吗？\n已下载的文件将被删除。";

            var confirmed2 = await ShowConfirmDialogAsync("删除任务", message);
            if (!confirmed2) return;

            try
            {
                if (wasRunning)
                {
                    try
                    {
                        await _parent.DownloadManager.CancelAsync(taskId);
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Error($"CtxDelete先取消 [{taskId}] 失败: {ex.Message}");
                    }
                }

                TaskRecordManager.Delete(taskId);

                var toRemove2 = TaskRecords.FirstOrDefault(r => r.TaskId == taskId);
                if (toRemove2 != null)
                    TaskRecords.Remove(toRemove2);

                UpdateVisibility();
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"CtxDelete [{taskId}] 失败: {ex.Message}");
            }
        }

        // ── 辅助方法 ─────────────────────────────────

        /// <summary>
        /// 从 sender（MenuItem 或 Button）获取对应的 TaskRecord。
        /// </summary>
        private TaskRecord GetRecordFromSender(object sender)
        {
            if (sender is FrameworkElement fe)
            {
                // 直接绑定（按钮的 DataContext）
                if (fe.DataContext is TaskRecord record)
                    return record;

                // ContextMenu 的 PlacementTarget
                if (fe is MenuItem mi && mi.Parent is ContextMenu cm)
                {
                    if (cm.PlacementTarget is FrameworkElement target &&
                        target.DataContext is TaskRecord targetRecord)
                        return targetRecord;
                }
            }
            return null;
        }

        /// <summary>
        /// 显示单按钮提示对话框（iNKORE ContentDialog）。
        /// </summary>
        private static async Task ShowMessageAsync(string title, string message)
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
        /// 显示确认对话框（iNKORE ContentDialog），返回用户是否点击"是"。
        /// </summary>
        private static async Task<bool> ShowConfirmDialogAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                PrimaryButtonText = "是",
                SecondaryButtonText = "否",
                DefaultButton = ContentDialogButton.Secondary
            };
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }
    }

    /// <summary>
    /// 将任务状态字符串转换为背景色画刷。
    /// </summary>
    public class TaskStatusColorConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var status = value as string;
            System.Windows.Media.Color color;
            switch (status)
            {
                case "Parsing":
                    color = System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4);
                    break;
                case "Running":
                    color = System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4);
                    break;
                case "Completed":
                    color = System.Windows.Media.Color.FromRgb(0x10, 0x7C, 0x10);
                    break;
                case "Failed":
                    color = System.Windows.Media.Color.FromRgb(0xD1, 0x3B, 0x2A);
                    break;
                case "Paused":
                    color = System.Windows.Media.Color.FromRgb(0xFF, 0x8C, 0x00);
                    break;
                case "Cancelled":
                default:
                    color = System.Windows.Media.Color.FromRgb(0x79, 0x79, 0x79);
                    break;
            }
            return new System.Windows.Media.SolidColorBrush(color);
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 从源值中减去指定数值，用于动态计算 MaxWidth。
    /// </summary>
    public class MathSubtractConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is double src && double.TryParse(parameter?.ToString(), out double sub))
                return src - sub;
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
