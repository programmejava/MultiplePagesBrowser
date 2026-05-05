using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MultiplePagesBrowser.Models
{
    public class AppSettings : INotifyPropertyChanged
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MultiplePagesBrowser", "settings.json");

        private string _homePage = string.Empty;
        private bool _sharedCookies = true;
        private bool _pauseVideosWhenInactive = true;
        private bool _lowMemoryForInactive = true;
        private int _defaultLayoutIndex = 2;
        private int _gridGap = 3;
        private bool _showTileNumbers = true;

        /// <summary>主页地址，空则显示空白页</summary>
        public string HomePage
        {
            get => _homePage;
            set { _homePage = value; OnPropertyChanged(); }
        }

        /// <summary>是否共享 Cookie / 登录状态</summary>
        public bool SharedCookies
        {
            get => _sharedCookies;
            set { _sharedCookies = value; OnPropertyChanged(); }
        }

        /// <summary>非激活格子暂停视频播放（减少 GPU 解码占用）</summary>
        public bool PauseVideosWhenInactive
        {
            get => _pauseVideosWhenInactive;
            set { _pauseVideosWhenInactive = value; OnPropertyChanged(); }
        }

        /// <summary>非激活格子降低内存压力等级（Chromium 主动回收缓存）</summary>
        public bool LowMemoryForInactive
        {
            get => _lowMemoryForInactive;
            set { _lowMemoryForInactive = value; OnPropertyChanged(); }
        }

        /// <summary>默认布局索引</summary>
        public int DefaultLayoutIndex
        {
            get => _defaultLayoutIndex;
            set { _defaultLayoutIndex = value; OnPropertyChanged(); }
        }

        /// <summary>格子间距（像素）</summary>
        public int GridGap
        {
            get => _gridGap;
            set { _gridGap = Math.Clamp(value, 1, 12); OnPropertyChanged(); }
        }

        /// <summary>是否显示格子编号角标</summary>
        public bool ShowTileNumbers
        {
            get => _showTileNumbers;
            set { _showTileNumbers = value; OnPropertyChanged(); }
        }

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath,
                    JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
