using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace RDownloaderGUI
{
    /// <summary>
    /// App.xaml 的交互逻辑
    /// </summary>
    public partial class App : Application
    {
        private static Mutex _mutex;
        private static EventWaitHandle _signal;

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, @"Global\RDownloaderGUI_SingleInstance", out var createdNew);
            if (!createdNew)
            {
                // 已有一个实例在运行 → 通知它激活窗口，然后退出
                _mutex = null;
                try
                {
                    using (var signal = EventWaitHandle.OpenExisting(@"Global\RDownloaderGUI_Activate"))
                    {
                        signal.Set();
                    }
                }
                catch { }
                Shutdown();
                return;
            }

            // 创建激活信号，后台监听
            _signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\RDownloaderGUI_Activate");

            Task.Run(() =>
            {
                while (_signal != null)
                {
                    try
                    {
                        if (_signal.WaitOne(Timeout.Infinite))
                        {
                            Current.Dispatcher.Invoke(() =>
                            {
                                var window = Current.MainWindow;
                                if (window != null)
                                {
                                    if (window.WindowState == WindowState.Minimized)
                                        window.WindowState = WindowState.Normal;
                                    window.Activate();
                                    window.Topmost = true;
                                    window.Topmost = false;
                                }
                            });
                        }
                    }
                    catch { break; }
                }
            });

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _signal?.Set(); // 唤醒后台线程退出
            _signal?.Dispose();
            _signal = null;
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
