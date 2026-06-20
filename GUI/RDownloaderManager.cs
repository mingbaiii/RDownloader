using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RDownloaderGUI
{
    // ──────────────────────────────────────────────
    //  Enums
    // ──────────────────────────────────────────────

    public enum DownloadTaskStatus
    {
        Idle,
        Parsing,
        Running,
        Paused,
        Completed,
        Failed,
        Cancelled
    }

    // ──────────────────────────────────────────────
    //  DTOs – match rdown JSON output (snake_case)
    // ──────────────────────────────────────────────

    public class DownloadInfo
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("output")]
        public string Output { get; set; }

        [JsonPropertyName("file_size")]
        public long FileSize { get; set; }

        [JsonPropertyName("downloaded")]
        public long Downloaded { get; set; }

        [JsonPropertyName("progress")]
        public double Progress { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("speed")]
        public double Speed { get; set; }

        [JsonPropertyName("endpoints")]
        public List<EndpointInfo> Endpoints { get; set; }
    }

    public class DownloadFullInfo : DownloadInfo
    {
        [JsonPropertyName("actual_url")]
        public string ActualUrl { get; set; }

        [JsonPropertyName("connections")]
        public int Connections { get; set; }

        [JsonPropertyName("segments")]
        public List<SegmentInfo> Segments { get; set; }
    }

    public class IcmpPingResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("rtt_ms")]
        public double? RttMs { get; set; }
    }

    public class TcpPingResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("port")]
        public int? Port { get; set; }

        [JsonPropertyName("latency_ms")]
        public double? LatencyMs { get; set; }
    }

    public class SpeedTestResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("status_code")]
        public int? StatusCode { get; set; }

        [JsonPropertyName("header_size")]
        public long? HeaderSize { get; set; }

        [JsonPropertyName("latency_ms")]
        public double? LatencyMs { get; set; }

        [JsonPropertyName("speed_bps")]
        public double? SpeedBps { get; set; }

        [JsonPropertyName("final_url")]
        public string FinalUrl { get; set; }
    }

    public class EndpointInfo
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("ip")]
        public string Ip { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("proxy")]
        public string Proxy { get; set; }

        [JsonPropertyName("icmp_ping")]
        public IcmpPingResult IcmpPing { get; set; }

        [JsonPropertyName("tcp_ping")]
        public TcpPingResult TcpPing { get; set; }

        [JsonPropertyName("speed_test")]
        public SpeedTestResult SpeedTest { get; set; }

        [JsonPropertyName("best_latency_ms")]
        public double? BestLatencyMs { get; set; }

        [JsonPropertyName("is_available")]
        public bool IsAvailable { get; set; }
    }

    public class SegmentInfo
    {
        [JsonPropertyName("start")]
        public long Start { get; set; }

        [JsonPropertyName("end")]
        public long End { get; set; }

        [JsonPropertyName("downloaded")]
        public long Downloaded { get; set; }

        [JsonPropertyName("completed")]
        public bool Completed { get; set; }

        [JsonPropertyName("endpoint_id")]
        public int? EndpointId { get; set; }

        [JsonPropertyName("speed")]
        public double Speed { get; set; }
    }

    // ──────────────────────────────────────────────
    //  ProbeResult (Class 1 output)
    // ──────────────────────────────────────────────

    public class ProbeResult
    {
        public string Id { get; }
        public string Output { get; }
        public List<EndpointInfo> Endpoints { get; }
        public Dictionary<string, string> ResponseHeaders { get; }

        internal byte[] JsonContent { get; }

        public ProbeResult(string id, string output, byte[] jsonContent, List<EndpointInfo> endpoints, Dictionary<string, string> responseHeaders = null)
        {
            Id = id;
            Output = output;
            JsonContent = jsonContent;
            Endpoints = endpoints ?? new List<EndpointInfo>();
            ResponseHeaders = responseHeaders ?? new Dictionary<string, string>();
        }
    }

    // ──────────────────────────────────────────────
    //  EventArgs
    // ──────────────────────────────────────────────

    public class TaskStatusChangedEventArgs : EventArgs
    {
        public string TaskId { get; }
        public DownloadTaskStatus Status { get; }
        public DownloadFullInfo Info { get; }

        public TaskStatusChangedEventArgs(string taskId, DownloadTaskStatus status, DownloadFullInfo info)
        {
            TaskId = taskId;
            Status = status;
            Info = info;
        }
    }

    // ──────────────────────────────────────────────
    //  Internal JSON helpers
    // ──────────────────────────────────────────────

    internal class RdownResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("output")]
        public string Output { get; set; }

        [JsonPropertyName("download")]
        public JsonElement? Download { get; set; }

        [JsonPropertyName("downloads")]
        public JsonElement? Downloads { get; set; }

        [JsonPropertyName("endpoints")]
        public JsonElement? Endpoints { get; set; }

        [JsonPropertyName("response_headers")]
        public JsonElement? ResponseHeaders { get; set; }
    }

    internal class RdownException : Exception
    {
        public RdownException(string message) : base(message) { }
    }

    // ──────────────────────────────────────────────
    //  DownloadTask (Class 2 – one per download)
    // ──────────────────────────────────────────────

    internal class DownloadTask : IDisposable
    {
        private readonly string _taskId;
        private readonly string _output;
        private readonly string _downloadDir;
        private readonly string _rdownExePath;
        private readonly string _configPath;   // may be null

        private Process _process;
        private StreamWriter _stdin;
        private StreamReader _stdout;
        private StreamReader _stderr;
        private CancellationTokenSource _stderrReadCts;
        private readonly SemaphoreSlim _cmdLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _pollCts;

        private readonly JsonSerializerOptions _jsonOptions;

        public string TaskId => _taskId;
        public DownloadTaskStatus Status { get; private set; } = DownloadTaskStatus.Idle;
        public DownloadFullInfo LastInfo { get; private set; }

        public event Action<DownloadTask, DownloadTaskStatus, DownloadFullInfo> OnStatusChanged;

        public DownloadTask(
            string taskId,
            string output,
            string downloadDir,
            string rdownExePath,
            string configPath)
        {
            _taskId = taskId ?? throw new ArgumentNullException(nameof(taskId));
            _output = output ?? throw new ArgumentNullException(nameof(output));
            _downloadDir = downloadDir ?? throw new ArgumentNullException(nameof(downloadDir));
            _rdownExePath = rdownExePath ?? throw new ArgumentNullException(nameof(rdownExePath));
            _configPath = configPath;

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
        }

        // ── public async API ──────────────────────────

        /// <summary>
        /// Spawn the rdown stdin process.  Must be called before ResumeAsync.
        /// 幂等：若进程已附加且存活，直接返回不做重复 spawn。
        /// </summary>
        public async Task AttachAsync()
        {
            if (_process != null && !_process.HasExited)
            {
                DebugLogger.Status($"AttachAsync: [{_taskId}] process already attached, skipping spawn");
                return;
            }

            // Config 可能在进程退出时被 CleanupTempConfig 删除。
            // 从 TaskRecord 中读取任务的 Headers/Proxy/DNS 设置并重建。
            EnsureConfigExists();

            var wasRunning = (Status == DownloadTaskStatus.Running);

            DebugLogger.Status($"AttachAsync: spawning process for [{_taskId}] → CWD: {_downloadDir}");
            await Task.Run(() => SpawnProcess());
            DebugLogger.Status($"AttachAsync: process spawned for [{_taskId}]");

            // rdown 启动时 task 处于暂停态，若之前是 Running 则自动补发 resume
            if (wasRunning)
            {
                DebugLogger.Status($"AttachAsync: [{_taskId}] was Running, auto-resuming...");
                Status = DownloadTaskStatus.Idle; // ResumeAsync 要求 Idle/Paused 起始态
                await ResumeAsync();
            }
        }

        /// <summary>
        /// 若 rdown_config.json 被删除（如进程退出时清理），
        /// 从 TaskRecord 持久化设置中重建，确保 Resume 时能传入正确的 --config。
        /// 全局设置（chunk_size 等）从 GuiConfig 读取，Headers/Proxy/DNS 使用任务级别存盘值。
        /// </summary>
        private void EnsureConfigExists()
        {
            if (string.IsNullOrEmpty(_configPath) || File.Exists(_configPath))
                return;

            try
            {
                var record = TaskRecordManager.Load(_taskId);
                if (record == null)
                {
                    DebugLogger.Error($"EnsureConfigExists: [{_taskId}] TaskRecord not found, cannot rebuild config");
                    return;
                }

                // 以当前 GuiConfig 为基础（获取最新的全局设置），
                // 再用任务级别的 Headers/Proxy/DNS 覆盖。
                var config = GuiConfig.Load().ToRdownConfig();
                if (record.Headers != null) config.Headers = record.Headers;
                if (record.Proxy != null) config.Proxy = record.Proxy;
                if (record.DnsServers != null) config.DnsServers = record.DnsServers;

                var dir = Path.GetDirectoryName(_configPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                config.Save(_configPath);
                DebugLogger.Status($"EnsureConfigExists: [{_taskId}] config rebuilt from TaskRecord + GuiConfig → {_configPath}");
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"EnsureConfigExists: [{_taskId}] failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Send resume command and start status polling.
        /// </summary>
        public async Task ResumeAsync()
        {
            if (_process == null || _process.HasExited)
                throw new InvalidOperationException("Process is not attached.  Call AttachAsync first.");

            if (Status != DownloadTaskStatus.Idle && Status != DownloadTaskStatus.Paused)
                throw new InvalidOperationException($"Cannot resume from status: {Status}");

            DebugLogger.Status($"ResumeAsync: [{_taskId}] sending resume command...");
            try
            {
                var resp = await SendCommandAsync("resume", new[] { _taskId });
                EnsureSuccess(resp);
            }
            catch
            {
                // rdown 返回错误（如全部端点不可用）→ 标记为失败
                Status = DownloadTaskStatus.Failed;
                FireStatusChanged();
                throw;
            }

            Status = DownloadTaskStatus.Running;
            StartPolling();
            FireStatusChanged();
            DebugLogger.Status($"ResumeAsync: [{_taskId}] now Running, polling started");
        }

        /// <summary>
        /// Pause the download.  Cancels polling, sends pause + exit, process exits.
        /// </summary>
        public async Task PauseAsync()
        {
            DebugLogger.Status($"PauseAsync: [{_taskId}] pausing...");
            StopPolling();

            // 必须在 SendExitAndCleanupAsync 之前设 Status，否则 OnProcessExited
            // 会因竞态条件把状态错误地改为 Failed。
            Status = DownloadTaskStatus.Paused;

            try
            {
                var resp = await SendCommandAsync("pause", new[] { _taskId });
                EnsureSuccess(resp);
            }
            finally
            {
                await SendExitAndCleanupAsync();
                FireStatusChanged();
                DebugLogger.Status($"PauseAsync: [{_taskId}] paused, process exited");
            }
        }

        /// <summary>
        /// Cancel the download.  Optionally deletes partial file and rdown.json.
        /// </summary>
        public async Task CancelAsync(bool deleteResidual = true)
        {
            DebugLogger.Status($"CancelAsync: [{_taskId}] cancelling (deleteResidual={deleteResidual})...");
            StopPolling();

            // 必须在 SendExitAndCleanupAsync 之前设 Status，否则 OnProcessExited
            // 会因竞态条件把状态错误地改为 Failed。
            Status = DownloadTaskStatus.Cancelled;

            try
            {
                if (_process != null && !_process.HasExited)
                {
                    var resp = await SendCommandAsync("cancel", new[] { _taskId });
                    EnsureSuccess(resp);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"CancelAsync: [{_taskId}] cancel command failed: {ex.Message}");
            }
            finally
            {
                await SendExitAndCleanupAsync();

                if (deleteResidual)
                {
                    DeleteResidualFiles();
                }

                FireStatusChanged();
            }
        }

        /// <summary>
        /// Get basic download info (deserialized as full info for consistency).
        ///   - If process is alive → via stdin
        ///   - Otherwise → direct execution mode
        /// </summary>
        public async Task<DownloadFullInfo> InfoAsync()
        {
            var resp = await ExecuteCommandAsync("info", new[] { _taskId });
            EnsureSuccess(resp);

            if (resp.Download == null)
                throw new RdownException("Missing 'download' in info response.");

            var info = Deserialize<DownloadFullInfo>(resp.Download.Value);
            LastInfo = info;
            return info;
        }

        /// <summary>
        /// Get full download info (with segments).
        /// </summary>
        public async Task<DownloadFullInfo> InfoAllAsync()
        {
            var resp = await ExecuteCommandAsync("info", new[] { "all", _taskId });
            EnsureSuccess(resp);

            if (resp.Download == null)
                throw new RdownException("Missing 'download' in info all response.");

            var info = Deserialize<DownloadFullInfo>(resp.Download.Value);
            LastInfo = info;
            return info;
        }

        /// <summary>
        /// Return the endpoint list for this task.
        /// </summary>
        public async Task<List<EndpointInfo>> GetEndpointsAsync()
        {
            var info = await InfoAsync();
            return info.Endpoints ?? new List<EndpointInfo>();
        }

        /// <summary>
        /// Add an endpoint (IP + optional proxy) to this download.
        /// </summary>
        public async Task AddEndpointAsync(string ip, string proxy = null)
        {
            var args = new List<string> { _taskId, ip };
            if (!string.IsNullOrEmpty(proxy))
            {
                args.Add("--proxy");
                args.Add(proxy);
            }

            var resp = await ExecuteCommandAsync("add-endpoint", args.ToArray());
            EnsureSuccess(resp);
        }

        /// <summary>
        /// Disable an endpoint for this download.
        /// </summary>
        public async Task DisableEndpointAsync(string ip)
        {
            var resp = await ExecuteCommandAsync("disable-endpoint", new[] { _taskId, ip });
            EnsureSuccess(resp);
        }

        /// <summary>
        /// Enable an endpoint for this download.
        /// </summary>
        public async Task EnableEndpointAsync(string ip)
        {
            var resp = await ExecuteCommandAsync("enable-endpoint", new[] { _taskId, ip });
            EnsureSuccess(resp);
        }

        // ── process management ────────────────────────

        private void SpawnProcess()
        {
            var args = BuildGlobalArgs();   // "--json [--config conf]"

            // 输出传入的 config 内容
            if (!string.IsNullOrEmpty(_configPath) && File.Exists(_configPath))
            {
                try
                {
                    var configJson = File.ReadAllText(_configPath, Encoding.UTF8);
                    DebugLogger.Info($"[{_taskId}] 传入 config 内容:\n  {configJson}");
                }
                catch { }
            }

            DebugLogger.Command(_rdownExePath, args, _downloadDir);

            var psi = new ProcessStartInfo
            {
                FileName = _rdownExePath,
                Arguments = args,
                WorkingDirectory = _downloadDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Exited += OnProcessExited;
            _process.Start();

            _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false))
            {
                AutoFlush = false
            };
            _stdout = _process.StandardOutput;
            _stderr = _process.StandardError;

            // 启动后台线程持续读取 stderr
            _stderrReadCts = new CancellationTokenSource();
            var token = _stderrReadCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var line = await _stderr.ReadLineAsync();
                        if (line == null) break; // stream closed
                        if (!string.IsNullOrWhiteSpace(line))
                            DebugLogger.Stderr(_taskId, line);
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (Exception ex)
                {
                    DebugLogger.Error($"[{_taskId}] stderr reader crashed: {ex.Message}");
                }
            }, token);
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            // ExitCode 在进程已 dispose 后访问会抛异常，用 try 保护
            int? exitCode = null;
            try { exitCode = _process?.ExitCode; }
            catch (InvalidOperationException) { /* 进程已被 dispose */ }

            DebugLogger.Status($"OnProcessExited: [{_taskId}] exit code = {exitCode}, status was {Status}");

            // 由 PauseAsync/CancelAsync 主动退出 → 正常流程，不干预
            if (Status == DownloadTaskStatus.Paused || Status == DownloadTaskStatus.Cancelled)
                return;

            // 进程意外退出 → 标记为失败
            if (Status == DownloadTaskStatus.Running)
            {
                DebugLogger.Error($"[{_taskId}] rdown process exited unexpectedly while Running");
                Status = DownloadTaskStatus.Failed;
                StopPolling();
                CleanupTempConfig();
                FireStatusChanged();
            }
        }

        private async Task SendExitAndCleanupAsync()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    await SendCommandInternalAsync("exit", null);
                }
            }
            catch
            {
                // if exit fails, just kill
            }
            finally
            {
                CleanupProcess();
                CleanupTempConfig();
            }
        }

        private void CleanupProcess()
        {
            // 停止 stderr 读取
            try
            {
                _stderrReadCts?.Cancel();
                _stderrReadCts?.Dispose();
                _stderrReadCts = null;
            }
            catch { }

            // 取消订阅 Exited，防止事件在 dispose 后触发
            if (_process != null)
            {
                try { _process.Exited -= OnProcessExited; }
                catch { }
            }

            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                }
            }
            catch { }

            _stdin?.Dispose();
            _stdout?.Dispose();
            _stderr?.Dispose();
            _process?.Dispose();

            _stdin = null;
            _stdout = null;
            _stderr = null;
            _process = null;
        }

        private void DeleteResidualFiles()
        {
            try
            {
                // .rdown.json state file
                var jsonFile = Path.Combine(_downloadDir, $"{_output}.{_taskId}.rdown.json");
                if (File.Exists(jsonFile))
                    File.Delete(jsonFile);

                // partial output file
                var outFile = Path.Combine(_downloadDir, _output);
                if (File.Exists(outFile))
                    File.Delete(outFile);
            }
            catch
            {
                // best-effort
            }

            CleanupTempConfig();
        }

        /// <summary>
        /// 删除临时 rdown_config.json 及其父目录（如果为空）。
        /// </summary>
        private void CleanupTempConfig()
        {
            if (string.IsNullOrEmpty(_configPath) || !File.Exists(_configPath))
                return;

            try
            {
                File.Delete(_configPath);
                DebugLogger.Status($"CleanupTempConfig: [{_taskId}] 已删除 {_configPath}");

                // 尝试清理空父目录
                try
                {
                    var dir = Path.GetDirectoryName(_configPath);
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch { }
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"CleanupTempConfig: [{_taskId}] 失败: {ex.Message}");
            }
        }

        // ── command execution ─────────────────────────

        /// <summary>
        /// Auto-route: stdin if process alive, else direct execution.
        /// </summary>
        private async Task<RdownResponse> ExecuteCommandAsync(string command, string[] args)
        {
            if (_process != null && !_process.HasExited)
                return await SendCommandAsync(command, args);
            else
                return await RunDirectCommandAsync(command, args);
        }

        /// <summary>
        /// Send a command via the stdin pipe (serialised by _cmdLock).
        /// Returns the parsed JSON response line.
        /// </summary>
        private async Task<RdownResponse> SendCommandAsync(string command, string[] args)
        {
            await _cmdLock.WaitAsync();
            try
            {
                return await SendCommandInternalAsync(command, args);
            }
            finally
            {
                _cmdLock.Release();
            }
        }

        private async Task<RdownResponse> SendCommandInternalAsync(string command, string[] args)
        {
            // Build JSON payload
            var payload = new Dictionary<string, object>
            {
                ["command"] = command
            };
            if (args != null && args.Length > 0)
                payload["args"] = args;

            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            DebugLogger.Stdin(_taskId, json);
            await _stdin.WriteLineAsync(json);
            await _stdin.FlushAsync();

            var line = await _stdout.ReadLineAsync();
            DebugLogger.Stdout(_taskId, line);
            if (line == null)
                throw new RdownException("rdown process closed stdout unexpectedly.");

            var resp = JsonSerializer.Deserialize<RdownResponse>(line, _jsonOptions);
            return resp;
        }

        /// <summary>
        /// Run a single command in direct-execution mode (short-lived process).
        /// </summary>
        private async Task<RdownResponse> RunDirectCommandAsync(string command, string[] args)
        {
            var cliArgs = BuildGlobalArgs() + " " + command;

            if (args != null && args.Length > 0)
            {
                foreach (var a in args)
                    cliArgs += " " + EscapeArg(a);
            }

            var psi = new ProcessStartInfo
            {
                FileName = _rdownExePath,
                Arguments = cliArgs,
                WorkingDirectory = _downloadDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            return await Task.Run(() =>
            {
                using (var proc = new Process { StartInfo = psi })
                {
                    DebugLogger.Command(_rdownExePath, cliArgs, _downloadDir);

                    proc.Start();
                    var stdout = proc.StandardOutput.ReadToEnd();
                    var stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(30_000);

                    DebugLogger.Stdout(_taskId, stdout);
                    if (!string.IsNullOrWhiteSpace(stderr))
                        DebugLogger.Stderr(_taskId, stderr);

                    if (string.IsNullOrWhiteSpace(stdout))
                        throw new RdownException("rdown produced no output.");

                    var resp = JsonSerializer.Deserialize<RdownResponse>(stdout, _jsonOptions);
                    return resp;
                }
            });
        }

        // ── polling ────────────────────────────────────

        private void StartPolling()
        {
            _pollCts = new CancellationTokenSource();
            _ = PollLoopAsync(_pollCts.Token);
        }

        private void StopPolling()
        {
            if (_pollCts != null)
            {
                _pollCts.Cancel();
                _pollCts.Dispose();
                _pollCts = null;
            }

            // Don't await _pollTask here – the polling loop will exit naturally
            // when cancelled.  The caller (PauseAsync/CancelAsync) holds _cmdLock
            // after stopping polling, so no new commands sneak in.
                    }

        private async Task PollLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (ct.IsCancellationRequested) break;

                try
                {
                    var resp = await SendCommandAsync("info", new[] { "all", _taskId });
                    if (resp.Status == "error")
                    {
                        // e.g. "Download not found" – treat as failure
                        DebugLogger.Error($"[{_taskId}] poll info returned error: {resp.Message}");
                        Status = DownloadTaskStatus.Failed;
                        FireStatusChanged();
                        await SendExitAndCleanupAsync();
                        return;
                    }

                    if (resp.Download != null)
                    {
                        var info = Deserialize<DownloadFullInfo>(resp.Download.Value);
                        LastInfo = info;

                        var newStatus = ParseStatusString(info.Status);
                        if (newStatus != DownloadTaskStatus.Running)
                        {
                            DebugLogger.Status($"[{_taskId}] download finished → {newStatus}");
                            Status = newStatus;
                            FireStatusChanged();
                            await SendExitAndCleanupAsync();
                            return;
                        }

                        // Still running – fire event so UI can update progress
                        FireStatusChanged();
                    }
                    else if (!string.IsNullOrEmpty(resp.Message)
                             && resp.Message.IndexOf("completed", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // .rdown.json 已被 rdown 删除，仅有 message: "Download completed"
                        DebugLogger.Status($"[{_taskId}] download completed (no task JSON)");
                        Status = DownloadTaskStatus.Completed;
                        // 确保所有字段置 100%：LastInfo 可能仍是上一轮轮询的旧值
                        if (LastInfo != null && LastInfo.FileSize > 0)
                        {
                            LastInfo.Downloaded = LastInfo.FileSize;
                            LastInfo.Progress = 100.0;
                            LastInfo.Speed = 0;
                            LastInfo.Status = "completed";
                        }
                        FireStatusChanged();
                        await SendExitAndCleanupAsync();
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Polling error – don't crash; try again next tick
                    // If process died, OnProcessExited will handle it
                }
            }
        }

        // ── helpers ────────────────────────────────────

        private string BuildGlobalArgs()
        {
            var sb = new StringBuilder("--json");
            if (!string.IsNullOrEmpty(_configPath) && File.Exists(_configPath))
            {
                sb.Append(" --config ");
                sb.Append(EscapeArg(_configPath));
            }
            return sb.ToString();
        }

        private static string EscapeArg(string arg)
        {
            if (string.IsNullOrEmpty(arg))
                return "\"\"";
            if (arg.IndexOf(' ') >= 0 || arg.IndexOf('"') >= 0)
                return "\"" + arg.Replace("\"", "\\\"") + "\"";
            return arg;
        }

        private static void EnsureSuccess(RdownResponse resp)
        {
            if (resp == null)
                throw new RdownException("Null response from rdown.");
            if (resp.Status == "error")
                throw new RdownException(resp.Message ?? "Unknown rdown error.");
        }

        private T Deserialize<T>(JsonElement element)
        {
            return JsonSerializer.Deserialize<T>(element.GetRawText(), _jsonOptions);
        }

        private static DownloadTaskStatus ParseStatusString(string status)
        {
            switch (status)
            {
                case "pending": return DownloadTaskStatus.Idle;
                case "downloading": return DownloadTaskStatus.Running;
                case "paused": return DownloadTaskStatus.Paused;
                case "completed": return DownloadTaskStatus.Completed;
                case "failed": return DownloadTaskStatus.Failed;
                case "cancelled": return DownloadTaskStatus.Cancelled;
                default: return DownloadTaskStatus.Idle;
            }
        }

        private void FireStatusChanged()
        {
            // 持久化任务状态到 AppData
            try
            {
                TaskRecordManager.UpdateStatus(
                    _taskId,
                    Status.ToString(),
                    LastInfo?.Downloaded ?? 0,
                    LastInfo?.FileSize ?? 0,
                    LastInfo?.Speed ?? 0);
            }
            catch { }

            OnStatusChanged?.Invoke(this, Status, LastInfo);
        }

        public void Dispose()
        {
            StopPolling();
            CleanupProcess();
            _cmdLock?.Dispose();
            _pollCts?.Dispose();
        }
    }

    // ──────────────────────────────────────────────
    //  RDownloaderManager – public façade
    // ──────────────────────────────────────────────

    public class RDownloaderManager : IDisposable
    {
        /// <summary>
        /// 全局调试开关。设为 true 后，所有 rdown 命令、stdin/stdout 将输出到独立日志窗口。
        /// </summary>
        public static bool DEBUG_MODE
        {
            get => DebugLogger.Enabled;
            set => DebugLogger.Enabled = value;
        }

        private readonly string _rdownExePath;

        // Class 1 – probe state (supports concurrent probes)
        private readonly ConcurrentDictionary<string, ProbeResult> _probes = new ConcurrentDictionary<string, ProbeResult>();

        // Class 2 – active tasks
        private readonly Dictionary<string, DownloadTask> _tasks = new Dictionary<string, DownloadTask>();
        private readonly object _tasksLock = new object();

        public event EventHandler<TaskStatusChangedEventArgs> TaskStatusChanged;

        // ── static: embedded exe extraction ─────────────

        private static readonly string _rdownExeDir = Path.Combine(Path.GetTempPath(), "RDownloader", "bin");
        private static readonly string _defaultExePath = Path.Combine(_rdownExeDir, "rdown.exe");
        private static readonly object _extractLock = new object();
        private static bool _extracted;

        /// <summary>
        /// Extract rdown.exe from embedded resource to temp dir.
        /// Returns the path to the extracted exe.
        /// </summary>
        public static string ExtractRdownExe()
        {
            var destPath = _defaultExePath;

            lock (_extractLock)
            {
                if (_extracted && File.Exists(destPath))
                    return destPath;

                var dir = Path.GetDirectoryName(destPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("RDownloaderGUI.rdown.exe"))
                {
                    if (stream == null)
                        throw new FileNotFoundException("Embedded resource 'rdown.exe' not found. Ensure it is included as EmbeddedResource in the project.");

                    using (var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                    {
                        stream.CopyTo(fileStream);
                    }
                }

                _extracted = true;
                return destPath;
            }
        }

        // ── constructor ────────────────────────────────

        /// <summary>
        /// </summary>
        /// <param name="rdownExePath">
        ///   Full path to rdown.exe.  If null, auto-extract from embedded resource.
        /// </param>
        public RDownloaderManager(string rdownExePath = null)
        {
            _rdownExePath = rdownExePath ?? ExtractRdownExe();
            DebugLogger.Status($"RDownloaderManager 初始化: exe={_rdownExePath}");
        }

        // ── Class 1: Probe ─────────────────────────────

        /// <summary>
        /// Probe a URL in a temp directory.
        /// Runs "rdown --json download <url>" (direct execution).
        /// Reads the resulting .rdown.json into memory, then deletes the temp dir.
        /// Returns extracted id + output filename.
        /// Supports concurrent probes – each URL gets its own temp dir.
        /// </summary>
        /// <param name="url">The URL to probe.</param>
        /// <param name="configPathOverride">
        ///   If non-null, use this config file instead of the global one.
        ///   Used for re-probing with temporary in-memory settings.
        /// </param>
        public async Task<ProbeResult> ProbeAsync(string url, string configPathOverride = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("URL is required.", nameof(url));

            DebugLogger.Separator();
            DebugLogger.Status($"ProbeAsync: url={url}, configOverride={configPathOverride ?? "(none)"}");

            // 输出传入的 config 内容
            if (!string.IsNullOrEmpty(configPathOverride) && File.Exists(configPathOverride))
            {
                try
                {
                    var configJson = File.ReadAllText(configPathOverride, Encoding.UTF8);
                    DebugLogger.Info($"传入 config 内容:\n  {configJson}");
                }
                catch { }
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "RDownloader", "probe", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                // 不传 --output，让 rdown 自己根据 Content-Disposition 响应头确定文件名
                var probeArgs = new List<string> { EscapeArg(url) };
                var args = BuildArgs("download", probeArgs.ToArray(), configPathOverride);

                var psi = new ProcessStartInfo
                {
                    FileName = _rdownExePath,
                    Arguments = args,
                    WorkingDirectory = tempDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                return await Task.Run(() =>
                {
                    using (var proc = new Process { StartInfo = psi })
                    {
                        DebugLogger.Command(_rdownExePath, args, tempDir);

                        proc.Start();

                        // 注册取消：终止 rdown 进程
                        // 用 wasKilled 标记进程是否被取消回调实际杀死，
                        // 避免进程正常完成后因 token 已取消而误抛异常
                        string stdout, stderr;
                        bool wasKilled = false;
                        using (var reg = cancellationToken.Register(() =>
                        {
                            try
                            {
                                if (!proc.HasExited)
                                {
                                    DebugLogger.Status($"ProbeAsync: 取消信号触发，正在终止 rdown 进程 (PID={proc.Id})...");
                                    proc.Kill();
                                    wasKilled = true;
                                    DebugLogger.Status($"ProbeAsync: rdown 进程已终止 (PID={proc.Id})");
                                }
                            }
                            catch (Exception ex)
                            {
                                DebugLogger.Error($"ProbeAsync: 终止进程失败 (PID={proc.Id}): {ex.Message}");
                            }
                        }))
                        {
                            stdout = proc.StandardOutput.ReadToEnd();
                            stderr = proc.StandardError.ReadToEnd();
                            proc.WaitForExit(30_000);
                        }

                        // 只有进程确实被取消回调杀死时才抛出
                        if (wasKilled)
                        {
                            DebugLogger.Status($"ProbeAsync: 已取消 (exitCode={proc.ExitCode}, stdoutLen={stdout?.Length ?? 0})");
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        DebugLogger.Stdout($"probe-{Guid.NewGuid().ToString("N").Substring(0, 6)}", stdout);
                        if (!string.IsNullOrWhiteSpace(stderr))
                            DebugLogger.Stderr($"probe-{Guid.NewGuid().ToString("N").Substring(0, 6)}", stderr);

                        if (string.IsNullOrWhiteSpace(stdout))
                        {
                            DebugLogger.Error("ProbeAsync: rdown produced no output (empty stdout)");
                            throw new RdownException("rdown probe produced no output.");
                        }

                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        var resp = JsonSerializer.Deserialize<RdownResponse>(stdout, options);

                        if (resp == null || resp.Status == "error")
                        {
                            DebugLogger.Error($"ProbeAsync: probe failed — {(resp?.Message ?? "null response")}");
                            throw new RdownException(resp?.Message ?? "Probe failed.");
                        }

                        DebugLogger.Status($"ProbeAsync: success — id={resp.Id}, output={resp.Output}");

                        var id = resp.Id;
                        var output = resp.Output;

                        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(output))
                            throw new RdownException("Probe response missing id or output.");

                        // Read the generated .rdown.json file
                        var jsonFileName = $"{output}.{id}.rdown.json";
                        var jsonPath = Path.Combine(tempDir, jsonFileName);

                        if (!File.Exists(jsonPath))
                            throw new RdownException($"rdown.json not found: {jsonPath}");

                        var jsonContent = File.ReadAllBytes(jsonPath);

                        // 输出 task JSON 内容
                        try
                        {
                            var jsonText = Encoding.UTF8.GetString(jsonContent);
                            DebugLogger.Info($"rdown task JSON ({jsonFileName}):\n  {jsonText}");
                        }
                        catch { }

                        // 从 probe 响应中提取端点信息
                        List<EndpointInfo> endpoints = null;
                        if (resp.Endpoints != null)
                        {
                            try
                            {
                                endpoints = JsonSerializer.Deserialize<List<EndpointInfo>>(
                                    resp.Endpoints.Value.GetRawText(), options);
                            }
                            catch { }
                        }

                        // 从 probe 响应中提取响应头
                        Dictionary<string, string> responseHeaders = null;
                        if (resp.ResponseHeaders != null)
                        {
                            try
                            {
                                responseHeaders = JsonSerializer.Deserialize<Dictionary<string, string>>(
                                    resp.ResponseHeaders.Value.GetRawText(), options);
                            }
                            catch { }
                        }

                        var result = new ProbeResult(id, output, jsonContent, endpoints, responseHeaders);

                        // Store in concurrent dictionary
                        _probes[id] = result;

                        return result;
                    }
                });
            }
            finally
            {
                // Clean up temp directory
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// Try to get a stored probe result by task ID.
        /// </summary>
        public bool TryGetProbeResult(string taskId, out ProbeResult result)
        {
            return _probes.TryGetValue(taskId, out result);
        }

        /// <summary>
        /// Move the specified probe's .rdown.json to the target download directory
        /// and create a DownloadTask (Class 2).
        /// </summary>
        /// <param name="taskId">The task ID from ProbeAsync.</param>
        /// <param name="downloadDir">The user-chosen download directory (becomes rdown CWD).</param>
        /// <returns>The task ID, ready for AttachAsync + ResumeAsync.</returns>
        public async Task<string> MoveToFormalAsync(string taskId, string downloadDir, string configPath = null)
        {
            if (string.IsNullOrWhiteSpace(taskId))
                throw new ArgumentException("Task ID is required.", nameof(taskId));

            if (!_probes.TryRemove(taskId, out var probe))
                throw new InvalidOperationException($"Probe not found for task: {taskId}. Call ProbeAsync first.");

            if (string.IsNullOrWhiteSpace(downloadDir))
                throw new ArgumentException("Download directory is required.", nameof(downloadDir));

            if (!Directory.Exists(downloadDir))
                Directory.CreateDirectory(downloadDir);

            var jsonFileName = $"{probe.Output}.{probe.Id}.rdown.json";
            var destPath = Path.Combine(downloadDir, jsonFileName);

            // Write .rdown.json to destination
            await Task.Run(() => File.WriteAllBytes(destPath, probe.JsonContent));

            // 输出传出的 task JSON 内容
            try
            {
                var jsonText = Encoding.UTF8.GetString(probe.JsonContent);
                DebugLogger.Info($"MoveToFormalAsync: [{taskId}] 传出 task JSON → {destPath}:\n  {jsonText}");
            }
            catch { }

            // Create DownloadTask (Class 2)
            var task = new DownloadTask(
                probe.Id,
                probe.Output,
                downloadDir,
                _rdownExePath,
                configPath: configPath);

            task.OnStatusChanged += OnTaskStatusChanged;

            lock (_tasksLock)
            {
                _tasks[probe.Id] = task;
            }

            return probe.Id;
        }

        /// <summary>
        /// Discard a stored probe result without creating a task.
        /// </summary>
        public void ClearProbe(string taskId)
        {
            _probes.TryRemove(taskId, out _);
        }

        /// <summary>
        /// Discard all stored probe results.
        /// </summary>
        public void ClearAllProbes()
        {
            _probes.Clear();
        }

        // ── Class 2: Task management ───────────────────

        /// <summary>
        /// Attach to a download: spawn the stdin process.
        /// Must be called after MoveToFormalAsync and before ResumeAsync.
        /// </summary>
        public async Task AttachAsync(string taskId)
        {
            var task = GetTaskOrThrow(taskId);
            await task.AttachAsync();
        }

        /// <summary>
        /// Start / resume downloading.
        /// </summary>
        public async Task ResumeAsync(string taskId)
        {
            var task = GetTaskOrThrow(taskId);
            await task.ResumeAsync();
        }

        /// <summary>
        /// Pause the download.  Process exits; can be resumed later.
        /// </summary>
        public async Task PauseAsync(string taskId)
        {
            var task = GetTaskOrThrow(taskId);
            await task.PauseAsync();
        }

        /// <summary>
        /// Cancel the download.
        /// </summary>
        /// <param name="deleteResidual">If true (default), also delete partial file and .rdown.json.</param>
        public async Task CancelAsync(string taskId, bool deleteResidual = true)
        {
            var task = GetTaskOrThrow(taskId);
            await task.CancelAsync(deleteResidual);
        }

        /// <summary>
        /// Get basic download info.
        /// </summary>
        public async Task<DownloadInfo> InfoAsync(string taskId)
        {
            var task = GetTaskOrThrow(taskId);
            return await task.InfoAsync();
        }

        /// <summary>
        /// Get full download info (with segments).
        /// 若任务不在活跃 _tasks 中，则通过 TaskRecord 构造临时 DownloadTask 回退执行直接指令。
        /// </summary>
        public async Task<DownloadFullInfo> InfoAllAsync(string taskId)
        {
            var task = TryGetTask(taskId);
            if (task != null)
                return await task.InfoAllAsync();

            return await RunOnTempTaskAsync(taskId, t => t.InfoAllAsync());
        }

        /// <summary>
        /// Get the list of endpoints for a download.
        /// </summary>
        public async Task<List<EndpointInfo>> GetEndpointsAsync(string taskId)
        {
            // 从 probe 结果读取（Class 1）—— probe 时已从 stdout 解析
            if (_probes.TryGetValue(taskId, out var probe))
            {
                return probe.Endpoints ?? new List<EndpointInfo>();
            }

            // 回退到活跃任务（Class 2）
            DebugLogger.Status($"GetEndpointsAsync: [{taskId}] resolving from live task (Class 2)");
            var task = GetTaskOrThrow(taskId);
            return await task.GetEndpointsAsync();
        }

        /// <summary>
        /// Add an endpoint (IP + optional proxy) to a download.
        /// </summary>
        public async Task AddEndpointAsync(string taskId, string ip, string proxy = null)
        {
            var task = TryGetTask(taskId);
            if (task != null)
            {
                await task.AddEndpointAsync(ip, proxy);
                return;
            }

            await RunOnTempTaskAsync(taskId, t => t.AddEndpointAsync(ip, proxy));
        }

        /// <summary>
        /// Disable an endpoint.
        /// </summary>
        public async Task DisableEndpointAsync(string taskId, string ip)
        {
            var task = TryGetTask(taskId);
            if (task != null)
            {
                await task.DisableEndpointAsync(ip);
                return;
            }

            await RunOnTempTaskAsync(taskId, t => t.DisableEndpointAsync(ip));
        }

        /// <summary>
        /// Enable an endpoint.
        /// </summary>
        public async Task EnableEndpointAsync(string taskId, string ip)
        {
            var task = TryGetTask(taskId);
            if (task != null)
            {
                await task.EnableEndpointAsync(ip);
                return;
            }

            await RunOnTempTaskAsync(taskId, t => t.EnableEndpointAsync(ip));
        }

        /// <summary>
        /// Get the current status of a task.
        /// </summary>
        public DownloadTaskStatus GetStatus(string taskId)
        {
            return GetTaskOrThrow(taskId).Status;
        }

        /// <summary>
        /// Get the last full info (with segments) for a running task.
        /// Returns null if the task is not running or no info is available yet.
        /// </summary>
        public DownloadFullInfo GetLastFullInfo(string taskId)
        {
            lock (_tasksLock)
            {
                if (_tasks.TryGetValue(taskId, out var task))
                    return task.LastInfo;
                return null;
            }
        }

        /// <summary>
        /// Remove a completed/failed/cancelled task from internal tracking.
        /// Does NOT affect the rdown process (call CancelAsync first if needed).
        /// </summary>
        public bool RemoveTask(string taskId)
        {
            lock (_tasksLock)
            {
                if (_tasks.TryGetValue(taskId, out var task))
                {
                    task.OnStatusChanged -= OnTaskStatusChanged;
                    task.Dispose();
                    return _tasks.Remove(taskId);
                }
                return false;
            }
        }

        /// <summary>
        /// 检查任务是否已在内存中注册。
        /// </summary>
        public bool HasTask(string taskId)
        {
            lock (_tasksLock)
                return _tasks.ContainsKey(taskId);
        }

        /// <summary>
        /// 从持久化 TaskRecord 重新注册 DownloadTask（用于重启后恢复暂停的任务）。
        /// 幂等：已注册则跳过。
        /// </summary>
        public void RegisterPersistedTask(string taskId, string output, string downloadDir, string configPath)
        {
            lock (_tasksLock)
            {
                if (_tasks.ContainsKey(taskId))
                    return;

                var task = new DownloadTask(taskId, output, downloadDir, _rdownExePath, configPath);
                task.OnStatusChanged += OnTaskStatusChanged;
                _tasks[taskId] = task;
                DebugLogger.Status($"RegisterPersistedTask: [{taskId}] 已从持久化记录重新注册");
            }
        }

        /// <summary>
        /// Get all tracked task IDs.
        /// </summary>
        public string[] GetAllTaskIds()
        {
            lock (_tasksLock)
            {
                var ids = new string[_tasks.Count];
                _tasks.Keys.CopyTo(ids, 0);
                return ids;
            }
        }

        // ── internals ───────────────────────────────────

        private DownloadTask TryGetTask(string taskId)
        {
            lock (_tasksLock)
            {
                _tasks.TryGetValue(taskId, out var task);
                return task;
            }
        }

        private DownloadTask GetTaskOrThrow(string taskId)
        {
            var task = TryGetTask(taskId);
            if (task == null)
                throw new KeyNotFoundException($"Task not found: {taskId}");
            return task;
        }

        /// <summary>
        /// 任务不在活跃 _tasks 中时，从 TaskRecord 构造临时 DownloadTask 执行操作。
        /// 若 TaskRecord 也不存在则返回 default(T)。
        /// </summary>
        private async Task<T> RunOnTempTaskAsync<T>(string taskId, Func<DownloadTask, Task<T>> action)
        {
            var record = TaskRecordManager.Load(taskId);
            if (record == null)
                return default;

            var output = Path.Combine(record.DownloadDir, record.FileName ?? "");
            using (var tempTask = new DownloadTask(taskId, output, record.DownloadDir, _rdownExePath, record.ConfigPath))
            {
                return await action(tempTask);
            }
        }

        /// <summary>
        /// 无返回值的版本。
        /// </summary>
        private async Task RunOnTempTaskAsync(string taskId, Func<DownloadTask, Task> action)
        {
            var record = TaskRecordManager.Load(taskId);
            if (record == null)
                return;

            var output = Path.Combine(record.DownloadDir, record.FileName ?? "");
            using (var tempTask = new DownloadTask(taskId, output, record.DownloadDir, _rdownExePath, record.ConfigPath))
            {
                await action(tempTask);
            }
        }

        private void OnTaskStatusChanged(DownloadTask task, DownloadTaskStatus status, DownloadFullInfo info)
        {
            TaskStatusChanged?.Invoke(this, new TaskStatusChangedEventArgs(task.TaskId, status, info));
        }

        private string BuildArgs(string command, string[] args, string configPathOverride = null)
        {
            var sb = new StringBuilder("--json");
            if (!string.IsNullOrEmpty(configPathOverride) && File.Exists(configPathOverride))
            {
                sb.Append(" --config ");
                sb.Append(EscapeArg(configPathOverride));
            }
            sb.Append(' ');
            sb.Append(command);
            if (args != null)
            {
                foreach (var a in args)
                {
                    sb.Append(' ');
                    sb.Append(a);
                }
            }
            return sb.ToString();
        }

        private static string EscapeArg(string arg)
        {
            if (string.IsNullOrEmpty(arg))
                return "\"\"";
            if (arg.IndexOf(' ') >= 0 || arg.IndexOf('"') >= 0)
                return "\"" + arg.Replace("\"", "\\\"") + "\"";
            return arg;
        }

        /// <summary>
        /// 从 URL 中提取安全的输出文件名（去除查询参数，替换非法字符）。
        /// 用于 probe 时传给 rdown --output，避免 ? 等查询参数被当作文件名
        /// 导致 Windows ERROR_INVALID_NAME (os error 123)。
        /// 返回 null 表示无法提取有效文件名。
        /// </summary>
        private static string GetSafeOutputFromUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                // AbsolutePath 不包含 query string
                var path = uri.AbsolutePath.TrimEnd('/');
                var filename = path.Split('/').LastOrDefault();
                if (string.IsNullOrEmpty(filename))
                    return null;

                filename = Uri.UnescapeDataString(filename);

                // 替换 Windows 文件名中的非法字符
                var invalid = Path.GetInvalidFileNameChars();
                var chars = filename.ToCharArray();
                for (int i = 0; i < chars.Length; i++)
                {
                    if (invalid.Contains(chars[i]))
                        chars[i] = '_';
                }
                filename = new string(chars);

                // 去掉首尾空格和点（Windows 也不允许）
                filename = filename.Trim().TrimEnd('.');

                return string.IsNullOrEmpty(filename) ? null : filename;
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            _probes.Clear();
            lock (_tasksLock)
            {
                foreach (var kv in _tasks)
                {
                    kv.Value.OnStatusChanged -= OnTaskStatusChanged;
                    kv.Value.Dispose();
                }
                _tasks.Clear();
            }
        }
    }
}
