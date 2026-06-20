using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RDownloaderGUI
{
    // ═══════════════════════════════════════════════════
    //  JSON-RPC 2.0 Request / Response
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Aria2 JSON-RPC 2.0 请求。
    /// params 用 JsonElement 存储，因为不同方法的 params 结构差异大。
    /// </summary>
    public class Aria2Request
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; }

        [JsonPropertyName("id")]
        public JsonElement? Id { get; set; }  // null = notification

        [JsonPropertyName("method")]
        public string Method { get; set; }

        [JsonPropertyName("params")]
        public JsonElement? Params { get; set; }

        /// <summary>
        /// 是否是一次通知（没有 id，不返回响应）。
        /// </summary>
        [JsonIgnore]
        public bool IsNotification => Id == null || Id.Value.ValueKind == JsonValueKind.Null;

        /// <summary>
        /// 提取 secret token。Aria2 约定 params[0] = "token:xxx"。
        /// 返回 token 字符串，无 token 时返回 null。
        /// </summary>
        [JsonIgnore]
        public string Token
        {
            get
            {
                if (Params == null || Params.Value.ValueKind != JsonValueKind.Array)
                    return null;
                var arr = Params.Value.EnumerateArray();
                if (!arr.MoveNext())
                    return null;
                var first = arr.Current;
                if (first.ValueKind == JsonValueKind.String)
                {
                    var s = first.GetString();
                    if (s != null && s.StartsWith("token:"))
                        return s.Substring(6);
                }
                return null;
            }
        }

        /// <summary>
        /// 从 params 中提取除去 token 后的实际参数。
        /// Aria2 约定如果第一个参数是 "token:xxx"，则真正的 params 从 [1] 开始。
        /// 返回新的 JsonElement 数组。
        /// </summary>
        public List<JsonElement> GetRealParams()
        {
            var result = new List<JsonElement>();
            if (Params == null || Params.Value.ValueKind != JsonValueKind.Array)
                return result;

            var arr = Params.Value.EnumerateArray();
            bool first = true;
            foreach (var item in arr)
            {
                if (first)
                {
                    first = false;
                    // 跳过 token:xxx
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var s = item.GetString();
                        if (s != null && s.StartsWith("token:"))
                            continue;
                    }
                }
                result.Add(item);
            }
            return result;
        }
    }

    /// <summary>
    /// JSON-RPC 2.0 错误对象。
    /// </summary>
    public class Aria2Error
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        public Aria2Error() { }

        public Aria2Error(int code, string message)
        {
            Code = code;
            Message = message;
        }

        // ── 标准 JSON-RPC 错误码 ──
        public static readonly Aria2Error ParseError = new Aria2Error(-32700, "Parse error");
        public static readonly Aria2Error InvalidRequest = new Aria2Error(-32600, "Invalid Request");
        public static readonly Aria2Error MethodNotFound = new Aria2Error(-32601, "Method not found");
        public static readonly Aria2Error InvalidParams = new Aria2Error(-32602, "Invalid params");
        public static readonly Aria2Error InternalError = new Aria2Error(-32603, "Internal error");

        public static Aria2Error NotSupported(string method) =>
            new Aria2Error(-32000, $"Not supported: {method}");

        public static Aria2Error TaskNotFound(string gid) =>
            new Aria2Error(1, $"GID {gid} is not found");

        public static Aria2Error GenericError(string msg) =>
            new Aria2Error(-1, msg);
    }

    /// <summary>
    /// JSON-RPC 2.0 响应。
    /// </summary>
    public class Aria2Response
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public JsonElement? Id { get; set; }

        [JsonPropertyName("result")]
        public object Result { get; set; }

        [JsonPropertyName("error")]
        public Aria2Error Error { get; set; }

        public static Aria2Response Success(JsonElement? id, object result) => new Aria2Response
        {
            Id = id,
            Result = result
        };

        public static Aria2Response Fail(JsonElement? id, Aria2Error error) => new Aria2Response
        {
            Id = id,
            Error = error
        };
    }

    // ═══════════════════════════════════════════════════
    //  Aria2 任务状态 / 信息 struct
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Aria2 任务状态，映射到 rdown 状态。
    /// </summary>
    public static class Aria2Status
    {
        public const string Active = "active";       // rdown: downloading
        public const string Waiting = "waiting";     // rdown: pending
        public const string Paused = "paused";       // rdown: paused
        public const string Complete = "complete";   // rdown: completed
        public const string Error = "error";         // rdown: failed
        public const string Removed = "removed";     // rdown: cancelled

        /// <summary>
        /// rdown 状态字符串 → Aria2 状态字符串
        /// </summary>
        public static string FromRdown(string rdownStatus)
        {
            switch (rdownStatus)
            {
                case "downloading": return Active;
                case "pending": return Waiting;
                case "paused": return Paused;
                case "completed": return Complete;
                case "failed": return Error;
                case "cancelled": return Removed;
                default: return Error;
            }
        }

        /// <summary>
        /// rdown DownloadTaskStatus → Aria2 状态字符串
        /// </summary>
        public static string FromRdownTaskStatus(DownloadTaskStatus status)
        {
            switch (status)
            {
                case DownloadTaskStatus.Running: return Active;
                case DownloadTaskStatus.Parsing:
                case DownloadTaskStatus.Idle: return Waiting;
                case DownloadTaskStatus.Paused: return Paused;
                case DownloadTaskStatus.Completed: return Complete;
                case DownloadTaskStatus.Failed: return Error;
                case DownloadTaskStatus.Cancelled: return Removed;
                default: return Error;
            }
        }
    }

    /// <summary>
    /// aria2.tellStatus / tellActive / tellWaiting / tellStopped 返回的任务详情。
    /// </summary>
    public class Aria2TaskInfo
    {
        [JsonPropertyName("gid")]
        public string Gid { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("totalLength")]
        public long TotalLength { get; set; }

        [JsonPropertyName("completedLength")]
        public long CompletedLength { get; set; }

        [JsonPropertyName("downloadSpeed")]
        public long DownloadSpeed { get; set; }

        [JsonPropertyName("uploadSpeed")]
        public long UploadSpeed { get; set; }

        [JsonPropertyName("files")]
        public List<Aria2File> Files { get; set; }

        [JsonPropertyName("dir")]
        public string Dir { get; set; }

        // ── 可选字段 ──
        [JsonPropertyName("uploadLength")]
        public long UploadLength { get; set; }

        [JsonPropertyName("connections")]
        public int Connections { get; set; }

        [JsonPropertyName("errorCode")]
        public int ErrorCode { get; set; }

        [JsonPropertyName("errorMessage")]
        public string ErrorMessage { get; set; }

        [JsonPropertyName("bitfield")]
        public string Bitfield { get; set; }

        [JsonPropertyName("infoHash")]
        public string InfoHash { get; set; }

        [JsonPropertyName("numSeeders")]
        public int NumSeeders { get; set; }

        [JsonPropertyName("pieceLength")]
        public long PieceLength { get; set; }

        [JsonPropertyName("seeder")]
        public bool Seeder { get; set; }
    }

    /// <summary>
    /// Aria2 文件信息。
    /// </summary>
    public class Aria2File
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("path")]
        public string Path { get; set; }

        [JsonPropertyName("length")]
        public long Length { get; set; }

        [JsonPropertyName("completedLength")]
        public long CompletedLength { get; set; }

        [JsonPropertyName("selected")]
        public bool Selected { get; set; }

        [JsonPropertyName("uris")]
        public List<Aria2Uri> Uris { get; set; }
    }

    /// <summary>
    /// Aria2 URI 信息。
    /// </summary>
    public class Aria2Uri
    {
        [JsonPropertyName("uri")]
        public string Uri { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "used";
    }

    /// <summary>
    /// aria2.getGlobalStat 返回的统计信息。
    /// </summary>
    public class Aria2GlobalStat
    {
        [JsonPropertyName("downloadSpeed")]
        public long DownloadSpeed { get; set; }

        [JsonPropertyName("uploadSpeed")]
        public long UploadSpeed { get; set; }

        [JsonPropertyName("numActive")]
        public int NumActive { get; set; }

        [JsonPropertyName("numWaiting")]
        public int NumWaiting { get; set; }

        [JsonPropertyName("numStopped")]
        public int NumStopped { get; set; }

        [JsonPropertyName("numStoppedTotal")]
        public int NumStoppedTotal { get; set; }
    }

    /// <summary>
    /// aria2.getVersion 返回的版本信息。
    /// </summary>
    public class Aria2Version
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "rdownloader-aria2-compat-1.0";

        [JsonPropertyName("enabledFeatures")]
        public List<string> EnabledFeatures { get; set; } = new List<string>
        {
            "Async DNS",
            "BitTorrent",    // 声称支持以通过客户端检查，实际返回空
            "Firefox3 Cookie",
            "GZip",
            "HTTPS",
            "Message Digest",
            "Metalink",      // 声称支持，实际返回空
            "XML-RPC",
            "Multi-File",    // 声称支持
        };
    }

    /// <summary>
    /// aria2.getSessionInfo 返回的会话信息。
    /// </summary>
    public class Aria2SessionInfo
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; }
    }

    /// <summary>
    /// Aria2 全局/任务选项字典。
    /// </summary>
    public class Aria2Options : Dictionary<string, string>
    {
        public Aria2Options() : base(StringComparer.OrdinalIgnoreCase) { }

        public Aria2Options(IDictionary<string, string> dict)
            : base(dict, StringComparer.OrdinalIgnoreCase) { }
    }
}
