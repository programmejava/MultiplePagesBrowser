using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MultiplePagesBrowser.Controls;
using MultiplePagesBrowser.Models;
using MultiplePagesBrowser.ViewModels;
using MultiplePagesBrowser.Views;

namespace MultiplePagesBrowser
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm = new();

        private readonly Dictionary<int, WebTile> _tilePool = new();
        private readonly List<WebTile> _activeTiles = new();

        // 书签窗口保持单例（不重复打开）
        private BookmarksWindow? _bookmarksWindow;

        // 最大化状态
        private bool _isTileMaximized = false;
        private WebTile? _maximizedTile = null;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _vm;

            WebTile.ViewModel = _vm;
            BookmarkStore.Load();

            // 从已加载的设置同步初始状态
            BtnCookieShare.IsChecked = _vm.Settings.SharedCookies;
            _vm.SharedCookies = _vm.Settings.SharedCookies;

            _vm.LayoutApplied += () => Dispatcher.Invoke(RefreshGrid);
            _vm.PropertyChanged += Vm_PropertyChanged;

            AddressBar.TextChanged += (s, e) =>
                AddressPlaceholder.Visibility =
                    string.IsNullOrEmpty(AddressBar.Text)
                        ? Visibility.Visible : Visibility.Collapsed;

            BuildLayoutButtons();
            RefreshGrid();
            UpdatePerfHint();

            // 窗口关闭时保存会话
            Closing += MainWindow_Closing;
        }

        // ── 会话保存（窗口关闭时）─────────────────────────────────

        private void MainWindow_Closing(object? sender,
            System.ComponentModel.CancelEventArgs e)
        {
            var session = new SessionData
            {
                LayoutIndex = _vm.LayoutIndex,
                Urls = _vm.Pages
                    .Take(_vm.Columns * _vm.Rows)
                    .Select(p => p.Url)
                    .ToList()
            };
            session.Save();
            BookmarkStore.Save();
        }

        // ── 恢复上次会话 ──────────────────────────────────────────

        private void BtnRestoreSession_Click(object sender, RoutedEventArgs e)
        {
            var session = SessionData.Load();
            if (!session.HasData)
            {
                StatusBar.Text = "没有找到上次的会话记录";
                return;
            }

            // 切换到保存的布局
            _vm.LayoutIndex = Math.Clamp(session.LayoutIndex,
                0, MainViewModel.LayoutPresets.Length - 1);

            // 恢复各格子 URL
            for (int i = 0; i < session.Urls.Count && i < _vm.Pages.Count; i++)
            {
                string url = session.Urls[i];
                if (!string.IsNullOrEmpty(url) && url != "about:blank")
                    _vm.Pages[i].Url = url;
            }

            StatusBar.Text = $"已恢复上次会话（{session.Urls.Count(u => u != "about:blank" && !string.IsNullOrEmpty(u))} 个页面）";
        }

        // ── 书签 ──────────────────────────────────────────────────

        private void BtnBookmarks_Click(object sender, RoutedEventArgs e)
            => OpenBookmarksWindow();

        private void OpenBookmarksWindow()
        {
            if (_bookmarksWindow != null && _bookmarksWindow.IsVisible)
            {
                _bookmarksWindow.Activate();
                return;
            }
            _bookmarksWindow = new BookmarksWindow(_vm) { Owner = this };
            _bookmarksWindow.OpenUrlRequested += url =>
            {
                _vm.NavigateActive(url);
                SyncAddressBar();
            };
            _bookmarksWindow.Show();
        }

        // ── 布局按钮 ──────────────────────────────────────────────

        private void BuildLayoutButtons()
        {
            LayoutButtonPanel.Children.Clear();
            for (int i = 0; i < MainViewModel.LayoutPresets.Length; i++)
            {
                int idx = i;
                var btn = new ToggleButton
                {
                    Content = _vm.LayoutLabels[i],
                    Style = (Style)Resources["LayoutBtn"],
                    IsChecked = (i == _vm.LayoutIndex),
                    Tag = i
                };
                btn.Click += (s, e) =>
                {
                    if (_isTileMaximized) RestoreTileFromMaximize();
                    _vm.LayoutIndex = idx;
                    SyncLayoutButtons();
                    UpdatePerfHint();
                };
                LayoutButtonPanel.Children.Add(btn);
            }
        }

        private void SyncLayoutButtons()
        {
            foreach (ToggleButton tb in LayoutButtonPanel.Children)
                tb.IsChecked = ((int)tb.Tag == _vm.LayoutIndex);
        }

        private void Vm_PropertyChanged(object? sender,
            System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.ActivePage):
                    Dispatcher.Invoke(() => { UpdateStatusBar(); SyncAddressBar(); });
                    break;
                case nameof(MainViewModel.AddressBarText):
                    Dispatcher.Invoke(() =>
                    {
                        if (AddressBar.Text != _vm.AddressBarText)
                            AddressBar.Text = _vm.AddressBarText;
                    });
                    break;
                case nameof(MainViewModel.LayoutIndex):
                    Dispatcher.Invoke(SyncLayoutButtons);
                    break;
            }
        }

        private void RefreshGrid()
        {
            int count = _vm.Columns * _vm.Rows;
            PageGrid.Rows = _vm.Rows;
            PageGrid.Columns = _vm.Columns;
            PageGrid.Children.Clear();
            _activeTiles.Clear();

            double gap = _vm.Settings.GridGap;
            GridContainer.Padding = new Thickness(gap);

            for (int i = 0; i < count; i++)
            {
                var page = _vm.Pages[i];
                if (!_tilePool.TryGetValue(i, out var tile))
                {
                    tile = new WebTile { Margin = new Thickness(gap / 2) };
                    tile.TileActivationRequested += Tile_ActivationRequested;
                    tile.MaximizeRequested += (s, _) => ToggleTileMaximize((WebTile)s!);
                    tile.MultiUrlPasteRequested += (s, urls) =>
                    {
                        _vm.NavigateMultiple(urls);
                        StatusBar.Text = $"已从剪贴板展开 {urls.Count} 个链接";
                    };
                    tile.AddressFocusRequested += (s, _) =>
                    {
                        AddressBar.Focus();
                        AddressBar.SelectAll();
                    };
                    tile.EscapePressed += (s, _) =>
                    {
                        if (_isTileMaximized) RestoreTileFromMaximize();
                    };
                    tile.Page = page;
                    _tilePool[i] = tile;
                }
                else
                {
                    tile.Margin = new Thickness(gap / 2);
                }
                _activeTiles.Add(tile);
                PageGrid.Children.Add(tile);
            }

            UpdateStatusBar();
            SyncLayoutButtons();
            UpdatePerfHint();
        }

        private void Tile_ActivationRequested(object? sender, PageItem page)
        {
            _vm.ActivePage = page;
            SyncAddressBar();
        }

        private WebTile? ActiveTile
        {
            get
            {
                if (_vm.ActivePage == null) return null;
                _tilePool.TryGetValue(_vm.ActivePage.Index, out var t);
                return t;
            }
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)    => ActiveTile?.GoBack();
        private void BtnForward_Click(object sender, RoutedEventArgs e) => ActiveTile?.GoForward();
        private void BtnReload_Click(object sender, RoutedEventArgs e)  => ActiveTile?.Reload();
        private void BtnStop_Click(object sender, RoutedEventArgs e)    => ActiveTile?.StopLoading();
        private void BtnHome_Click(object sender, RoutedEventArgs e)
        {
            string home = _vm.Settings.HomePage;
            if (_vm.ActivePage != null)
                _vm.ActivePage.Url = string.IsNullOrWhiteSpace(home) ? "about:blank" : home;
        }

        private void AddressBar_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            string text = AddressBar.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            // 尝试多链接解析
            var urls = MainViewModel.ExtractUrls(text);

            if (urls.Count > 1)
            {
                // 多链接：自动扩展布局并逐格打开
                _vm.NavigateMultiple(urls);
                StatusBar.Text = $"已展开 {urls.Count} 个链接";
            }
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                _vm.NavigateAll(text);
                StatusBar.Text = $"已同步导航所有 {_vm.Columns * _vm.Rows} 个格子";
            }
            else
            {
                _vm.NavigateActive(text);
            }

            Keyboard.ClearFocus();
        }

        private void AddressBar_GotFocus(object sender, RoutedEventArgs e)
            => AddressBar.SelectAll();

        private void BtnSyncNav_Click(object sender, RoutedEventArgs e)
        {
            string url = AddressBar.Text.Trim();
            if (string.IsNullOrEmpty(url)) return;
            _vm.NavigateAll(url);
            StatusBar.Text = $"已同步导航 {_vm.Columns * _vm.Rows} 个格子";
        }

        private void BtnMuteAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var p in _vm.Pages) p.IsMuted = true;
            StatusBar.Text = "所有格子已静音";
        }

        private void BtnCookieShare_Click(object sender, RoutedEventArgs e)
        {
            _vm.SharedCookies = BtnCookieShare.IsChecked == true;
            _vm.Settings.SharedCookies = _vm.SharedCookies;
            _vm.Settings.Save();
            StatusBar.Text = _vm.SharedCookies
                ? "Cookie 共享模式：所有格子共享登录状态"
                : "Cookie 独立模式：每个格子独立账号（需重启格子生效）";
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SettingsWindow(_vm.Settings, _vm) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                BtnCookieShare.IsChecked = _vm.Settings.SharedCookies;
                _vm.SharedCookies = _vm.Settings.SharedCookies;
                _vm.ApplySettings();
                UpdatePerfHint();
                StatusBar.Text = "设置已保存并应用";
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            switch (e.Key)
            {
                case Key.F11:
                    if (_isTileMaximized)
                        RestoreTileFromMaximize();
                    else if (ActiveTile != null)
                        MaximizeTile(ActiveTile);
                    e.Handled = true;
                    break;

                case Key.Escape when _isTileMaximized:
                    RestoreTileFromMaximize();
                    e.Handled = true;
                    break;

                case Key.F5:
                    ActiveTile?.Reload();
                    break;

                case Key.L when ctrl:
                    AddressBar.Focus();
                    AddressBar.SelectAll();
                    e.Handled = true;
                    break;

                case Key.OemComma when ctrl:
                    BtnSettings_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;

                case Key.B when ctrl:
                    OpenBookmarksWindow();
                    e.Handled = true;
                    break;

                case Key.D when ctrl:
                    ActiveTile?.AddBookmark();
                    StatusBar.Text = ActiveTile == null ? string.Empty :
                        BookmarkStore.Contains(_vm.ActivePage?.Url ?? string.Empty)
                            ? "已添加到书签" : "已从书签移除";
                    e.Handled = true;
                    break;

                case Key.V when ctrl:
                    if (AddressBar.IsFocused) break;

                    // 检测多链接粘贴
                    string clipText = Clipboard.GetText().Trim();
                    var urls = MainViewModel.ExtractUrls(clipText);
                    if (urls.Count > 1)
                    {
                        _vm.NavigateMultiple(urls);
                        StatusBar.Text = $"已从剪贴板展开 {urls.Count} 个链接";
                        e.Handled = true;
                    }
                    else
                    {
                        ActiveTile?.HandlePaste();
                        e.Handled = ActiveTile != null;
                    }
                    break;

                case Key.D1: case Key.D2: case Key.D3:
                case Key.D4: case Key.D5: case Key.D6:
                case Key.D7: case Key.D8: case Key.D9:
                    if (ctrl)
                    {
                        int idx = e.Key - Key.D1;
                        int maxIdx = _vm.Columns * _vm.Rows - 1;
                        if (idx <= maxIdx && idx < _vm.Pages.Count)
                            _vm.ActivePage = _vm.Pages[idx];
                        e.Handled = true;
                    }
                    break;
            }
        }

        private void UpdateStatusBar()
        {
            if (_vm.ActivePage != null)
                ActivePageLabel.Text =
                    $"格子: {_vm.ActivePage.Index + 1} / {_vm.Columns * _vm.Rows}";
        }

        private void UpdatePerfHint()
        {
            int total = _vm.Columns * _vm.Rows;
            PerfHint.Text = total >= 16 ? "⚡ 16格模式：已启用视频暂停+内存降级优化" : string.Empty;
        }

        private void SyncAddressBar()
        {
            if (_vm.ActivePage == null) return;
            string url = _vm.ActivePage.Url;
            string display = url == "about:blank" ? string.Empty : url;
            if (AddressBar.Text != display)
                AddressBar.Text = display;
        }

        // ── 格子最大化 ────────────────────────────────────────────

        private void ToggleTileMaximize(WebTile tile)
        {
            if (_isTileMaximized)
                RestoreTileFromMaximize();
            else
                MaximizeTile(tile);
        }

        private void MaximizeTile(WebTile tile)
        {
            if (_isTileMaximized) return;

            // 激活该格子
            if (tile.Page != null) _vm.ActivePage = tile.Page;

            // 从网格中临时移除并放入最大化容器
            PageGrid.Children.Remove(tile);
            tile.Margin = new Thickness(0);
            MaximizedContainer.Child = tile;

            // 切换可见性
            GridContainer.Visibility = Visibility.Collapsed;
            MaximizedContainer.Visibility = Visibility.Visible;

            // 显示还原按钮
            BtnRestoreTile.Visibility = Visibility.Visible;

            _isTileMaximized = true;
            _maximizedTile = tile;

            StatusBar.Text = $"格子 {(tile.Page?.Index ?? 0) + 1} 已最大化 — 双击角标 / F11 / Esc 还原";
        }

        private void RestoreTileFromMaximize()
        {
            if (!_isTileMaximized || _maximizedTile == null) return;

            // 从最大化容器取出
            MaximizedContainer.Child = null;

            // 重新放回网格（按原索引位置）
            int idx = _maximizedTile.Page?.Index ?? 0;
            double gap = _vm.Settings.GridGap;
            _maximizedTile.Margin = new Thickness(gap / 2);

            // 找到在 _activeTiles 里的位置插回 PageGrid
            int insertPos = Math.Min(idx, PageGrid.Children.Count);
            PageGrid.Children.Insert(insertPos, _maximizedTile);

            // 切换可见性
            MaximizedContainer.Visibility = Visibility.Collapsed;
            GridContainer.Visibility = Visibility.Visible;

            // 隐藏还原按钮
            BtnRestoreTile.Visibility = Visibility.Collapsed;

            _isTileMaximized = false;
            _maximizedTile = null;

            StatusBar.Text = "已还原网格视图";
        }

        private void BtnRestoreTile_Click(object sender, RoutedEventArgs e)
            => RestoreTileFromMaximize();
    }
}
