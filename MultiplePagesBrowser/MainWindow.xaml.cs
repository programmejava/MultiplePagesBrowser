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

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _vm;

            WebTile.ViewModel = _vm;

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
        }

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
                    _vm.LayoutIndex = idx;
                    foreach (ToggleButton tb in LayoutButtonPanel.Children)
                        tb.IsChecked = ((int)tb.Tag == idx);
                    UpdatePerfHint();
                };
                LayoutButtonPanel.Children.Add(btn);
            }
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
            string url = AddressBar.Text.Trim();
            if (string.IsNullOrEmpty(url)) return;

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                _vm.NavigateAll(url);
                StatusBar.Text = $"已同步导航所有 {_vm.Columns * _vm.Rows} 个格子";
            }
            else
            {
                _vm.NavigateActive(url);
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
            var dlg = new Views.SettingsWindow(_vm.Settings, _vm) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                // 同步共享 Cookie 状态到工具栏按钮
                BtnCookieShare.IsChecked = _vm.Settings.SharedCookies;
                _vm.SharedCookies = _vm.Settings.SharedCookies;
                // 重建网格（间距/编号可能已变）
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
                case Key.V when ctrl:
                    // 若当前激活格子已获焦（WebView2 内），WPF 不会触发此事件；
                    // 仅在地址栏/工具栏有焦点时才接管，将剪贴板 URL 导航到激活格子
                    if (AddressBar.IsFocused) break; // 让地址栏自己处理粘贴
                    ActiveTile?.HandlePaste();
                    e.Handled = ActiveTile != null;
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
            if (total >= 16)
                PerfHint.Text = "⚡ 16格模式：已启用视频暂停+内存降级优化";
            else if (total >= 9)
                PerfHint.Text = string.Empty;
            else
                PerfHint.Text = string.Empty;
        }

        private void SyncAddressBar()
        {
            if (_vm.ActivePage == null) return;
            string url = _vm.ActivePage.Url;
            string display = url == "about:blank" ? string.Empty : url;
            if (AddressBar.Text != display)
                AddressBar.Text = display;
        }
    }
}
