using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks;

namespace RDownloaderGUI
{
    /// <summary>
    /// Aria2 兼容 JSON-RPC 服务器。
    /// 在 GUI 进程内启动 HTTP + WebSocket 服务，监听 localhost:{port}。
    /// 使用 HttpListener 处理 HTTP POST，使用 AcceptWebSocketAsync 升级 WebSocket。
    /// </summary>
    public class Aria2RpcServer : IDisposable
    {
        private readonly RDownloaderManager _manager;
        private readonly Aria2Methods _methods;
        private HttpListener _listener;
        private CancellationTokenSource _cts;

        // WebSocket 连接管理
        private readonly ConcurrentDictionary<Guid, WebSocket> _wsConnections
            = new ConcurrentDictionary<Guid, WebSocket>();

        /// <summary>
        /// 当 RPC 创建新下载任务时触发（taskId）。MainWindow 订阅以刷新任务列表。
        /// </summary>
        public event Action<string> OnTaskCreated;

        public int Port { get; private set; }
        public string Secret { get; private set; }
        public bool IsRunning => _listener?.IsListening ?? false;
        public int WsConnectionCount => _wsConnections.Count;

        public Aria2RpcServer(RDownloaderManager manager, int port = 6800, string secret = "")
        {
            _manager = manager;
            _methods = new Aria2Methods(manager);
            _methods.BroadcastEventAsync = BroadcastWsAsync;
            _methods.OnTaskCreated = taskId => OnTaskCreated?.Invoke(taskId);
            Port = port;
            Secret = secret ?? "";
        }

        // ── 启动 / 停止 ──

        public void Start()
        {
            if (_listener?.IsListening == true) return;

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");

            try { _listener.Start(); }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5)
            {
                // 权限不足，尝试注册 URL ACL
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "netsh",
                        Arguments = $"http add urlacl url=http://localhost:{Port}/ user={Environment.UserDomainName}\\{Environment.UserName}",
                        Verb = "runas",
                        UseShellExecute = true,
                        CreateNoWindow = true
                    };
                    System.Diagnostics.Process.Start(psi)?.WaitForExit(3000);
                    _listener.Start();
                }
                catch
                {
                    // 回退到 127.0.0.1 只监听
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                    _listener.Start();
                }
            }

            DebugLogger.Status($"Aria2RpcServer: 启动在 http://localhost:{Port}");
            _ = ListenLoopAsync(_cts.Token);
        }

        /// <summary>
        /// 热重启：使用新的端口和密钥。
        /// </summary>
        public void Restart(int port, string secret)
        {
            Stop();
            Port = port;
            Secret = secret ?? "";
            Start();
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _listener?.Stop();
                _listener?.Close();
            }
            catch { }

            // 关闭所有 WebSocket 连接
            foreach (var kv in _wsConnections)
            {
                try { kv.Value.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(1000); }
                catch { }
            }
            _wsConnections.Clear();

            DebugLogger.Status("Aria2RpcServer: 已停止");
        }

        // ── 主监听循环 ──

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = HandleContextAsync(context, ct); // fire-and-forget
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (OperationCanceledException) { break; }
            }
        }

        // ── 请求分发 ──

        private async Task HandleContextAsync(HttpListenerContext context, CancellationToken ct)
        {
            try
            {
                var request = context.Request;
                var response = context.Response;

                // ── CORS 预检头（所有响应都要加）──
                SetCorsHeaders(response);

                // OPTIONS 预检请求
                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 200;
                    response.Close();
                    return;
                }

                // 检查是否是 WebSocket 升级
                if (request.IsWebSocketRequest)
                {
                    await HandleWebSocketAsync(context, ct);
                    return;
                }

                // 只处理 POST
                if (request.HttpMethod != "POST")
                {
                    response.StatusCode = 405;
                    response.Close();
                    return;
                }

                // 读取请求体
                string body;
                using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync();
                }

                DebugLogger.Status($"Aria2Rpc POST: {body}");

                var trimmed = body.TrimStart();

                // ── 批量请求（JSON 数组）──
                if (trimmed.StartsWith("["))
                {
                    await HandleBatchAsync(context.Response, body, ct);
                    return;
                }

                // ── 单个请求 ──
                var rpcRequest = ParseSingleRequest(body);
                if (rpcRequest == null)
                {
                    await WriteResponseAsync(response, null,
                        Aria2Response.Fail(null, Aria2Error.ParseError));
                    return;
                }

                // 验证 secret
                if (!string.IsNullOrEmpty(Secret) && rpcRequest.Token != Secret)
                {
                    await WriteResponseAsync(response, rpcRequest.Id,
                        Aria2Response.Fail(rpcRequest.Id,
                            Aria2Error.GenericError("Unauthorized: invalid secret token")));
                    return;
                }

                // 通知（无 id）不返回响应
                if (rpcRequest.IsNotification)
                {
                    response.StatusCode = 204;
                    response.Close();
                    return;
                }

                // 分发方法
                var result = await DispatchAsync(rpcRequest);
                await WriteResponseAsync(response, rpcRequest.Id, result);
            }
            catch (Exception ex)
            {
                try
                {
                    await WriteResponseAsync(context.Response, null,
                        Aria2Response.Fail(null, Aria2Error.GenericError(ex.Message)));
                }
                catch { }
            }
        }

        // ── 批量请求处理 ──

        private Aria2Request ParseSingleRequest(string body)
        {
            try
            {
                return JsonSerializer.Deserialize<Aria2Request>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { return null; }
        }

        private async Task HandleBatchAsync(HttpListenerResponse response, string body, CancellationToken ct)
        {
            List<Aria2Request> requests;
            try
            {
                requests = JsonSerializer.Deserialize<List<Aria2Request>>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                await WriteResponseAsync(response, null,
                    Aria2Response.Fail(null, Aria2Error.ParseError));
                return;
            }

            if (requests == null || requests.Count == 0)
            {
                await WriteResponseAsync(response, null,
                    Aria2Response.Fail(null, Aria2Error.InvalidRequest));
                return;
            }

            var results = new List<Aria2Response>();
            foreach (var req in requests)
            {
                if (string.IsNullOrEmpty(req.Method))
                {
                    results.Add(Aria2Response.Fail(req.Id, Aria2Error.InvalidRequest));
                    continue;
                }

                // 验证 secret
                if (!string.IsNullOrEmpty(Secret) && req.Token != Secret)
                {
                    results.Add(Aria2Response.Fail(req.Id,
                        Aria2Error.GenericError("Unauthorized: invalid secret token")));
                    continue;
                }

                // 通知不产生响应项
                if (req.IsNotification)
                    continue;

                results.Add(await DispatchAsync(req));
            }

            // 全部是通知 → 空数组
            if (results.Count == 0)
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            await WriteBatchResponseAsync(response, results);
        }

        private static async Task WriteBatchResponseAsync(HttpListenerResponse response, List<Aria2Response> results)
        {
            try
            {
                response.ContentType = "application/json; charset=utf-8";
                response.StatusCode = 200;

                var options = new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                };
                var json = JsonSerializer.Serialize(results, options);
                var bytes = Encoding.UTF8.GetBytes(json);

                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                response.OutputStream.Close();
            }
            catch { }
        }

        // ── WebSocket 处理 ──

        private async Task HandleWebSocketAsync(HttpListenerContext context, CancellationToken ct)
        {
            try
            {
                var wsContext = await context.AcceptWebSocketAsync(null);
                var ws = wsContext.WebSocket;
                var connId = Guid.NewGuid();
                _wsConnections[connId] = ws;

                DebugLogger.Status($"Aria2RpcServer: WebSocket 已连接 ({connId.ToString("N").Substring(0, 6)}), 共 {_wsConnections.Count} 个连接");

                await WsReadLoopAsync(connId, ws, ct);
            }
            catch (Exception ex)
            {
                DebugLogger.Status($"Aria2RpcServer: WebSocket 升级失败: {ex.Message}");
            }
        }

        private async Task WsReadLoopAsync(Guid connId, WebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[4096];
            var messageBuffer = new MemoryStream();

            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        messageBuffer.Write(buffer, 0, result.Count);

                        if (result.EndOfMessage)
                        {
                            var text = Encoding.UTF8.GetString(messageBuffer.ToArray());
                            messageBuffer.SetLength(0);

                            DebugLogger.Status($"Aria2Rpc WS: {text}");

                            // ── 批量请求（JSON 数组）──
                            var trimmed = text.TrimStart();
                            if (trimmed.StartsWith("["))
                            {
                                try
                                {
                                    var requests = JsonSerializer.Deserialize<List<Aria2Request>>(text,
                                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                    if (requests != null)
                                    {
                                        var results = new List<Aria2Response>();
                                        foreach (var req in requests)
                                        {
                                            if (string.IsNullOrEmpty(req.Method)) continue;
                                            if (!string.IsNullOrEmpty(Secret) && req.Token != Secret) continue;
                                            if (req.IsNotification) continue;
                                            results.Add(await DispatchAsync(req));
                                        }
                                        if (results.Count > 0)
                                        {
                                            var batchJson = JsonSerializer.Serialize(results,
                                                new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
                                            await SendWsAsync(ws, batchJson);
                                        }
                                    }
                                }
                                catch { }
                                continue;
                            }

                            // ── 单个请求 ──
                            var rpcRequest = ParseSingleRequest(text);

                            if (rpcRequest != null && !string.IsNullOrEmpty(rpcRequest.Method))
                            {
                                // 验证 secret
                                if (!string.IsNullOrEmpty(Secret))
                                {
                                    var token = rpcRequest.Token;
                                    if (token != Secret)
                                    {
                                        await SendWsAsync(ws, JsonSerializer.Serialize(
                                            Aria2Response.Fail(rpcRequest.Id,
                                                Aria2Error.GenericError("Unauthorized: invalid secret token"))));
                                        continue;
                                    }
                                }

                                if (!rpcRequest.IsNotification)
                                {
                                    var response = await DispatchAsync(rpcRequest);
                                    await SendWsAsync(ws, JsonSerializer.Serialize(response,
                                        new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
                                }
                            }
                        }
                    }
                }
            }
            catch (WebSocketException) { }
            catch (OperationCanceledException) { }
            finally
            {
                _wsConnections.TryRemove(connId, out _);
                messageBuffer.Dispose();
                DebugLogger.Status($"Aria2RpcServer: WebSocket 已断开 ({connId.ToString("N").Substring(0, 6)})，剩余 {_wsConnections.Count} 个连接");
            }
        }

        // ── 方法分发 ──

        private async Task<Aria2Response> DispatchAsync(Aria2Request request)
        {
            try
            {
                var realParams = request.GetRealParams();
                object result = null;

                switch (request.Method)
                {
                    // 下载生命周期
                    case "aria2.addUri":
                        result = await _methods.AddUriAsync(realParams);
                        break;
                    case "aria2.pause":
                        result = await _methods.PauseAsync(realParams[0].GetString());
                        break;
                    case "aria2.pauseAll":
                        result = await _methods.PauseAllAsync();
                        break;
                    case "aria2.unpause":
                        result = await _methods.UnpauseAsync(realParams[0].GetString());
                        break;
                    case "aria2.unpauseAll":
                        result = await _methods.UnpauseAllAsync();
                        break;
                    case "aria2.remove":
                        result = await _methods.RemoveAsync(realParams[0].GetString());
                        break;
                    case "aria2.forceRemove":
                        result = await _methods.ForceRemoveAsync(realParams[0].GetString());
                        break;
                    case "aria2.removeDownloadResult":
                        result = _methods.RemoveDownloadResult(realParams[0].GetString());
                        break;

                    // 查询
                    case "aria2.tellStatus":
                        {
                            // tellStatus secret,gid[,keys]
                            var gid = realParams.Count > 0 ? realParams[0].GetString() : "";
                            result = await _methods.TellStatusAsync(gid);
                        }
                        break;
                    case "aria2.tellActive":
                        result = _methods.TellActive();
                        break;
                    case "aria2.tellWaiting":
                        {
                            int offset = 0, num = int.MaxValue;
                            if (realParams.Count > 0) int.TryParse(realParams[0].GetRawText(), out offset);
                            if (realParams.Count > 1) int.TryParse(realParams[1].GetRawText(), out num);
                            result = _methods.TellWaiting(offset, num);
                        }
                        break;
                    case "aria2.tellStopped":
                        {
                            int offset = 0, num = int.MaxValue;
                            if (realParams.Count > 0) int.TryParse(realParams[0].GetRawText(), out offset);
                            if (realParams.Count > 1) int.TryParse(realParams[1].GetRawText(), out num);
                            result = _methods.TellStopped(offset, num);
                        }
                        break;
                    case "aria2.getGlobalStat":
                        result = _methods.GetGlobalStat();
                        break;

                    // 配置
                    case "aria2.changeOption":
                        {
                            var optGid = realParams[0].GetString();
                            var opts = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(realParams[1].GetRawText());
                            result = _methods.ChangeOption(optGid, opts);
                        }
                        break;
                    case "aria2.getOption":
                        result = _methods.GetOption(realParams[0].GetString());
                        break;
                    case "aria2.changeGlobalOption":
                        {
                            var gOpts = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(realParams[0].GetRawText());
                            result = _methods.ChangeGlobalOption(gOpts);
                        }
                        break;
                    case "aria2.getGlobalOption":
                        result = _methods.GetGlobalOption();
                        break;

                    // 系统
                    case "aria2.getVersion":
                        result = _methods.GetVersion();
                        break;
                    case "aria2.getSessionInfo":
                        result = _methods.GetSessionInfo();
                        break;
                    case "aria2.shutdown":
                        result = _methods.Shutdown();
                        break;
                    case "system.multicall":
                        result = await _methods.MulticallAsync(realParams);
                        break;

                    // BT/种子方法 → "Not supported"
                    case "aria2.addTorrent":
                    case "aria2.addMetalink":
                    case "aria2.changeUri":
                    case "aria2.getFiles":
                    case "aria2.getPeers":
                    case "aria2.getUris":
                    case "aria2.getServers":
                        return Aria2Response.Fail(request.Id,
                            Aria2Error.NotSupported(request.Method));

                    default:
                        return Aria2Response.Fail(request.Id, Aria2Error.MethodNotFound);
                }

                return Aria2Response.Success(request.Id, result);
            }
            catch (KeyNotFoundException)
            {
                return Aria2Response.Fail(request.Id,
                    Aria2Error.TaskNotFound(request.GetRealParams().Count > 0
                        ? request.GetRealParams()[0].GetString() : "?"));
            }
            catch (NotSupportedException ex)
            {
                return Aria2Response.Fail(request.Id, Aria2Error.NotSupported(request.Method));
            }
            catch (ArgumentException ex)
            {
                return Aria2Response.Fail(request.Id, Aria2Error.GenericError(ex.Message));
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"Aria2RpcServer: 方法 {request.Method} 执行失败: {ex}");
                return Aria2Response.Fail(request.Id,
                    Aria2Error.GenericError(ex.Message));
            }
        }

        // ── CORS ──

        private static void SetCorsHeaders(HttpListenerResponse response)
        {
            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.Headers["Access-Control-Allow-Methods"] = "POST, GET, OPTIONS";
            response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Accept";
            response.Headers["Access-Control-Max-Age"] = "86400";
        }

        // ── 响应写入 ──

        private static async Task WriteResponseAsync(HttpListenerResponse response, JsonElement? id, Aria2Response rpcResponse)
        {
            try
            {
                response.ContentType = "application/json; charset=utf-8";
                response.StatusCode = 200;

                var options = new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                };

                var json = JsonSerializer.Serialize(rpcResponse, options);
                var bytes = Encoding.UTF8.GetBytes(json);

                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                response.OutputStream.Close();
            }
            catch { }
        }

        // ── WebSocket 广播 ──

        private async Task SendWsAsync(WebSocket ws, string json)
        {
            if (ws.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(json);
            try
            {
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch { }
        }

        private async Task BroadcastWsAsync(string json)
        {
            var dead = new System.Collections.Generic.List<Guid>();
            foreach (var kv in _wsConnections)
            {
                try
                {
                    await SendWsAsync(kv.Value, json);
                }
                catch
                {
                    dead.Add(kv.Key);
                }
            }
            foreach (var id in dead)
                _wsConnections.TryRemove(id, out _);
        }

        // ── 清理 ──

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}
