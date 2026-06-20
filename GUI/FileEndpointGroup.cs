using System.Collections.ObjectModel;

namespace RDownloaderGUI
{
    /// <summary>
    /// 一个文件的端点分组，包含文件名、链接和该文件对应的所有端点。
    /// 用于在 SelectEndpointPage 中按文件分组显示端点。
    /// </summary>
    public class FileEndpointGroup
    {
        /// <summary>
        /// 文件名（探测结果中的 Output）。
        /// </summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>
        /// 下载链接。
        /// </summary>
        public string Link { get; set; } = string.Empty;

        /// <summary>
        /// rdown 任务 ID。
        /// </summary>
        public string TaskId { get; set; } = string.Empty;

        /// <summary>
        /// 该文件对应的端点列表。
        /// </summary>
        public ObservableCollection<EndpointItem> Endpoints { get; set; }
            = new ObservableCollection<EndpointItem>();
    }
}
