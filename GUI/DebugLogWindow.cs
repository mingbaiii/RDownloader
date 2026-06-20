using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace RDownloaderGUI
{
    /// <summary>
    /// 调试日志输出窗口 —— 以只读 RichTextBox 显示带时间戳的日志，
    /// 支持分色：命令（绿）、stdin（蓝）、stdout（白）、错误（红）。
    /// </summary>
    internal class DebugLogWindow : Window
    {
        private readonly RichTextBox _logBox;
        private readonly Paragraph _paragraph;
        private readonly FontFamily _fontFamily;
        private bool _isClosed;

        public DebugLogWindow()
        {
            Title = "RDownloader 调试日志";
            Width = 900;
            Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = true;
            Topmost = false;

            var fontUri = new Uri("file:///C:/Windows/Fonts/CascadiaMono.ttf");
            _fontFamily = new FontFamily(fontUri, "./#Cascadia Mono");

            _logBox = new RichTextBox
            {
                IsReadOnly = true,
                FontFamily = _fontFamily,
                FontSize = 13,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                Foreground = new SolidColorBrush(Color.FromRgb(212, 212, 212)),
                BorderThickness = new Thickness(0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            // 初始段落
            _paragraph = new Paragraph
            {
                Margin = new Thickness(8),
                LineHeight = 2,
                FontFamily = _fontFamily
            };
            _logBox.Document = new FlowDocument(_paragraph)
            {
                FontFamily = _fontFamily
            };

            Content = _logBox;

            Closed += (s, e) =>
            {
                _isClosed = true;
                DebugLogger.OnWindowClosed();
            };
        }

        /// <summary>
        /// 线程安全地追加一条彩色日志行。
        /// 支持 UI 线程直接写，也支持后台线程通过 Dispatcher 调度。
        /// </summary>
        public void AppendLine(string text, string color = "White")
        {
            if (_isClosed) return;

            if (!CheckAccess())
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action<string, string>(AppendLine), text, color);
                return;
            }

            Brush brush;
            switch (color)
            {
                case "Green": brush = new SolidColorBrush(Colors.LightGreen); break;
                case "Blue": brush = new SolidColorBrush(Colors.DeepSkyBlue); break;
                case "Red": brush = new SolidColorBrush(Colors.OrangeRed); break;
                case "Yellow": brush = new SolidColorBrush(Colors.Gold); break;
                case "Cyan": brush = new SolidColorBrush(Colors.Cyan); break;
                case "Gray": brush = new SolidColorBrush(Colors.Gray); break;
                default: brush = _logBox.Foreground; break;
            }

            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var run = new Run($"[{timestamp}] {text}\n")
            {
                FontFamily = _fontFamily,
                Foreground = brush
            };
            _paragraph.Inlines.Add(run);

            // 自动滚动到底部
            _logBox.ScrollToEnd();
        }
    }
}
