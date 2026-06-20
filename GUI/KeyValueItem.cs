using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RDownloaderGUI
{
    /// <summary>
    /// 用于列表数据的键值对模型，支持 INotifyPropertyChanged 和 IsSelected。
    /// </summary>
    public class KeyValueItem : INotifyPropertyChanged
    {
        private string _key = string.Empty;
        private string _value = string.Empty;
        private bool _isSelected;

        public string Key
        {
            get => _key;
            set { _key = value; OnPropertyChanged(); }
        }

        public string Value
        {
            get => _value;
            set { _value = value; OnPropertyChanged(); }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
