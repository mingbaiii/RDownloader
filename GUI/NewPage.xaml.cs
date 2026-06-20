using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ContentDialog = iNKORE.UI.WPF.Modern.Controls.ContentDialog;
using ContentDialogButton = iNKORE.UI.WPF.Modern.Controls.ContentDialogButton;
using ContentDialogResult = iNKORE.UI.WPF.Modern.Controls.ContentDialogResult;

namespace RDownloaderGUI
{
    /// <summary>
    /// NewPage.xaml 的交互逻辑
    /// </summary>
    public partial class NewPage : Page
    {
        private MainWindow parent;
        private bool _isProbing;

        public NewPage(MainWindow parent)
        {
            this.parent = parent;
            InitializeComponent();
        }

        /// <summary>
        /// "直接下载"按钮：立即创建任务记录并跳转下载页，后台解析完成后自动开始下载。
        /// </summary>
        private async void DirectDownload_Click(object sender, RoutedEventArgs e)
        {
            if (_isProbing) return;

            var urls = UrlTextBox.Text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => u.Trim())
                .Distinct()
                .ToList();

            if (urls.Count == 0)
            {
                await ShowMessageAsync("提示", "请输入至少一个下载链接。");
                return;
            }

            _isProbing = true;
            ParseButton.IsEnabled = false;
            DirectDownloadButton.IsEnabled = false;
            ProgressPanel.Visibility = Visibility.Visible;
            ProgressText.Text = $"正在创建任务... (0/{urls.Count})";

            try
            {
                await parent.StartDirectDownloadAsync(urls, new Progress<(int done, int total)>(p =>
                {
                    ProgressText.Text = $"正在创建任务... ({p.done}/{p.total})";
                }));
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"DirectDownload 失败: {ex.Message}");
            }
            finally
            {
                _isProbing = false;
                ParseButton.IsEnabled = true;
                DirectDownloadButton.IsEnabled = true;
                ProgressPanel.Visibility = Visibility.Collapsed;
                UrlTextBox.Clear();
            }
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            // 提取并验证 URL（每行一个）
            var urls = UrlTextBox.Text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => u.Trim())
                .Distinct()
                .ToList();

            if (urls.Count == 0)
            {
                _ = ShowMessageAsync("提示", "请输入至少一个下载链接。");
                return;
            }

            // 立即跳转到设置页，后台静默解析
            parent.NavigateToSettingsAndProbeInBackground(urls);

            // 清空输入框
            UrlTextBox.Clear();
        }

        private async System.Threading.Tasks.Task ShowMessageAsync(string title, string message)
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
    }
}
