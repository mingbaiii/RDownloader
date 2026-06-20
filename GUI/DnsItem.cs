using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RDownloaderGUI
{
    /// <summary>
    /// DNS 服务器列表项，包含地址和启用状态，支持属性变更通知。
    /// </summary>
    public class DnsItem : INotifyPropertyChanged
    {
        private string _address = string.Empty;
        private bool _isEnabled = true;

        public string Address
        {
            get => _address;
            set { _address = value; OnPropertyChanged(); }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set { _isEnabled = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
