using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace RDownloaderGUI
{
    /// <summary>
    /// Aria2 JSON-RPC 方法实现。
    /// 所有查询以 TaskRecordManager 为主数据源（与 GUI DownloadPage 一致），
    /// 所有操作完全镜像 GUI 按钮流程（PauseResume_Click / Cancel_Click）。
    /// </summary>
    public class Aria2Methods
    {
        private readonly RDownloaderManager _manager;

        public Func<string, Task> BroadcastEventAsync { get; set; }
        public Action<string> OnTaskCreated { get; set; }

        public Aria2Methods(RDownloaderManager manager)
        {
            _manager = manager;
        }

        // ═══════════════════════════════════════════════
        //  GID 转换
        // ═══════════════════════════════════════════════

        public static string ToGid(string rdownId) => rdownId + "00000000";
        public static string FromGid(string gid)
            => string.IsNullOrEmpty(gid) || gid.Length < 8 ? gid : gid.Substring(0, 8);

        // ═══════════════════════════════════════════════
        //  下载生命周期 — 镜像 GUI 按钮流程
        // ═══════════════════════════════════════════════

        /// <summary>
        /// aria2.addUri — 镜像 GUI StartDirectDownloadAsync 流程。
        /// </summary>
        public async Task<object> AddUriAsync(List<JsonElement> realParams)
        {
            if (realParams.Count == 0)
                throw new ArgumentException("URLs required");

            var urls = new List<string>();
            var urlsElement = realParams[0];
            if (urlsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var u in urlsElement.EnumerateArray())
                    if (u.ValueKind == JsonValueKind.String) urls.Add(u.GetString());
            }
            else if (urlsElement.ValueKind == JsonValueKind.String)
                urls.Add(urlsElement.GetString());

            if (urls.Count == 0)
                throw new ArgumentException("No valid URLs");

            var options = ParseOptions(realParams.Count > 1 ? realParams[1] : default);
            var url = urls[0];
            var downloadDir = options.TryGetValue("dir", out var d) && !string.IsNullOrWhiteSpace(d)
                ? d : GuiConfig.GetEffectiveDownloadDir();

            // ── 同 GUI: 先创建 Parsing 占位记录 ──
            var tempId = $"parsing-{Guid.NewGuid():N}";
            var tempRecord = new TaskRecord
            {
                TaskId = tempId, Url = url, FileName = "解析中...",
                DownloadDir = downloadDir, Status = "Parsing", CreatedAt = DateTime.Now
            };
            TaskRecordManager.Save(tempRecord);
            OnTaskCreated?.Invoke(tempId);

            string taskId = null;
            try
            {
                var configPath = BuildTempConfig(options, out var rdownConfig);
                var probeResult = await _manager.ProbeAsync(url, configPath);
                taskId = probeResult.Id;

                var formalConfigDir = Path.Combine(Path.GetTempPath(), "RDownloader", "config", taskId);
                Directory.CreateDirectory(formalConfigDir);
                var formalConfigPath = Path.Combine(formalConfigDir, "rdown_config.json");
                // 同 Probe 用的配置（含 RPC header/user-agent/proxy），不重新从 GUI 全局加载
                rdownConfig.Save(formalConfigPath);

                var record = new TaskRecord
                {
                    TaskId = taskId, Url = url, FileName = probeResult.Output,
                    DownloadDir = downloadDir,
                    TaskJsonPath = Path.Combine(downloadDir, $"{probeResult.Output}.{taskId}.rdown.json"),
                    ConfigPath = formalConfigPath, Status = "Running", CreatedAt = DateTime.Now,
                    Headers = rdownConfig.Headers, Proxy = rdownConfig.Proxy, DnsServers = rdownConfig.DnsServers
                };
                TaskRecordManager.Save(record);
                TaskRecordManager.Delete(tempId);
                OnTaskCreated?.Invoke(taskId);

                await _manager.MoveToFormalAsync(taskId, downloadDir, formalConfigPath);
                await _manager.AttachAsync(taskId);
                await _manager.ResumeAsync(taskId);

                var gid = ToGid(taskId);
                await BroadcastEventAsync(BuildEventJson("aria2.onDownloadStart", gid));
                return gid;
            }
            catch
            {
                TaskRecordManager.Delete(tempId);
                if (!string.IsNullOrEmpty(taskId))
                {
                    // UpdateStatus 只在记录已存在时生效，所以先创建一个
                    var failedRecord = new TaskRecord
                    {
                        TaskId = taskId, Url = url, FileName = "Failed",
                        DownloadDir = downloadDir, Status = "Failed", CreatedAt = DateTime.Now
                    };
                    TaskRecordManager.Save(failedRecord);
                    OnTaskCreated?.Invoke(taskId);
                }
                throw;
            }
        }

        /// <summary>
        /// aria2.pause — 镜像 GUI PauseResume_Click (Running 分支)。
        /// DownloadPage: await _parent.DownloadManager.PauseAsync(record.TaskId);
        /// </summary>
        public async Task<string> PauseAsync(string gid)
        {
            var taskId = FromGid(gid);
            // 同 GUI：任务必须在内存中且 Running
            if (!_manager.HasTask(taskId))
                throw new InvalidOperationException("Task is not active. Call unpause first to re-attach.");
            await _manager.PauseAsync(taskId);
            await BroadcastEventAsync(BuildEventJson("aria2.onDownloadPause", gid));
            return gid;
        }

        /// <summary>
        /// aria2.pauseAll — 遍历 TaskRecordManager (GUI 同源)。
        /// </summary>
        public async Task<string> PauseAllAsync()
        {
            foreach (var record in TaskRecordManager.ListAll())
            {
                if (record.Status == "Running" && _manager.HasTask(record.TaskId))
                {
                    try { await _manager.PauseAsync(record.TaskId); } catch { }
                }
            }
            return "OK";
        }

        /// <summary>
        /// aria2.unpause — 完全镜像 GUI PauseResume_Click (Paused 分支):
        ///   if (!HasTask) → RegisterPersistedTask
        ///   AttachAsync → ResumeAsync
        /// </summary>
        public async Task<string> UnpauseAsync(string gid)
        {
            var taskId = FromGid(gid);

            // 同 GUI lines 130-137
            if (!_manager.HasTask(taskId))
            {
                var record = TaskRecordManager.Load(taskId);
                if (record == null)
                    throw new KeyNotFoundException($"Task not found: {gid}");
                _manager.RegisterPersistedTask(taskId, record.FileName, record.DownloadDir, record.ConfigPath);
            }
            await _manager.AttachAsync(taskId);
            await _manager.ResumeAsync(taskId);

            await BroadcastEventAsync(BuildEventJson("aria2.onDownloadStart", gid));
            return gid;
        }

        /// <summary>
        /// aria2.unpauseAll — 遍历 TaskRecordManager 中 Paused 的任务。
        /// </summary>
        public async Task<string> UnpauseAllAsync()
        {
            foreach (var record in TaskRecordManager.ListAll())
            {
                if (record.Status == "Paused")
                {
                    try
                    {
                        if (!_manager.HasTask(record.TaskId))
                            _manager.RegisterPersistedTask(record.TaskId, record.FileName, record.DownloadDir, record.ConfigPath);
                        await _manager.AttachAsync(record.TaskId);
                        await _manager.ResumeAsync(record.TaskId);
                    }
                    catch { }
                }
            }
            return "OK";
        }

        /// <summary>
        /// aria2.remove — 同 GUI CancelAsync(deleteResidual: false)。
        /// 如果任务不在内存中（软件重启后），直接清理文件 + TaskRecord。
        /// </summary>
        public async Task<string> RemoveAsync(string gid)
        {
            var taskId = FromGid(gid);

            if (_manager.HasTask(taskId))
            {
                await _manager.CancelAsync(taskId, deleteResidual: false);
            }
            else
            {
                // 重启后任务不在内存，模拟 CancelAsync 的清理
                CancelStaleTask(taskId, deleteResidual: false);
            }

            await BroadcastEventAsync(BuildEventJson("aria2.onDownloadStop", gid));
            return gid;
        }

        /// <summary>
        /// aria2.forceRemove — 同 GUI CancelAsync(deleteResidual: true)。
        /// </summary>
        public async Task<string> ForceRemoveAsync(string gid)
        {
            var taskId = FromGid(gid);

            if (_manager.HasTask(taskId))
            {
                await _manager.CancelAsync(taskId, deleteResidual: true);
            }
            else
            {
                CancelStaleTask(taskId, deleteResidual: true);
            }

            await BroadcastEventAsync(BuildEventJson("aria2.onDownloadStop", gid));
            return gid;
        }

        /// <summary>
        /// 清理不在内存中的旧任务（重启后）。
        /// </summary>
        private void CancelStaleTask(string taskId, bool deleteResidual)
        {
            if (TaskRecordManager.CleanupStaleTask(taskId, deleteResidual))
                OnTaskCreated?.Invoke(taskId); // 通知 GUI 刷新（从列表移除）
        }

        /// <summary>
        /// aria2.removeDownloadResult — 清除已完成/失败/已取消记录。
        /// </summary>
        public string RemoveDownloadResult(string gid)
        {
            var taskId = FromGid(gid);
            var record = TaskRecordManager.Load(taskId);
            if (record == null) return "OK";

            var s = record.Status;
            if (s == "Completed" || s == "Failed" || s == "Cancelled")
            {
                if (_manager.HasTask(taskId))
                    _manager.RemoveTask(taskId);
                TaskRecordManager.Delete(taskId);
                OnTaskCreated?.Invoke(taskId); // 通知 GUI 刷新（从列表移除）
            }
            return "OK";
        }

        // ═══════════════════════════════════════════════
        //  查询 — 以 TaskRecordManager 为主数据源（与 GUI DownloadPage 同）
        //  内存中的活跃任务用 live info 覆盖进度/速度
        // ═══════════════════════════════════════════════

        /// <summary>
        /// aria2.tellStatus — 单任务详情。
        /// 优先取内存中 live info，否则从 TaskRecord + InfoAllAsync(temp) 取值。
        /// </summary>
        public async Task<Aria2TaskInfo> TellStatusAsync(string gid)
        {
            var taskId = FromGid(gid);
            var record = TaskRecordManager.Load(taskId);
            if (record == null)
                throw new KeyNotFoundException($"Task not found: {gid}");

            DownloadFullInfo info = null;

            if (_manager.HasTask(taskId))
            {
                // 内存中有 → 取 live info
                info = _manager.GetLastFullInfo(taskId);
                if (info == null)
                {
                    try { info = await _manager.InfoAllAsync(taskId); } catch { }
                }
            }

            return BuildTaskInfoFromRecord(record, info);
        }

        /// <summary>
        /// tellActive — TaskRecordManager 中 status=Running 的任务。
        /// </summary>
        public List<Aria2TaskInfo> TellActive()
        {
            var result = new List<Aria2TaskInfo>();
            foreach (var record in TaskRecordManager.ListAll())
            {
                if (record.Status == "Running")
                {
                    DownloadFullInfo info = null;
                    if (_manager.HasTask(record.TaskId))
                        info = _manager.GetLastFullInfo(record.TaskId);
                    result.Add(BuildTaskInfoFromRecord(record, info));
                }
            }
            return result;
        }

        /// <summary>
        /// tellWaiting — TaskRecordManager 中 status=Parsing/Idle 的任务。
        /// </summary>
        public List<Aria2TaskInfo> TellWaiting(int offset = 0, int num = int.MaxValue)
        {
            var result = new List<Aria2TaskInfo>();
            foreach (var record in TaskRecordManager.ListAll())
            {
                if (record.Status == "Parsing" || record.Status == "Idle")
                {
                    DownloadFullInfo info = null;
                    if (_manager.HasTask(record.TaskId))
                        info = _manager.GetLastFullInfo(record.TaskId);
                    result.Add(BuildTaskInfoFromRecord(record, info));
                }
            }
            return result.Skip(offset).Take(num).ToList();
        }

        /// <summary>
        /// tellStopped — TaskRecordManager 中 Paused/Completed/Failed/Cancelled。
        /// </summary>
        public List<Aria2TaskInfo> TellStopped(int offset = 0, int num = int.MaxValue)
        {
            var result = new List<Aria2TaskInfo>();
            foreach (var record in TaskRecordManager.ListAll())
            {
                if (record.Status == "Paused" || record.Status == "Completed"
                    || record.Status == "Failed" || record.Status == "Cancelled")
                {
                    DownloadFullInfo info = null;
                    if (_manager.HasTask(record.TaskId))
                        info = _manager.GetLastFullInfo(record.TaskId);
                    result.Add(BuildTaskInfoFromRecord(record, info));
                }
            }
            return result.Skip(offset).Take(num).ToList();
        }

        /// <summary>
        /// getGlobalStat — 基于 TaskRecordManager 统计。
        /// </summary>
        public Aria2GlobalStat GetGlobalStat()
        {
            int active = 0, waiting = 0, stopped = 0;
            long speed = 0;

            foreach (var record in TaskRecordManager.ListAll())
            {
                switch (record.Status)
                {
                    case "Running":
                        active++;
                        if (_manager.HasTask(record.TaskId))
                            speed += (long)(_manager.GetLastFullInfo(record.TaskId)?.Speed ?? 0);
                        else
                            speed += (long)record.Speed;
                        break;
                    case "Parsing":
                    case "Idle":
                        waiting++;
                        break;
                    case "Paused":
                    case "Completed":
                    case "Failed":
                    case "Cancelled":
                        stopped++;
                        break;
                }
            }

            return new Aria2GlobalStat
            {
                DownloadSpeed = speed,
                UploadSpeed = 0,
                NumActive = active,
                NumWaiting = waiting,
                NumStopped = stopped,
                NumStoppedTotal = stopped
            };
        }

        // ═══════════════════════════════════════════════
        //  配置
        // ═══════════════════════════════════════════════

        public string ChangeOption(string gid, Dictionary<string, string> options)
        {
            DebugLogger.Status($"Aria2 changeOption [{FromGid(gid)}]: {string.Join(", ", options.Select(kv => $"{kv.Key}={kv.Value}"))}");
            return "OK";
        }

        public Aria2Options GetOption(string gid)
        {
            var record = TaskRecordManager.Load(FromGid(gid));
            var config = GuiConfig.Load();
            var options = new Aria2Options();
            options["dir"] = record?.DownloadDir ?? GuiConfig.GetEffectiveDownloadDir();
            options["split"] = config.TotalConnections.ToString();
            options["max-connection-per-server"] = config.TotalConnections.ToString();
            options["check-certificate"] = "false";
            return options;
        }

        public string ChangeGlobalOption(Dictionary<string, string> options)
        {
            var config = GuiConfig.Load();
            foreach (var kv in options)
            {
                switch (kv.Key.ToLowerInvariant())
                {
                    case "dir": config.DownloadPath = kv.Value; break;
                    case "split":
                        if (int.TryParse(kv.Value, out var s) && s > 0) config.TotalConnections = s;
                        break;
                }
            }
            config.Save();
            return "OK";
        }

        public Aria2Options GetGlobalOption()
        {
            var config = GuiConfig.Load();
            return new Aria2Options
            {
                ["dir"] = GuiConfig.GetEffectiveDownloadDir(),
                ["split"] = config.TotalConnections.ToString(),
                ["max-connection-per-server"] = config.TotalConnections.ToString(),
                ["max-download-limit"] = "0",
                ["check-certificate"] = "false"
            };
        }

        // ═══════════════════════════════════════════════
        //  系统
        // ═══════════════════════════════════════════════

        public Aria2Version GetVersion() => new Aria2Version();

        public Aria2SessionInfo GetSessionInfo()
            => new Aria2SessionInfo { SessionId = $"rdownloader-{Guid.NewGuid():N}".Substring(0, 32) };

        public string Shutdown() => "OK";

        public async Task<List<object>> MulticallAsync(List<JsonElement> realParams)
        {
            var results = new List<object>();
            if (realParams.Count == 0) return results;
            var calls = realParams[0];
            if (calls.ValueKind != JsonValueKind.Array) return results;

            foreach (var call in calls.EnumerateArray())
            {
                try
                {
                    var methodName = call.GetProperty("methodName").GetString()
                        ?? call.GetProperty("method").GetString();
                    var callParams = call.TryGetProperty("params", out var p)
                        ? p.EnumerateArray().Select(e => e).ToList()
                        : new List<JsonElement>();
                    results.Add(new List<object> { await DispatchMethod(methodName, callParams) });
                }
                catch (Exception ex) { results.Add(new { error = new { code = -1, message = ex.Message } }); }
            }
            return results;
        }

        // ═══════════════════════════════════════════════
        //  辅助
        // ═══════════════════════════════════════════════

        /// <summary>
        /// 从 TaskRecord + 可选的 live info 构建 Aria2TaskInfo。
        /// </summary>
        private Aria2TaskInfo BuildTaskInfoFromRecord(TaskRecord record, DownloadFullInfo liveInfo)
        {
            var ariaStatus = RecordStatusToAria2(record.Status);
            var url = record.Url ?? "";
            var output = record.FileName ?? "";
            var dir = record.DownloadDir ?? "";

            var totalLength = liveInfo?.FileSize ?? record.TotalSize;
            var completedLength = liveInfo?.Downloaded ?? record.Downloaded;
            var speed = liveInfo?.Speed ?? record.Speed;
            var connections = liveInfo?.Connections ?? 0;

            return new Aria2TaskInfo
            {
                Gid = ToGid(record.TaskId),
                Status = ariaStatus,
                TotalLength = totalLength,
                CompletedLength = completedLength,
                DownloadSpeed = (long)speed,
                UploadSpeed = 0,
                UploadLength = 0,
                Connections = connections,
                Dir = dir,
                Files = new List<Aria2File>
                {
                    new Aria2File
                    {
                        Index = 1,
                        Path = Path.Combine(dir, output),
                        Length = totalLength,
                        CompletedLength = completedLength,
                        Selected = true,
                        Uris = new List<Aria2Uri> { new Aria2Uri { Uri = url, Status = "used" } }
                    }
                },
                ErrorCode = record.Status == "Failed" ? 1 : 0,
                ErrorMessage = record.Status == "Failed" ? "Download failed" : "",
                Bitfield = "",
                InfoHash = ""
            };
        }

        private static string RecordStatusToAria2(string recordStatus)
        {
            switch (recordStatus)
            {
                case "Running": return "active";
                case "Parsing":
                case "Idle": return "waiting";
                case "Paused": return "paused";
                case "Completed": return "complete";
                case "Failed": return "error";
                case "Cancelled": return "removed";
                default: return "error";
            }
        }

        private static Dictionary<string, string> ParseOptions(JsonElement el)
        {
            var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (el.ValueKind == JsonValueKind.Object)
                foreach (var p in el.EnumerateObject()) opts[p.Name] = p.Value.ToString();
            return opts;
        }

        private static string BuildTempConfig(Dictionary<string, string> options, out RdownConfig config)
        {
            config = GuiConfig.Load().ToRdownConfig();

            // header — Aria2 支持字符串或数组
            if (options.TryGetValue("header", out var h))
            {
                // 尝试作为 JSON 数组解析 ["Name: Value", ...]
                var headerList = TryParseStringArray(h);
                if (headerList != null)
                {
                    foreach (var item in headerList)
                        ApplyHeaderString(item, config.Headers);
                }
                else
                {
                    ApplyHeaderString(h, config.Headers);
                }
            }
            // user-agent — Aria2 独立选项，映射到 User-Agent header
            if (options.TryGetValue("user-agent", out var ua) && !string.IsNullOrWhiteSpace(ua))
            {
                config.Headers["User-Agent"] = ua;
            }
            // referer — Aria2 独立选项，映射到 Referer header
            if (options.TryGetValue("referer", out var refVal) && !string.IsNullOrWhiteSpace(refVal))
            {
                config.Headers["Referer"] = refVal;
            }
            if (options.TryGetValue("all-proxy", out var ap)) config.Proxy["*"] = ap;

            var dir = Path.Combine(Path.GetTempPath(), "RDownloader", "rpc");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"rpc_config_{Guid.NewGuid():N}.json");
            config.Save(path);
            return path;
        }

        private static void ApplyHeaderString(string headerStr, Dictionary<string, string> headers)
        {
            if (string.IsNullOrWhiteSpace(headerStr)) return;
            var parts = headerStr.Split(new[] { ':', '=' }, 2);
            if (parts.Length == 2) headers[parts[0].Trim()] = parts[1].Trim();
        }

        /// <summary>尝试将字符串解析为 JSON 字符串数组，失败返回 null。</summary>
        private static List<string> TryParseStringArray(string raw)
        {
            try
            {
                var el = JsonSerializer.Deserialize<JsonElement>(raw);
                if (el.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var item in el.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString());
                    return list;
                }
            }
            catch { }
            return null;
        }

        public static string BuildEventJson(string eventMethod, string gid)
            => JsonSerializer.Serialize(new { jsonrpc = "2.0", method = eventMethod, @params = new[] { new { gid } } });

        internal async Task<object> DispatchMethod(string method, List<JsonElement> realParams)
        {
            switch (method)
            {
                case "aria2.addUri": return await AddUriAsync(realParams);
                case "aria2.pause": return await PauseAsync(Param0(realParams));
                case "aria2.pauseAll": return await PauseAllAsync();
                case "aria2.unpause": return await UnpauseAsync(Param0(realParams));
                case "aria2.unpauseAll": return await UnpauseAllAsync();
                case "aria2.remove": return await RemoveAsync(Param0(realParams));
                case "aria2.forceRemove": return await ForceRemoveAsync(Param0(realParams));
                case "aria2.removeDownloadResult": return RemoveDownloadResult(Param0(realParams));
                case "aria2.tellStatus":
                    var tsGid = realParams.Count > 0 ? realParams[0].GetString() : "";
                    return await TellStatusAsync(tsGid);
                case "aria2.tellActive": return TellActive();
                case "aria2.tellWaiting":
                    return TellWaiting(IntParam(realParams, 0), IntParam(realParams, 1, int.MaxValue));
                case "aria2.tellStopped":
                    return TellStopped(IntParam(realParams, 0), IntParam(realParams, 1, int.MaxValue));
                case "aria2.getGlobalStat": return GetGlobalStat();
                case "aria2.changeOption":
                    return ChangeOption(Param0(realParams),
                        JsonSerializer.Deserialize<Dictionary<string, string>>(realParams[1].GetRawText()));
                case "aria2.getOption": return GetOption(Param0(realParams));
                case "aria2.changeGlobalOption":
                    return ChangeGlobalOption(JsonSerializer.Deserialize<Dictionary<string, string>>(realParams[0].GetRawText()));
                case "aria2.getGlobalOption": return GetGlobalOption();
                case "aria2.getVersion": return GetVersion();
                case "aria2.getSessionInfo": return GetSessionInfo();
                case "aria2.shutdown": return Shutdown();
                case "system.multicall": return await MulticallAsync(realParams);
                // BT methods
                case "aria2.addTorrent":
                case "aria2.addMetalink":
                case "aria2.changeUri":
                case "aria2.getFiles":
                case "aria2.getPeers":
                case "aria2.getUris":
                case "aria2.getServers":
                    throw new NotSupportedException($"Not supported: {method}");
                default:
                    throw new NotSupportedException($"Method not found: {method}");
            }
        }

        private static string Param0(List<JsonElement> p) => p.Count > 0 ? p[0].GetString() : "";
        private static int IntParam(List<JsonElement> p, int idx, int def = 0)
        {
            if (idx >= p.Count) return def;
            return int.TryParse(p[idx].GetRawText(), out var v) ? v : def;
        }
    }
}
