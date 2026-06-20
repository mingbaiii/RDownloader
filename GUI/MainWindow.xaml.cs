using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace RDownloaderGUI
{

    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        public enum NewTaskStep
        {
            InputURLs,
            DownloadSettings,
            EndpointSelects
        }

        public MainWindow()
        {
            InitializeComponent();

            // 数据中心
            _downloadManager = new RDownloaderManager();
            ProbeResults = new ObservableCollection<NewListItem>();

            // ── Aria2 RPC 服务器 ──
            var config = GuiConfig.Load();
            RDownloaderManager.DEBUG_MODE = config.DebugMode;
            _rpcServer = new Aria2RpcServer(_downloadManager, config.RpcPort, config.RpcSecret);
            _rpcServer.OnTaskCreated += taskId =>
            {
                Dispatcher.InvokeAsync(() => _downloadPage.RefreshTaskList());
            };
            _rpcServer.Start();

            this.Closing += (s, e) => _rpcServer.Stop();

            // 页面
            _downloadPage = new DownloadPage(this);
            _settingsPage = new SettingsPage(this);
            _newPage = new NewPage(this);
            _newListPage = new NewListPage(this);
            _selectEndpointPage = new SelectEndpointPage(this);

            // 设置 DataContext 供子页面绑定
            DataContext = this;

            MainView.SelectedItem = DownloadItem;
        }

        /// <summary>
        /// 热重启 RPC 服务器（设置页修改端口/密钥后调用）。
        /// </summary>
        public void RestartRpcServer(int port, string secret)
        {
            _rpcServer.Restart(port, secret);
        }

        /// <summary>
        /// 刷新下载列表（设置页清理文件后调用）。
        /// </summary>
        public void RefreshDownloadPage()
        {
            Dispatcher.InvokeAsync(() => _downloadPage.RefreshTaskList());
        }

        private DownloadPage _downloadPage;
        private SettingsPage _settingsPage;
        private NewPage _newPage;
        private NewListPage _newListPage;
        private SelectEndpointPage _selectEndpointPage;

        private RDownloaderManager _downloadManager;
        private Aria2RpcServer _rpcServer;

        /// <summary>
        /// 后台解析 Task，用于「配置节点」等待解析完成。
        /// </summary>
        private Task _backgroundParseTask = Task.CompletedTask;

        /// <summary>
        /// 后台解析的取消令牌源。
        /// </summary>
        private CancellationTokenSource _parseCts;
        private volatile bool _parseCancelled;

        /// <summary>
        /// 每个 Parsing 任务的取消令牌，key 为临时任务 ID（parsing-{Guid}）。
        /// 用于「取消」/「删除」Parsing 任务时直接结束 rdown.exe 进程。
        /// </summary>
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _parsingCts
            = new ConcurrentDictionary<string, CancellationTokenSource>();

        /// <summary>
        /// 后台解析是否正在运行。
        /// </summary>
        public bool IsBackgroundParseRunning =>
            _backgroundParseTask != null && !_backgroundParseTask.IsCompleted;

        private NewTaskStep nowNewTaskStep = NewTaskStep.InputURLs;
        public NewTaskStep NowNewTaskStep
        {
            get { return nowNewTaskStep; }
            set
            {
                nowNewTaskStep = value;

                if (MainView.SelectedItem != NewItem)
                {
                    return;
                }

                switch (nowNewTaskStep)
                {
                    case NewTaskStep.InputURLs:
                        ContentFrame.Navigate(_newPage);
                        break;
                    case NewTaskStep.DownloadSettings:
                        ContentFrame.Navigate(_newListPage);
                        break;
                    case NewTaskStep.EndpointSelects:
                        ContentFrame.Navigate(_selectEndpointPage);
                        break;
                }
            }
        }

        // ── 后台解析（高级选项） ────────────────────────

        /// <summary>
        /// 「高级选项」：先创建占位条目加入列表，跳转到设置页，再后台静默解析并逐个更新。
        /// </summary>
        public void NavigateToSettingsAndProbeInBackground(List<string> urls)
        {
            // 计算初始配置指纹
            string initialFingerprint = null;
            string tempConfigPath = null;
            try
            {
                var guiConfig = GuiConfig.Load();
                var rdownConfig = guiConfig.ToRdownConfig();
                var dir = Path.Combine(Path.GetTempPath(), "RDownloader", "reprobe", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                tempConfigPath = Path.Combine(dir, "rdown_config.json");
                rdownConfig.Save(tempConfigPath);

                // 仅基于 Headers 计算初始指纹（Proxy/DNS 的 ListView 选中态异步还原）
                initialFingerprint = GuiConfig.ComputeFingerprint(
                    rdownConfig.Headers, null, null);
            }
            catch { }

            // 先创建占位条目，立即加入列表（文件名从 URL 提取，解析后更新）
            foreach (var url in urls)
            {
                var item = new NewListItem
                {
                    TaskId = "",
                    FileName = ExtractFileNameFromUrl(url),
                    Link = url,
                    LastProbeFingerprint = initialFingerprint
                };
                item.DeleteCommand = new RelayCommand(_ => ProbeResults.Remove(item));
                ProbeResults.Add(item);
            }

            // 跳转到设置页
            RefreshNewListPageConfig();
            NowNewTaskStep = NewTaskStep.DownloadSettings;

            // 等页面完全渲染（ListView 选中态还原）后再开始后台解析，确保基线指纹准确
            _parseCancelled = false;
            _parseCts?.Cancel();
            _parseCts?.Dispose();
            _parseCts = new CancellationTokenSource();
            var cts = _parseCts; // 局部捕获，避免后续被覆盖
            var urlsCopy = urls;
            var configPath = tempConfigPath;
            var fp = initialFingerprint;
            RoutedEventHandler startAfterLoad = null;
            startAfterLoad = (s, e) =>
            {
                _newListPage.Loaded -= startAfterLoad;
                if (!cts.IsCancellationRequested)
                    _backgroundParseTask = ProbeUrlsInBackgroundAsync(urlsCopy, configPath, fp, cts.Token);
            };
            _newListPage.Loaded += startAfterLoad;
        }

        private async Task ProbeUrlsInBackgroundAsync(List<string> urls, string tempConfigPath, string initialFingerprint, CancellationToken cancellationToken = default)
        {
            var total = urls.Count;
            var failed = new List<string>();
            var done = 0;

            // 先隐藏进度，等条目模板渲染完成后再显示并截取基线
            _newListPage?.SetProgressVisible(false);

            await Dispatcher.InvokeAsync(() =>
            {
                _newListPage?.SetProgressText($"正在后台解析... (0/{total})");
                _newListPage?.SetProgressVisible(true);
                _newListPage?.CaptureBaselineFingerprint();
            }, System.Windows.Threading.DispatcherPriority.Loaded);

            var tasks = urls.Select(async url =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = await _downloadManager.ProbeAsync(url, tempConfigPath, cancellationToken);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        var item = ProbeResults.FirstOrDefault(i => i.Link == url);
                        if (item != null)
                        {
                            item.TaskId = result.Id;
                            item.FileName = result.Output;
                            item.LastProbeFingerprint = initialFingerprint;
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    // 正常取消，不记录为失败
                }
                catch
                {
                    lock (failed)
                    {
                        failed.Add(url);
                    }
                    // 更新占位条目显示失败状态
                    await Dispatcher.InvokeAsync(() =>
                    {
                        var item = ProbeResults.FirstOrDefault(i => i.Link == url);
                        if (item != null)
                        {
                            item.FileName = "解析失败";
                        }
                    });
                }
                finally
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        var current = System.Threading.Interlocked.Increment(ref done);
                        _newListPage?.SetProgressText($"正在后台解析... ({current}/{total})");
                    }
                }
            }).ToArray();

            await Task.WhenAll(tasks);

            // 用完整指纹更新已成功解析的 item，
            // 避免后续点击「下载」/「配置节点」时因 Proxy/DNS 差异误触发重解析
            await Dispatcher.InvokeAsync(() =>
            {
                foreach (var item in ProbeResults)
                {
                    if (!string.IsNullOrEmpty(item.TaskId) && item.FileName != "解析失败")
                    {
                        GetEffectiveSettings(item, out var headers, out var proxy, out var dnsServers);
                        item.LastProbeFingerprint = GuiConfig.ComputeFingerprint(headers, proxy, dnsServers);
                    }
                }
            });

            // 清理临时 config
            if (tempConfigPath != null)
            {
                try
                {
                    var tempDir = Path.GetDirectoryName(tempConfigPath);
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch { }
            }

            // 已取消时不修改 UI，避免覆盖重新解析的进度显示
            if (!cancellationToken.IsCancellationRequested)
            {
                if (failed.Count > 0 && failed.Count == total)
                {
                    _newListPage?.SetProgressText("解析失败：所有链接均失败，请返回检查 URL 后重试。");
                }
                else
                {
                    _newListPage?.SetProgressVisible(false);
                }
            }
        }

        /// <summary>
        /// 等待后台解析完成。由 NewListPage 的「配置节点」按钮调用。
        /// </summary>
        public async Task WaitForBackgroundParseAsync()
        {
            if (_backgroundParseTask != null && !_backgroundParseTask.IsCompleted)
                await _backgroundParseTask;
        }

        /// <summary>
        /// 终止当前后台解析并重新解析已变化的条目。
        /// 由 HyperlinkButton 点击和配置节点/开始下载（设置已变化时）调用。
        /// </summary>
        public async Task CancelParseAndReprobeAsync(IProgress<(int done, int total)> progress = null)
        {
            // 终止当前解析
            CancelBackgroundParse();

            // 等待后台任务完全结束，防止其完成回调覆盖当前进度显示
            await WaitForBackgroundParseAsync();

            // 等待期间用户可能点了返回，此时应直接终止
            if (_parseCancelled)
            {
                _newListPage?.SetProgressText("重新解析已取消。");
                throw new OperationCanceledException();
            }

            // 创建新的 CTS，使得 reprobe 阶段也可被取消
            _parseCts?.Cancel();
            _parseCts?.Dispose();
            _parseCts = new CancellationTokenSource();
            var reprobeCts = _parseCts;

            // 重新解析已变化的条目
            var failedUrls = await ReprobeAllAsync(progress, reprobeCts.Token);

            if (reprobeCts.IsCancellationRequested)
            {
                _newListPage?.SetProgressText("重新解析已取消。");
                throw new OperationCanceledException(reprobeCts.Token);
            }
            else if (failedUrls.Count > 0)
            {
                _newListPage?.SetProgressText(
                    failedUrls.Count == ProbeResults.Count
                        ? "重新解析失败：所有链接均失败。"
                        : $"{failedUrls.Count} 个链接重新解析失败。");
            }
            else
            {
                _newListPage?.SetProgressText("重新解析完成。");
            }
        }

        /// <summary>
        /// 取消后台解析（发送取消信号并等待 Task 结束）。
        /// </summary>
        public void CancelBackgroundParse()
        {
            if (_parseCts != null && !_parseCts.IsCancellationRequested)
            {
                DebugLogger.Status("CancelBackgroundParse: 正在发送取消信号...");
                _parseCts.Cancel();
                DebugLogger.Status("CancelBackgroundParse: 取消信号已发送");
            }
            else
            {
                DebugLogger.Status($"CancelBackgroundParse: 无需取消 (cts={_parseCts != null}, cancelled={_parseCts?.IsCancellationRequested})");
            }
            // 设置标记，确保 CancelParseAndReprobeAsync 创建新 CTS 后也能感知取消
            _parseCancelled = true;
        }

        /// <summary>
        /// 取消一个 Parsing 任务（直接从下载页触发）。
        /// 取消对应的 CancellationTokenSource，ProbeAsync 会因此 kill rdown.exe 进程。
        /// </summary>
        /// <param name="tempId">Parsing 任务的临时 ID（parsing-{Guid}）</param>
        public void CancelParsingTask(string tempId)
        {
            if (_parsingCts.TryRemove(tempId, out var cts))
            {
                DebugLogger.Status($"CancelParsingTask: [{tempId}] 正在取消 Parsing 任务...");
                try
                {
                    cts.Cancel();
                    DebugLogger.Status($"CancelParsingTask: [{tempId}] 取消信号已发送，rdown.exe 进程将被终止");
                }
                catch (ObjectDisposedException)
                {
                    DebugLogger.Status($"CancelParsingTask: [{tempId}] CTS 已释放");
                }
                finally
                {
                    cts.Dispose();
                }
            }
            else
            {
                DebugLogger.Status($"CancelParsingTask: [{tempId}] 未找到活跃的 Parsing CTS（可能已完成）");
            }
        }

        /// <summary>
        /// 从 URL 路径中提取文件名作为初始显示名称，解析完成后由探测结果覆盖。
        /// </summary>
        private static string ExtractFileNameFromUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                var path = uri.AbsolutePath.TrimEnd('/');
                var lastSegment = path.Split('/').LastOrDefault();
                if (!string.IsNullOrEmpty(lastSegment))
                    return Uri.UnescapeDataString(lastSegment);
            }
            catch { }
            return "解析中...";
        }

        // ── 探测结果集合（ItemsControl 绑定） ──────────

        /// <summary>
        /// 探测结果集合，NewListPage 的 ItemsControl 绑定此集合。
        /// </summary>
        public ObservableCollection<NewListItem> ProbeResults { get; }

        // ── 探测协调 ────────────────────────────────────

        /// <summary>
        /// 对一批 URL 并发执行 ProbeAsync，完成后加入 ProbeResults。
        /// 返回失败的 URL 列表。
        /// </summary>
        /// <param name="urls">要探测的 URL 列表</param>
        /// <param name="progress">完成一个回调：current / total</param>
        /// <returns>探测失败的 URL 列表</returns>
        public async Task<List<string>> ProbeUrlsAsync(IEnumerable<string> urls, IProgress<(int done, int total)> progress = null)
        {
            var urlList = urls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().ToList();
            var total = urlList.Count;
            var failed = new List<string>();
            var done = 0;

            // 首次探测使用 GUI 全局配置 → 生成临时 config + 计算初始指纹
            string initialFingerprint = null;
            string tempConfigPath = null;
            try
            {
                var guiConfig = GuiConfig.Load();
                var rdownConfig = guiConfig.ToRdownConfig();
                var dir = Path.Combine(Path.GetTempPath(), "RDownloader", "reprobe", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                tempConfigPath = Path.Combine(dir, "rdown_config.json");
                rdownConfig.Save(tempConfigPath);

                // 仅基于 Headers 计算初始指纹
                initialFingerprint = GuiConfig.ComputeFingerprint(
                    rdownConfig.Headers, null, null);
            }
            catch { }

            var tasks = urlList.Select(async url =>
            {
                try
                {
                    var result = await _downloadManager.ProbeAsync(url, tempConfigPath);

                    var item = new NewListItem
                    {
                        TaskId = result.Id,
                        FileName = result.Output,
                        Link = url,
                        LastProbeFingerprint = initialFingerprint
                    };
                    item.DeleteCommand = new RelayCommand(_ => ProbeResults.Remove(item));

                    // UI 线程操作集合
                    await Dispatcher.InvokeAsync(() => ProbeResults.Add(item));
                }
                catch (Exception)
                {
                    lock (failed)
                    {
                        failed.Add(url);
                    }
                }
                finally
                {
                    var current = System.Threading.Interlocked.Increment(ref done);
                    progress?.Report((current, total));
                }
            }).ToArray();

            await Task.WhenAll(tasks);

            // 清理初始探测的临时 config
            if (tempConfigPath != null)
            {
                try
                {
                    var tempDir = Path.GetDirectoryName(tempConfigPath);
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch { }
            }

            return failed;
        }

        /// <summary>
        /// 清除所有探测结果。
        /// </summary>
        public void ClearProbeResults()
        {
            ProbeResults.Clear();
            _downloadManager.ClearAllProbes();
        }

        // ── 开始下载 ────────────────────────────────────

        /// <summary>
        /// 将所有已探测的文件转为正式下载任务并启动。
        /// 完成后清空探测结果，重置新建流程，跳转到下载页。
        /// </summary>
        public async System.Threading.Tasks.Task StartDownloadsAsync()
        {
            var items = ProbeResults.ToList();
            DebugLogger.Status($"StartDownloadsAsync: 准备启动 {items.Count} 个下载...");

            var globalDownloadPath = _newListPage?.GlobalSettingsControl?.GetEffectiveDownloadDir();

            foreach (var item in items)
            {
                var taskId = item.TaskId;
                if (string.IsNullOrEmpty(taskId)) continue;

                // 在 MoveToFormalAsync（会移除 probe）之前快照端点配置
                List<EndpointInfo> endpoints = null;
                if (_downloadManager.TryGetProbeResult(taskId, out var probe))
                {
                    endpoints = probe.Endpoints?.ToList();
                }

                var downloadDir = GetDownloadDir(item, globalDownloadPath);

                // 生成临时 rdown_config.json（含 headers/proxy/DNS），rdown 下载时需要这些设置
                var tempConfigPath = SaveConfigForDownload(item, downloadDir);

                try
                {
                    // 提取有效设置（Headers/Proxy/DNS），config 被删除后用于重建
                    GetEffectiveSettings(item,
                        out var headers, out var proxy, out var dnsServers);

                    // 先创建持久化任务记录（确保 MoveToFormalAsync 失败也能在列表中看到 Failed）
                    var record = new TaskRecord
                    {
                        TaskId = taskId,
                        Url = item.Link,
                        FileName = item.FileName,
                        DownloadDir = downloadDir,
                        TaskJsonPath = Path.Combine(downloadDir, $"{item.FileName}.{taskId}.rdown.json"),
                        ConfigPath = tempConfigPath,
                        Status = "Running",
                        CreatedAt = DateTime.Now,
                        Headers = headers,
                        Proxy = proxy,
                        DnsServers = dnsServers,
                        ResponseHeaders = probe?.ResponseHeaders
                    };
                    TaskRecordManager.Save(record);

                    // Step 1: probe → 正式任务（写入 .rdown.json 到下载目录）
                    await _downloadManager.MoveToFormalAsync(taskId, downloadDir, tempConfigPath);
                    DebugLogger.Status($"StartDownloadsAsync: [{taskId}] MoveToFormalAsync 完成 → {downloadDir}");

                    // Step 2: 启动 rdown 进程
                    await _downloadManager.AttachAsync(taskId);
                    DebugLogger.Status($"StartDownloadsAsync: [{taskId}] AttachAsync 完成");

                    // Step 3: 应用用户的端点启用/禁用设置
                    if (endpoints != null)
                    {
                        foreach (var ep in endpoints)
                        {
                            if (string.IsNullOrWhiteSpace(ep.Ip)) continue;
                            try
                            {
                                if (ep.Enabled)
                                    await _downloadManager.EnableEndpointAsync(taskId, ep.Ip);
                                else
                                    await _downloadManager.DisableEndpointAsync(taskId, ep.Ip);
                            }
                            catch (Exception ex)
                            {
                                DebugLogger.Error($"StartDownloadsAsync: [{taskId}] 端点 {ep.Ip} 配置失败: {ex.Message}");
                            }
                        }
                    }

                    // Step 4: 开始下载
                    await _downloadManager.ResumeAsync(taskId);
                    DebugLogger.Status($"StartDownloadsAsync: [{taskId}] ResumeAsync 完成，下载已启动");
                }
                catch (Exception ex)
                {
                    DebugLogger.Error($"StartDownloadsAsync: [{taskId}] 启动失败: {ex.Message}");
                    // 标记 TaskRecord 为 Failed
                    try
                    {
                        TaskRecordManager.UpdateStatus(taskId, "Failed", 0, 0, 0);
                    }
                    catch { }
                }
            }

            // 清空探测状态，重置新建流程，跳转到下载页
            ClearProbeResults();
            NowNewTaskStep = NewTaskStep.InputURLs;
            MainView.SelectedItem = DownloadItem;
        }

        // ── 开始下载（后台模式） ────────────────────────

        /// <summary>
        /// 「开始下载」：立即跳转到下载页，为每个任务创建 Parsing 占位记录，
        /// 后台解析完成后自动转为正式下载。与「直接下载」采用相同方案。
        /// </summary>
        public async void StartDownloadsInBackground()
        {
            var items = ProbeResults.ToList();
            if (items.Count == 0) return;

            // ── UI 线程：捕获每个 item 的有效设置（导航前必须完成）──
            var itemConfigs = new List<(NewListItem item, Dictionary<string, string> headers,
                Dictionary<string, string> proxy, List<string> dnsServers,
                bool needReprobe, string reprobeConfigPath)>();

            foreach (var item in items)
            {
                GetEffectiveSettings(item, out var headers, out var proxy, out var dnsServers);
                var fingerprint = GuiConfig.ComputeFingerprint(headers, proxy, dnsServers);
                // 空 TaskId 说明从未成功解析，必须重新解析
                bool needReprobe = string.IsNullOrEmpty(item.TaskId) || fingerprint != item.LastProbeFingerprint;

                string reprobeConfigPath = null;
                if (needReprobe)
                {
                    try
                    {
                        var config = GuiConfig.Load().ToRdownConfig();
                        config.Headers = headers;
                        config.Proxy = proxy;
                        config.DnsServers = dnsServers;
                        var dir = Path.Combine(Path.GetTempPath(), "RDownloader", "reprobe", Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(dir);
                        reprobeConfigPath = Path.Combine(dir, "rdown_config.json");
                        config.Save(reprobeConfigPath);
                    }
                    catch { needReprobe = false; }
                }

                itemConfigs.Add((item, headers, proxy, dnsServers, needReprobe, reprobeConfigPath));
            }

            // ── 立即跳转到下载页 ──
            await Dispatcher.InvokeAsync(() =>
            {
                if (MainView.SelectedItem != DownloadItem)
                {
                    MainView.SelectedItem = DownloadItem;
                    ContentFrame.Navigate(_downloadPage);
                }
            });

            // ── 后台逐个处理 ──
            // 读取 NewListPage 全局下载路径作为回退
            var globalDownloadPath = _newListPage?.GlobalSettingsControl?.GetEffectiveDownloadDir();
            var downloadDirBase = globalDownloadPath ?? GuiConfig.GetEffectiveDownloadDir();

            foreach (var (item, headers, proxy, dnsServers, needReprobe, reprobeConfigPath) in itemConfigs)
            {
                var tempId = $"parsing-{Guid.NewGuid():N}";
                string taskId = null;

                // --- 创建此任务的取消令牌，用于「取消」/「删除」时直接结束进程 ---
                var parseCts = new CancellationTokenSource();
                _parsingCts[tempId] = parseCts;

                try
                {
                    parseCts.Token.ThrowIfCancellationRequested();

                    // --- Step 0: 创建 Parsing 占位记录，立刻显示在下载页 ---
                    var tempRecord = new TaskRecord
                    {
                        TaskId = tempId,
                        Url = item.Link,
                        FileName = item.FileName,
                        DownloadDir = downloadDirBase,
                        Status = "Parsing",
                        CreatedAt = DateTime.Now
                    };
                    TaskRecordManager.Save(tempRecord);
                    await Dispatcher.InvokeAsync(() => _downloadPage.RefreshTaskList());

                    // --- Step 1: 重新解析（如需要）---
                    if (needReprobe && reprobeConfigPath != null)
                    {
                        try
                        {
                            parseCts.Token.ThrowIfCancellationRequested();
                            var reprobeResult = await _downloadManager.ProbeAsync(item.Link, reprobeConfigPath, parseCts.Token);
                            item.TaskId = reprobeResult.Id;
                            item.FileName = reprobeResult.Output;
                            item.LastProbeFingerprint = GuiConfig.ComputeFingerprint(headers, proxy, dnsServers);

                            // 更新 Parsing 记录的显示名称
                            tempRecord.FileName = reprobeResult.Output;
                            TaskRecordManager.Save(tempRecord);
                            await Dispatcher.InvokeAsync(() => _downloadPage.RefreshTaskList());
                        }
                        finally
                        {
                            try
                            {
                                var tempDir = Path.GetDirectoryName(reprobeConfigPath);
                                if (Directory.Exists(tempDir))
                                    Directory.Delete(tempDir, true);
                            }
                            catch { }
                        }
                    }

                    taskId = item.TaskId;
                    if (string.IsNullOrEmpty(taskId))
                    {
                        // 解析失败 → 标记 Parsing 为 Failed
                        tempRecord.Status = "Failed";
                        TaskRecordManager.Save(tempRecord);
                        await Dispatcher.InvokeAsync(() => _downloadPage.RefreshTaskList());
                        continue;
                    }

                    // 确定下载目录（使用解析后的文件名）
                    var downloadDir = GetDownloadDir(item, globalDownloadPath);

                    // Snapshot endpoints（MoveToFormal 会移除 probe）
                    List<EndpointInfo> endpoints = null;
                    if (_downloadManager.TryGetProbeResult(taskId, out var probe))
                    {
                        endpoints = probe.Endpoints?.ToList();
                    }

                    // 生成正式 config
                    var configDir = Path.Combine(Path.GetTempPath(), "RDownloader", "config", taskId);
                    Directory.CreateDirectory(configDir);
                    var configPath = Path.Combine(configDir, "rdown_config.json");
                    // 使用当前 GuiConfig 作为基础（继承所有设置，尤其是 TotalConnections），
                    // 避免 probe (64) 和 download (默认64) 的 total_connections 不一致
                    var formalConfig = GuiConfig.Load().ToRdownConfig();
                    formalConfig.Headers = headers;
                    formalConfig.Proxy = proxy;
                    formalConfig.DnsServers = dnsServers;
                    formalConfig.Save(configPath);

                    // 先创建正式 TaskRecord（确保后续任何步骤失败都能在列表中看到 Failed 状态）
                    var record = new TaskRecord
                    {
                        TaskId = taskId,
                        Url = item.Link,
                        FileName = item.FileName,
                        DownloadDir = downloadDir,
                        TaskJsonPath = Path.Combine(downloadDir, $"{item.FileName}.{taskId}.rdown.json"),
                        ConfigPath = configPath,
                        Status = "Running",
                        CreatedAt = DateTime.Now,
                        Headers = headers,
                        Proxy = proxy,
                        DnsServers = dnsServers
                    };
                    TaskRecordManager.Save(record);

                    // 正式记录已保存，安全删除 Parsing 占位记录
                    TaskRecordManager.Delete(tempId);
                    await Dispatcher.InvokeAsync(() => _downloadPage.RefreshTaskList());

                    // Step 2: 转为正式任务
                    await _downloadManager.MoveToFormalAsync(taskId, downloadDir, configPath);

                    // Step 3: 启动 rdown 进程
                    await _downloadManager.AttachAsync(taskId);

                    // 应用端点设置
                    if (endpoints != null)
                    {
                        foreach (var ep in endpoints)
                        {
                            if (string.IsNullOrWhiteSpace(ep.Ip)) continue;
                            try
                            {
                                if (ep.Enabled)
                                    await _downloadManager.EnableEndpointAsync(taskId, ep.Ip);
                                else
                                    await _downloadManager.DisableEndpointAsync(taskId, ep.Ip);
                            }
                            catch (Exception ex)
                            {
                                DebugLogger.Error($"StartDownloadsInBackground [{taskId}] 端点 {ep.Ip}: {ex.Message}");
                            }
                        }
                    }

                    // Step 4: 开始下载
                    await _downloadManager.ResumeAsync(taskId);
                    DebugLogger.Status($"StartDownloadsInBackground: [{taskId}] 下载已启动");
                }
                catch (OperationCanceledException)
                {
                    // Parsing 被用户取消 → 清理临时记录，跳过此任务
                    DebugLogger.Status($"StartDownloadsInBackground: [{tempId}] Parsing 已被取消");
                    TaskRecordManager.Delete(tempId);
                    await Dispatcher.InvokeAsync(() => _downloadPage.RefreshTaskList());
                }
                catch (Exception ex)
                {
                    DebugLogger.Error($"StartDownloadsInBackground [{item.Link}] 失败: {ex.Message}");

                    // 更新对应 TaskRecord 为 Failed
                    try
                    {
                        if (!string.IsNullOrEmpty(taskId))
                        {
                            // 优先更新正式记录
                            var existing = TaskRecordManager.Load(taskId);
                            if (existing != null)
                            {
                                existing.Status = "Failed";
                                TaskRecordManager.Save(existing);
                            }
                            else
                            {
                                // 正式记录不存在 → 创建一条 Failed 记录
                                TaskRecordManager.Save(new TaskRecord
                                {
                                    TaskId = taskId,
                                    Url = item.Link,
                                    FileName = item.FileName,
                                    DownloadDir = downloadDirBase,
                                    Status = "Failed",
                                    CreatedAt = DateTime.Now
                                });
                            }
                        }
                        else
                        {
                            // MoveToFormalAsync 之前失败 → 更新 Parsing 记录
                            var failedRecord = TaskRecordManager.Load(tempId);
                            if (failedRecord != null)
                            {
                                failedRecord.Status = "Failed";
                                TaskRecordManager.Save(failedRecord);
                            }
                        }
                    }
                    catch (Exception innerEx)
                    {
                        DebugLogger.Error($"StartDownloadsInBackground 更新失败状态异常: {innerEx.Message}");
                    }
                    await Dispatcher.InvokeAsync(() => _downloadPage.RefreshTaskList());
                }
                finally
                {
                    // 清理此 Parsing 任务的 CTS
                    _parsingCts.TryRemove(tempId, out _);
                    parseCts.Dispose();
                }
            }

            // 清理探测状态
            ClearProbeResults();
            NowNewTaskStep = NewTaskStep.InputURLs;
        }

        /// <summary>
        /// 根据 NewListItem 的配置确定下载目录。
        /// 优先级：item.DownloadPath → globalFallback → GuiConfig.GetEffectiveDownloadDir()
        /// </summary>
        private static string GetDownloadDir(NewListItem item, string globalFallback = null)
        {
            // 优先使用 item 级别下载路径
            var basePath = item.DownloadPath;
            if (string.IsNullOrWhiteSpace(basePath))
            {
                // 其次使用全局设置（来自 NewListPage 的 GlobalSettings）
                basePath = globalFallback;
            }
            if (string.IsNullOrWhiteSpace(basePath))
            {
                basePath = GuiConfig.GetEffectiveDownloadDir();
            }
            return basePath;
        }

        /// <summary>
        /// 从 NewListItem 的有效设置生成临时 rdown_config.json，写入临时目录，返回路径。
        /// 该文件在 rdown.exe 退出后由 CleanupTempConfig 删除。
        /// </summary>

        /// <summary>
        /// 从 NewListItem 提取有效 Headers/Proxy/DNS 设置，用于写入 TaskRecord
        /// 以便 Config 被删除后重新生成。
        /// </summary>
        private static void GetEffectiveSettings(NewListItem item,
            out Dictionary<string, string> headers,
            out Dictionary<string, string> proxy,
            out List<string> dnsServers)
        {
            var window = Application.Current.MainWindow as MainWindow;
            if (window != null && item.UseGlobalSettings)
            {
                var gs = window._newListPage.GlobalSettingsControl;
                headers = gs.GetEffectiveHeaders();
                proxy = gs.GetEffectiveProxy();
                dnsServers = gs.GetEffectiveDnsServers();
            }
            else
            {
                headers = item.GetEffectiveHeaders();
                proxy = item.GetEffectiveProxy();
                dnsServers = item.GetEffectiveDnsServers();
            }
        }

        private static string SaveConfigForDownload(NewListItem item, string downloadDir)
        {
            try
            {
                // 从 NewListItem 获取有效设置（与 reprobe 逻辑一致）
                Dictionary<string, string> headers, proxy;
                List<string> dnsServers;

                var window = Application.Current.MainWindow as MainWindow;
                if (window != null && item.UseGlobalSettings)
                {
                    var gs = window._newListPage.GlobalSettingsControl;
                    headers = gs.GetEffectiveHeaders();
                    proxy = gs.GetEffectiveProxy();
                    dnsServers = gs.GetEffectiveDnsServers();
                }
                else
                {
                    headers = item.GetEffectiveHeaders();
                    proxy = item.GetEffectiveProxy();
                    dnsServers = item.GetEffectiveDnsServers();
                }

                // 使用当前 GuiConfig 作为基础（继承所有设置，尤其是 TotalConnections），
                // 避免 probe (64) 和 download (默认64) 的 total_connections 不一致
                var config = GuiConfig.Load().ToRdownConfig();
                config.Headers = headers;
                config.Proxy = proxy;
                config.DnsServers = dnsServers;

                // 保存到临时目录，rdown.exe 结束后删除
                var configDir = Path.Combine(Path.GetTempPath(), "RDownloader", "config", item.TaskId);
                Directory.CreateDirectory(configDir);
                var configPath = Path.Combine(configDir, "rdown_config.json");
                config.Save(configPath);
                DebugLogger.Status($"SaveConfigForDownload: [{item.TaskId}] config → {configPath}");
                return configPath;
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"SaveConfigForDownload: [{item.TaskId}] 生成 config 失败: {ex.Message}");
                return null;
            }
        }

        // ── 重新解析 ────────────────────────────────────

        /// <summary>
        /// 对当前 ProbeResults 中所有 item 重新执行 ProbeAsync。
        /// 仅当 Headers/Proxy/DNS 设置相比上次发生变化时才实际 probe。
        /// 成功后原地更新 item 的 FileName / TaskId / 指纹。
        /// 返回失败的 URL 列表（不含因指纹相同而跳过的）。
        /// </summary>
        private List<(NewListItem item, string fingerprint, string tempConfigPath)> CollectReprobeItems()
        {
            var list = new List<(NewListItem item, string fingerprint, string tempConfigPath)>();
            foreach (var item in ProbeResults)
            {
                // 确定有效设置来源
                Dictionary<string, string> headers, proxy;
                List<string> dnsServers;

                if (item.UseGlobalSettings)
                {
                    var gs = _newListPage.GlobalSettingsControl;
                    headers = gs.GetEffectiveHeaders();
                    proxy = gs.GetEffectiveProxy();
                    dnsServers = gs.GetEffectiveDnsServers();
                }
                else
                {
                    headers = item.GetEffectiveHeaders();
                    proxy = item.GetEffectiveProxy();
                    dnsServers = item.GetEffectiveDnsServers();
                }

                // 计算指纹
                var fingerprint = GuiConfig.ComputeFingerprint(headers, proxy, dnsServers);

                // 指纹相同且 TaskId 非空 → 跳过（已成功解析且配置未变）
                if (fingerprint == item.LastProbeFingerprint && !string.IsNullOrEmpty(item.TaskId))
                    continue;

                // 生成临时 config 文件（基于全局设置，仅覆盖 Headers/Proxy/DNS）
                var config = GuiConfig.Load().ToRdownConfig();
                config.Headers = headers;
                config.Proxy = proxy;
                config.DnsServers = dnsServers;
                var dir = Path.Combine(Path.GetTempPath(), "RDownloader", "reprobe", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var configPath = Path.Combine(dir, "rdown_config.json");
                config.Save(configPath);

                list.Add((item, fingerprint, configPath));
            }
            return list;
        }

        public async Task<List<string>> ReprobeAllAsync(IProgress<(int done, int total)> progress = null, CancellationToken cancellationToken = default)
        {
            // ── Phase 1: 收集需要重新解析的 item 及其临时 config ──
            cancellationToken.ThrowIfCancellationRequested();

            List<(NewListItem item, string fingerprint, string tempConfigPath)> reprobeList;
            if (Dispatcher.CheckAccess())
            {
                reprobeList = CollectReprobeItems();
            }
            else
            {
                reprobeList = await Dispatcher.InvokeAsync(CollectReprobeItems);
            }

            if (reprobeList.Count == 0)
                return new List<string>(); // 全部跳过，无失败

            var total = reprobeList.Count;
            var failed = new List<string>();
            var done = 0;

            // ── Phase 2: 并发 probe ──
            var tasks = reprobeList.Select(async tuple =>
            {
                var (item, fingerprint, tempConfigPath) = tuple;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = await _downloadManager.ProbeAsync(item.Link, tempConfigPath, cancellationToken);

                    // 原地更新
                    await Dispatcher.InvokeAsync(() =>
                    {
                        item.FileName = result.Output;
                        item.TaskId = result.Id;
                        item.LastProbeFingerprint = fingerprint;
                    });
                }
                catch (OperationCanceledException)
                {
                    // 正常取消，不记录为失败
                }
                catch (Exception)
                {
                    lock (failed)
                    {
                        failed.Add(item.Link);
                    }
                    // 清除指纹，确保重试时不会被跳过
                    await Dispatcher.InvokeAsync(() =>
                    {
                        item.LastProbeFingerprint = null;
                    });
                }
                finally
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        var current = System.Threading.Interlocked.Increment(ref done);
                        progress?.Report((current, total));
                    }

                    // 清理临时配置目录
                    try
                    {
                        var tempDir = Path.GetDirectoryName(tempConfigPath);
                        if (Directory.Exists(tempDir))
                            Directory.Delete(tempDir, true);
                    }
                    catch { }
                }
            }).ToArray();

            await Task.WhenAll(tasks);

            cancellationToken.ThrowIfCancellationRequested();

            return failed;
        }

        // ── 直接下载 ────────────────────────────────────

        /// <summary>
        /// “直接下载”入口：立即创建 Parsing 任务记录并跳转下载页，
        /// 后台依次执行 ProbeAsync → MoveToFormalAsync → AttachAsync → ResumeAsync。
        /// </summary>
        public async Task StartDirectDownloadAsync(List<string> urls, IProgress<(int done, int total)> progress = null)
        {
            // 跳转到下载页
            await Dispatcher.InvokeAsync(() =>
            {
                if (MainView.SelectedItem != DownloadItem)
                {
                    MainView.SelectedItem = DownloadItem;
                    ContentFrame.Navigate(_downloadPage);
                }
            });

            var total = urls.Count;
            var done = 0;

            // 每个 URL 独立处理：先创建 Parsing 记录，然后后台完成全流程
            var tasks = urls.Select(async url =>
            {
                var tempId = $"parsing-{Guid.NewGuid():N}";
                string taskId = null;

                // --- 创建此任务的取消令牌，用于「取消」/「删除」时直接结束进程 ---
                var parseCts = new CancellationTokenSource();
                _parsingCts[tempId] = parseCts;

                try
                {
                    parseCts.Token.ThrowIfCancellationRequested();

                    // --- 创建临时 Parsing 记录 ---
                    var downloadDirBase = GuiConfig.GetEffectiveDownloadDir();

                    var tempRecord = new TaskRecord
                    {
                        TaskId = tempId,
                        Url = url,
                        FileName = "解析中...",
                        DownloadDir = downloadDirBase,
                        Status = "Parsing",
                        CreatedAt = DateTime.Now
                    };
                    TaskRecordManager.Save(tempRecord);

                    await Dispatcher.InvokeAsync(() => _downloadPage.RefreshTaskList());

                    // --- 生成临时 config（使用全局设置） ---
                    var guiConfig = GuiConfig.Load();
                    var rdownConfig = guiConfig.ToRdownConfig();
                    var configDir = Path.Combine(Path.GetTempPath(), "RDownloader", "config", tempId);
                    Directory.CreateDirectory(configDir);
                    var configPath = Path.Combine(configDir, "rdown_config.json");
                    rdownConfig.Save(configPath);

                    // --- Step 1: 探测 ---
                    parseCts.Token.ThrowIfCancellationRequested();
                    var result = await _downloadManager.ProbeAsync(url, configPath, parseCts.Token);

                    // --- Step 2: 转为正式任务 ---
                    taskId = result.Id;
                    var downloadDir = downloadDirBase;
                    var formalConfigDir = Path.Combine(Path.GetTempPath(), "RDownloader", "config", taskId);
                    Directory.CreateDirectory(formalConfigDir);
                    var formalConfigPath = Path.Combine(formalConfigDir, "rdown_config.json");
                    rdownConfig.Save(formalConfigPath);

                    // 先创建正式 TaskRecord（确保 MoveToFormalAsync 失败也能在列表中看到 Failed）
                    var record = new TaskRecord
                    {
                        TaskId = taskId,
                        Url = url,
                        FileName = result.Output,
                        DownloadDir = downloadDir,
                        TaskJsonPath = Path.Combine(downloadDir, $"{result.Output}.{taskId}.rdown.json"),
                        ConfigPath = formalConfigPath,
                        Status = "Running",
                        CreatedAt = DateTime.Now,
                        Headers = rdownConfig.Headers,
                        Proxy = rdownConfig.Proxy,
                        DnsServers = rdownConfig.DnsServers
                    };
                    TaskRecordManager.Save(record);

                    // 正式记录已保存，安全删除 Parsing 占位记录
                    TaskRecordManager.Delete(tempId);

                    await _downloadManager.MoveToFormalAsync(taskId, downloadDir, formalConfigPath);

                    await Dispatcher.InvokeAsync(() => _downloadPage.RefreshTaskList());

                    // --- Step 3: 启动 rdown 进程 ---
                    await _downloadManager.AttachAsync(taskId);

                    // --- 应用端点配置 ---
                    if (result.Endpoints != null)
                    {
                        foreach (var ep in result.Endpoints)
                        {
                            if (string.IsNullOrWhiteSpace(ep.Ip)) continue;
                            try
                            {
                                if (ep.Enabled)
                                    await _downloadManager.EnableEndpointAsync(taskId, ep.Ip);
                                else
                                    await _downloadManager.DisableEndpointAsync(taskId, ep.Ip);
                            }
                            catch (Exception ex)
                            {
                                DebugLogger.Error($"DirectDownload [{taskId}] 端点 {ep.Ip}: {ex.Message}");
                            }
                        }
                    }

                    // --- Step 4: 开始下载 ---
                    await _downloadManager.ResumeAsync(taskId);
                    DebugLogger.Status($"DirectDownload: [{taskId}] 全部完成，下载已启动");
                }
                catch (Exception ex)
                {
                    DebugLogger.Error($"DirectDownload [{url}] 失败: {ex.Message}");

                    // 标记 TaskRecord 为 Failed
                    try
                    {
                        // 优先尝试正式记录（ProbeAsync 成功后已保存，Parsing 已删除）
                        if (!string.IsNullOrEmpty(taskId))
                        {
                            var record = TaskRecordManager.Load(taskId);
                            if (record != null)
                            {
                                record.Status = "Failed";
                                TaskRecordManager.Save(record);
                            }
                        }
                        // 回退到 Parsing 记录（ProbeAsync 之前就失败了）
                        if (string.IsNullOrEmpty(taskId) || TaskRecordManager.Load(taskId) == null)
                        {
                            var failedRecord = TaskRecordManager.Load(tempId);
                            if (failedRecord != null)
                            {
                                failedRecord.Status = "Failed";
                                TaskRecordManager.Save(failedRecord);
                            }
                        }
                    }
                    catch { }

                    await Dispatcher.InvokeAsync(() => _downloadPage.RefreshTaskList());
                }
                finally
                {
                    // 清理此 Parsing 任务的 CTS
                    _parsingCts.TryRemove(tempId, out _);
                    parseCts.Dispose();

                    var current = System.Threading.Interlocked.Increment(ref done);
                    progress?.Report((current, total));
                }
            }).ToArray();

            await Task.WhenAll(tasks);
        }

        // ── 导航 ────────────────────────────────────────

        internal void NavigateToSettings()
        {
            MainView.SelectedItem = SettingsItem;
        }

        internal void RefreshNewListPageConfig()
        {
            _newListPage.RefreshConfig();
        }

        /// <summary>
        /// 从 SettingsPage 实时同步 DNS 和代理条目到 NewListPage（仅条目，不同步启用/禁用）。
        /// </summary>
        internal void SyncDnsProxyToNewListPage(List<string> dnsAddresses, List<KeyValueItem> proxyEntries)
        {
            _newListPage.SyncDnsProxyEntries(dnsAddresses, proxyEntries);
        }

        /// <summary>
        /// 加载端点数据并导航到端点选择页。
        /// </summary>
        internal async System.Threading.Tasks.Task NavigateToEndpointSelectsAsync()
        {
            await _selectEndpointPage.LoadEndpointsAsync();
            NowNewTaskStep = NewTaskStep.EndpointSelects;
        }

        /// <summary>
        /// 获取 RDownloaderManager 实例（供 NewListPage 等使用）。
        /// </summary>
        public RDownloaderManager DownloadManager => _downloadManager;

        private void MainView_SelectionChanged(iNKORE.UI.WPF.Modern.Controls.NavigationView sender, iNKORE.UI.WPF.Modern.Controls.NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem == DownloadItem)
            {
                _downloadPage.RefreshTaskList();
                ContentFrame.Navigate(_downloadPage);
            }
            else if (args.SelectedItem == NewItem)
            {
                // 从设置页返回时刷新配置，确保 DNS/代理等修改已同步
                if (nowNewTaskStep == NewTaskStep.DownloadSettings)
                {
                    _newListPage.RefreshConfig();
                }
                NowNewTaskStep = nowNewTaskStep;
            }
            else
            {
                ContentFrame.Navigate(_settingsPage);
            }
        }
    }
}
