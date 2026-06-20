using System;
using System.Threading;
using System.Windows.Threading;

namespace RDownloaderGUI
{
    /// <summary>
    /// 全局调试日志器 —— 线程安全，首次调用时自动打开调试窗口。
    ///
    /// 使用方式：
    ///   DebugLogger.Enabled = true;   // 由 RDownloaderManager.DEBUG_MODE 控制
    ///   DebugLogger.Command("rdown --json download https://...");
    ///   DebugLogger.Stdin("{ \"command\": \"resume\", ... }");
    ///   DebugLogger.Stdout("{ \"status\": \"ok\", ... }");
    /// </summary>
    internal static class DebugLogger
    {
        private static readonly object _lock = new object();
        private static DebugLogWindow _window;
        private static bool _enabled;

        /// <summary>
        /// 启用/禁用日志。设为 true 时自动打开日志窗口。
        /// </summary>
        public static bool Enabled
        {
            get => _enabled;
            set
            {
                lock (_lock)
                {
                    if (_enabled == value) return;
                    _enabled = value;

                    if (_enabled)
                    {
                        EnsureWindow();
                    }
                    else
                    {
                        CloseWindow();
                    }
                }
            }
        }

        // ── 便捷方法 ──────────────────────────────────

        /// <summary>记录执行的命令行（EXE + 参数 + 工作目录）。</summary>
        public static void Command(string exe, string args, string cwd = null)
        {
            var msg = $"▶ 执行命令\n  EXE : {exe}\n  CWD : {cwd ?? "."}\n  ARGS: {args}";
            Log(msg, "Green");
        }

        /// <summary>记录通过 stdin 发送的 JSON。</summary>
        public static void Stdin(string taskId, string json)
        {
            Log($"📤 stdin → [{taskId}]\n  {json}", "Blue");
        }

        /// <summary>记录从 stdout 收到的内容。</summary>
        public static void Stdout(string taskId, string line)
        {
            Log($"📥 stdout ← [{taskId}]\n  {line}", "White");
        }

        /// <summary>记录从 stderr 收到的内容。</summary>
        public static void Stderr(string taskId, string line)
        {
            Log($"⚠ stderr ← [{taskId}]\n  {line}", "Yellow");
        }

        /// <summary>记录错误信息。</summary>
        public static void Error(string message)
        {
            Log($"❌ ERROR: {message}", "Red");
        }

        /// <summary>记录一般信息。</summary>
        public static void Info(string message)
        {
            Log($"ℹ {message}", "Gray");
        }

        /// <summary>记录进度/状态信息。</summary>
        public static void Status(string message)
        {
            Log($"● {message}", "Cyan");
        }

        /// <summary>分隔线。</summary>
        public static void Separator()
        {
            Log("──────────────────────────────────────────────", "Gray");
        }

        // ── 内部 ──────────────────────────────────────

        private static void Log(string message, string color)
        {
            if (!_enabled) return;

            var w = _window;
            if (w == null) return;

            w.AppendLine(message, color);
        }

        private static void EnsureWindow()
        {
            if (_window != null) return;

            // 必须在 UI 线程上创建 Window
            if (System.Windows.Application.Current?.Dispatcher != null)
            {
                var dispatcher = System.Windows.Application.Current.Dispatcher;
                if (dispatcher.CheckAccess())
                {
                    CreateWindow();
                }
                else
                {
                    dispatcher.Invoke((Action)CreateWindow);
                }
            }
        }

        private static void CreateWindow()
        {
            if (_window != null) return;
            _window = new DebugLogWindow();
            _window.Show();
            _window.AppendLine("══════ 调试日志已启动 ══════", "Yellow");
        }

        private static void CloseWindow()
        {
            if (_window == null) return;

            var w = _window;
            _window = null;

            if (System.Windows.Application.Current?.Dispatcher != null)
            {
                var dispatcher = System.Windows.Application.Current.Dispatcher;
                if (dispatcher.CheckAccess())
                {
                    w.Close();
                }
                else
                {
                    dispatcher.Invoke((Action)w.Close);
                }
            }
        }

        /// <summary>
        /// 窗口被用户关闭时由 DebugLogWindow 回调。
        /// 不清除 Enabled 标志 —— 下次日志调用时重新创建窗口。
        /// </summary>
        internal static void OnWindowClosed()
        {
            lock (_lock)
            {
                _window = null;
            }
        }
    }
}
