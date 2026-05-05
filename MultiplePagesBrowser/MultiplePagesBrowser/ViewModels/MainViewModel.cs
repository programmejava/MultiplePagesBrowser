using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using MultiplePagesBrowser.Models;

namespace MultiplePagesBrowser.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        // ── 布局预设 ──────────────────────────────────────────────
        public static readonly (int cols, int rows)[] LayoutPresets =
        {
            (1, 1),   // 1格
            (2, 2),   // 4格
            (3, 3),   // 9格  ← 默认
            (4, 3),   // 12格
            (4, 4),   // 16格
        };

        private int _layoutIndex = 2;
        private int _columns = 3;
        private int _rows = 3;
        private PageItem? _activePage;
        private string _addressBarText = string.Empty;

        /// <summary>全局应用设置</summary>
        public AppSettings Settings { get; } = AppSettings.Load();

        /// <summary>共享 Cookie 模式下所有格子使用同一个数据目录</summary>
        public static readonly string SharedUserDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MultiplePagesBrowser", "SharedProfile");

        // 兼容旧代码：直接代理到 Settings
        public bool SharedCookies
        {
            get => Settings.SharedCookies;
            set { Settings.SharedCookies = value; OnPropertyChanged(); }
        }

        public ObservableCollection<PageItem> Pages { get; } = new();

        public int Columns
        {
            get => _columns;
            set { _columns = value; OnPropertyChanged(); }
        }

        public int Rows
        {
            get => _rows;
            set { _rows = value; OnPropertyChanged(); }
        }

        public int LayoutIndex
        {
            get => _layoutIndex;
            set
            {
                if (value < 0 || value >= LayoutPresets.Length) return;
                _layoutIndex = value;
                OnPropertyChanged();
                ApplyLayout(LayoutPresets[value].cols, LayoutPresets[value].rows);
            }
        }

        public PageItem? ActivePage
        {
            get => _activePage;
            set
            {
                // 取消之前激活格子
                if (_activePage != null) _activePage.IsActive = false;
                _activePage = value;
                if (_activePage != null)
                {
                    _activePage.IsActive = true;
                    AddressBarText = _activePage.Url == "about:blank" ? string.Empty : _activePage.Url;
                }
                OnPropertyChanged();
            }
        }

        public string AddressBarText
        {
            get => _addressBarText;
            set { _addressBarText = value; OnPropertyChanged(); }
        }

        // ── 布局切换标题 ──────────────────────────────────────────
        public string[] LayoutLabels { get; } = { "1格", "4格", "9格", "12格", "16格" };

        /// <summary>布局应用完成后触发一次，通知 UI 重建网格</summary>
        public event Action? LayoutApplied;

        public MainViewModel()
        {
            _layoutIndex = Settings.DefaultLayoutIndex;
            var (c, r) = LayoutPresets[_layoutIndex];
            ApplyLayout(c, r);
        }

        /// <summary>设置保存后调用，热应用可立即生效的选项</summary>
        public void ApplySettings()
        {
            // 通知 UI 间距和编号变更
            OnPropertyChanged(nameof(Settings));
            LayoutApplied?.Invoke();
        }

        /// <summary>
        /// 切换布局：只增减 PageItem，不销毁已有实例，最后触发一次 LayoutApplied
        /// </summary>
        public void ApplyLayout(int cols, int rows)
        {
            Columns = cols;
            Rows = rows;

            int count = cols * rows;

            // 确保有足够的 PageItem（只增加，不销毁已有的）
            while (Pages.Count < count)
            {
                int i = Pages.Count;
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MultiplePagesBrowser", $"Profile_{i}");
                Directory.CreateDirectory(folder);

                Pages.Add(new PageItem
                {
                    Index = i,
                    UserDataFolder = folder,
                    Url = "about:blank",
                    Title = $"页面 {i + 1}"
                });
            }

            // 重新编号（数量只增不减，多余的格子下次切换大布局时复用）
            for (int i = 0; i < Pages.Count; i++)
                Pages[i].Index = i;

            // 若当前激活格子超出新布局范围，重置到第一个
            if (ActivePage == null || ActivePage.Index >= count)
                ActivePage = Pages.Count > 0 ? Pages[0] : null;

            // 通知 UI：布局已就绪，触发一次重建
            LayoutApplied?.Invoke();
        }

        /// <summary>对当前激活格子导航</summary>
        public void NavigateActive(string url)
        {
            if (ActivePage == null) return;
            if (!url.StartsWith("http://") && !url.StartsWith("https://") && url != "about:blank")
                url = "https://" + url;
            ActivePage.Url = url;
        }

        /// <summary>让所有格子同时导航到同一个 URL</summary>
        public void NavigateAll(string url)
        {
            if (!url.StartsWith("http://") && !url.StartsWith("https://") && url != "about:blank")
                url = "https://" + url;
            foreach (var p in Pages)
                p.Url = url;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
