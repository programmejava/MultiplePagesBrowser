using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MultiplePagesBrowser.Models
{
    /// <summary>
    /// 代表九宫格中一个页面格子的数据模型
    /// </summary>
    public class PageItem : INotifyPropertyChanged
    {
        private string _url = "about:blank";
        private string _title = "新标签页";
        private bool _isActive;
        private bool _isMuted = true;   // 默认静音，只有激活格子才发声
        private bool _isLoading;

        public int Index { get; set; }

        /// <summary>独立的 Cookie/Session 存储目录，每个格子不同路径实现账号隔离</summary>
        public string UserDataFolder { get; set; } = string.Empty;

        public string Url
        {
            get => _url;
            set { _url = value; OnPropertyChanged(); }
        }

        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        /// <summary>是否为当前焦点格子（影响音频、帧率）</summary>
        public bool IsActive
        {
            get => _isActive;
            set
            {
                _isActive = value;
                OnPropertyChanged();
                // 焦点格子发声，其余静音
                IsMuted = !value;
            }
        }

        public bool IsMuted
        {
            get => _isMuted;
            set { _isMuted = value; OnPropertyChanged(); }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
