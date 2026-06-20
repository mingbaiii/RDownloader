using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace RDownloaderGUI
{
    /// <summary>
    /// 任务记录的持久化管理器。
    /// 每个任务存储为一个独立的 JSON 文件：%AppData%/RDownloader/tasks/{TaskId}.json
    /// </summary>
    public static class TaskRecordManager
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>任务记录存储目录</summary>
        public static string TasksDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RDownloader",
            "tasks");

        private static string GetFilePath(string taskId)
        {
            return Path.Combine(TasksDir, $"{taskId}.json");
        }

        /// <summary>保存/覆盖一条任务记录。</summary>
        public static void Save(TaskRecord record)
        {
            try
            {
                if (!Directory.Exists(TasksDir))
                    Directory.CreateDirectory(TasksDir);

                var json = JsonSerializer.Serialize(record, JsonOptions);
                File.WriteAllText(GetFilePath(record.TaskId), json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"TaskRecordManager.Save({record.TaskId}) 失败: {ex.Message}");
            }
        }

        /// <summary>加载单条任务记录，不存在时返回 null。</summary>
        public static TaskRecord Load(string taskId)
        {
            try
            {
                var path = GetFilePath(taskId);
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path, Encoding.UTF8);
                return JsonSerializer.Deserialize<TaskRecord>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"TaskRecordManager.Load({taskId}) 失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>删除一条任务记录及其临时 config。</summary>
        public static void Delete(string taskId)
        {
            try
            {
                // 先加载记录以便清理 config
                var record = Load(taskId);
                if (record != null && !string.IsNullOrEmpty(record.ConfigPath))
                {
                    try
                    {
                        if (File.Exists(record.ConfigPath))
                            File.Delete(record.ConfigPath);
                        // 清理空的父目录
                        var dir = Path.GetDirectoryName(record.ConfigPath);
                        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                            Directory.Delete(dir);
                    }
                    catch { }
                }

                var path = GetFilePath(taskId);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"TaskRecordManager.Delete({taskId}) 失败: {ex.Message}");
            }
        }

        /// <summary>列出所有任务记录，按创建时间倒序。</summary>
        public static List<TaskRecord> ListAll()
        {
            var results = new List<TaskRecord>();
            try
            {
                if (!Directory.Exists(TasksDir))
                    return results;

                foreach (var file in Directory.GetFiles(TasksDir, "*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file, Encoding.UTF8);
                        var record = JsonSerializer.Deserialize<TaskRecord>(json, JsonOptions);
                        if (record != null)
                            results.Add(record);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"TaskRecordManager.ListAll 失败: {ex.Message}");
            }
            results.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
            return results;
        }

        /// <summary>更新任务的状态、进度和速度字段。</summary>
        public static void UpdateStatus(string taskId, string status, long downloaded, long totalSize, double speed)
        {
            try
            {
                var record = Load(taskId);
                if (record == null) return;

                record.Status = status;
                // 完成时强制置 100%，不受 > 0 守卫限制
                if (status == "Completed" && totalSize > 0)
                {
                    record.Downloaded = totalSize;
                }
                else if (downloaded > 0)
                {
                    record.Downloaded = downloaded;
                }
                if (totalSize > 0) record.TotalSize = totalSize;
                record.Speed = status == "Completed" ? 0 : speed;

                Save(record);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"TaskRecordManager.UpdateStatus({taskId}) 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清理不在内存中的旧任务（软件重启后）及其关联文件。
        /// 返回 true 表示任务已清理，false 表示记录不存在。
        /// </summary>
        public static bool CleanupStaleTask(string taskId, bool deleteResidual)
        {
            var record = Load(taskId);
            if (record == null) return false;

            if (deleteResidual && !string.IsNullOrEmpty(record.FileName) && record.FileName != "解析中...")
            {
                var filePath = Path.Combine(record.DownloadDir ?? "", record.FileName);
                try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
            }
            if (!string.IsNullOrEmpty(record.TaskJsonPath))
            {
                try { if (File.Exists(record.TaskJsonPath)) File.Delete(record.TaskJsonPath); } catch { }
            }
            // Delete 内部已清理 ConfigPath
            Delete(taskId);
            return true;
        }
    }
}
