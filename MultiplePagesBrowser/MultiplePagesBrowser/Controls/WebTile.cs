using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using MultiplePagesBrowser.Models;
using MultiplePagesBrowser.ViewModels;
using MultiplePagesBrowser.Views;

namespace MultiplePagesBrowser.Controls;

/// <summary>持有一个浏览器实例；标题栏始终位于 WPF 行中，避免 WebView2 Airspace 遮挡。</summary>
public partial class WebTile : UserControl, IDisposable
{
    public static MainViewModel? ViewModel { get; set; }
    public event EventHandler? MaximizeRequested;
    public event EventHandler<PageItem>? TileActivationRequested;
    public event EventHandler<List<string>>? MultiUrlPasteRequested;
    public event EventHandler? AddressFocusRequested;
    public event EventHandler? EscapePressed;
    public event Action<PageItem, PageItem>? SwapRequested;

    private readonly Border _border, _header;
    private readonly ContentPresenter _host;
    private readonly StackPanel _message;
    private readonly TextBlock _messageText, _title, _countdown;
    private readonly Button _mute, _zoom;
    private readonly DispatcherTimer _timer;
    private readonly RefreshSchedule _schedule = new();
    private readonly SemaphoreSlim _policyGate = new(1, 1);
    private WebView2? _webView;
    private PageItem? _page;
    private bool _ready, _disposed, _syncingUrl, _initializing;
    private bool _suspended;
    private string? _pendingUrl;
    private Point? _dragStart;
    private string _lastStatus = "初始化中…";

    public WebTile()
    {
        _title = new TextBlock
        {
            Foreground = Brushes.LightSteelBlue, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 3, 0)
        };
        _countdown = new TextBlock { Foreground = Brushes.LightSteelBlue, FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 3, 0) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(_countdown);
        tools.Children.Add(HeaderButton("↻", "刷新当前窗格", Reload));
        _mute = HeaderButton("静", "切换此窗格声音（自动切换到每格独立控制）", ToggleMute);
        tools.Children.Add(_mute);
        tools.Children.Add(HeaderButton("−", "缩小网页", () => ChangeZoom(-.1)));
        _zoom = HeaderButton("100%", "点击恢复 100%", () => { if (_page != null) _page.ZoomFactor = 1; });
        _zoom.Width = 42;
        tools.Children.Add(_zoom);
        tools.Children.Add(HeaderButton("+", "放大网页", () => ChangeZoom(.1)));
        tools.Children.Add(HeaderButton("⋯", "运行策略、定时刷新及更多操作", ShowMenu));
        tools.Children.Add(HeaderButton("×", "关闭此页面（保留空白窗格）", ClosePage));
        var headerContent = new DockPanel();
        DockPanel.SetDock(tools, Dock.Right);
        headerContent.Children.Add(tools);
        headerContent.Children.Add(_title);
        _header = new Border { Height = 30, Background = Brush(17, 24, 38), Child = headerContent, AllowDrop = true };
        _header.MouseLeftButtonDown += HeaderDown;
        _header.MouseLeftButtonUp += (_, _) => _dragStart = null;
        _header.MouseMove += HeaderMove;
        _header.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(typeof(PageItem)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        _header.Drop += (_, e) =>
        {
            if (_page != null && e.Data.GetData(typeof(PageItem)) is PageItem source)
                SwapRequested?.Invoke(source, _page);
            e.Handled = true;
        };
        _host = new ContentPresenter();
        _messageText = new TextBlock { Text = "初始化中…", Foreground = Brushes.LightSteelBlue,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(2) };
        _message = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _message.Children.Add(_messageText);
        var resume = new Button { Content = "恢复 / 重试", Padding = new Thickness(10, 4, 10, 4), HorizontalAlignment = HorizontalAlignment.Center };
        resume.Click += (_, _) =>
        {
            if (_page == null) return;
            if (_ready) _page.RunMode = PaneRunMode.Continuous;
            else _ = InitializeAsync();
        };
        _message.Children.Add(resume);
        var content = new Grid();
        content.Children.Add(_host); content.Children.Add(_message);
        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(content, 1); outer.Children.Add(_header); outer.Children.Add(content);
        _border = new Border { BorderThickness = new Thickness(2), BorderBrush = Brush(37, 42, 58), Child = outer };
        Content = _border;
        // Header 在窄窗格中显示紧凑版，完整操作仍可从菜单访问。
        SizeChanged += (_, _) =>
        {
            bool compact = ActualWidth < 360;
            foreach (UIElement child in tools.Children)
                if (child != tools.Children[^2] && child != _countdown)
                    child.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        };
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => TimerTick(), Dispatcher);
        _timer.Stop();
        Loaded += (_, _) => _ = InitializeAsync();
    }
    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
    private static Button HeaderButton(string text, string tip, Action action)
    {
        var button = new Button { Content = text, ToolTip = tip, Width = 24, Height = 24,
            Margin = new Thickness(1, 1, 1, 1), Padding = new Thickness(0), FontSize = 11,
            Foreground = Brushes.LightSteelBlue, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Focusable = false };
        button.Click += (_, e) => { action(); e.Handled = true; };
        return button;
    }
    private void HeaderDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject node && FindButton(node) != null) return;
        if (_page != null) TileActivationRequested?.Invoke(this, _page);
        if (e.ClickCount == 2) { _dragStart = null; MaximizeRequested?.Invoke(this, EventArgs.Empty); }
        else _dragStart = e.GetPosition(_header);
        e.Handled = true;
    }
    private static Button? FindButton(DependencyObject? node)
    {
        while (node != null)
        {
            if (node is Button button) return button;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node) : (node as FrameworkContentElement)?.Parent;
        }
        return null;
    }
    private void HeaderMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed || _page == null) return;
        var point = e.GetPosition(_header);
        if (Math.Abs(start.X - point.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(start.Y - point.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        DragDrop.DoDragDrop(_header, new DataObject(typeof(PageItem), _page), DragDropEffects.Move);
    }
    public PageItem? Page
    {
        get => _page;
        set
        {
            if (_page != null) throw new InvalidOperationException("WebTile 的页面身份不能替换，请复用整个控件或重建。");
            _page = value;
            if (value == null) return;
            value.PropertyChanged += PageChanged;
            UpdateHeader();
            if (IsLoaded) _ = InitializeAsync();
        }
    }
    private void PageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || _page == null) return;
        switch (e.PropertyName)
        {
            case nameof(PageItem.Url):
                if (!_syncingUrl) NavigateTo(_page.Url);
                break;
            case nameof(PageItem.ZoomFactor):
                if (_ready && _webView != null && Math.Abs(_webView.ZoomFactor - _page.ZoomFactor) > .001)
                    _webView.ZoomFactor = _page.ZoomFactor;
                break;
            case nameof(PageItem.IsActive):
            case nameof(PageItem.IsMuted):
                _ = ApplyPolicyAsync();
                break;
            case nameof(PageItem.RunMode):
                _schedule.Reset();
                _ = ApplyPolicyAsync();
                break;
            case nameof(PageItem.RefreshIntervalSeconds):
            case nameof(PageItem.AutoRefreshPaused):
            case nameof(PageItem.IsLoading):
                _schedule.Reset();
                break;
        }
        UpdateHeader();
    }
    public void ApplySettings()
    {
        if (_disposed) return;
        UpdateHeader();
        _ = ApplyPolicyAsync();
    }
    private void UpdateHeader()
    {
        if (_page == null) return;
        string number = ViewModel?.Settings.ShowTileNumbers == false ? "" : $"{_page.Index + 1} · ";
        _title.Text = number + (_page.Url == "about:blank" ? "空白页面" : _page.Title);
        _title.ToolTip = $"{_page.Title}\n{_page.Url}\n{_lastStatus}\n拖动标题到另一窗格标题上交换；双击最大化";
        _zoom.Content = $"{_page.ZoomFactor:P0}";
        bool muted = _page.ShouldMute(ViewModel?.SoundMode ?? SoundMode.ActiveOnly);
        _mute.Content = muted ? "静" : "声";
        _border.BorderBrush = _page.IsActive ? Brush(79, 195, 247) : Brush(37, 42, 58);
        _header.Background = _page.IsActive ? Brush(10, 35, 54) : Brush(17, 24, 38);
    }
    private void ToggleMute()
    {
        if (_page == null || ViewModel == null) return;
        bool wasMuted = _page.ShouldMute(ViewModel.SoundMode);
        // 进入独立模式时保留每格此刻的实际发声状态，避免意外开启其他窗格。
        if (ViewModel.SoundMode != SoundMode.Custom)
        {
            foreach (var page in ViewModel.Pages) page.IsMuted = page.ShouldMute(ViewModel.SoundMode);
            ViewModel.SoundMode = SoundMode.Custom;
        }
        _page.IsMuted = !wasMuted;
    }
    private void ChangeZoom(double delta)
    { if (_page != null) _page.ZoomFactor = Math.Round(_page.ZoomFactor + delta, 2); }
    private void ClosePage()
    {
        if (_page == null) return;
        _page.RefreshIntervalSeconds = 0; _page.AutoRefreshPaused = false;
        _page.RunMode = PaneRunMode.Continuous; _page.IsMuted = true;
        _page.Title = "空白页面"; _page.Url = "about:blank";
    }
    private void ShowMenu()
    {
        if (_page == null) return;
        var menu = new ContextMenu();
        void Item(string title, Action action)
        { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Item("刷新", Reload);
        Item(_page.ShouldMute(ViewModel?.SoundMode ?? SoundMode.ActiveOnly) ? "开启此窗格声音" : "静音此窗格", ToggleMute);
        Item("缩小网页", () => ChangeZoom(-.1));
        Item($"恢复 100%（当前 {_page.ZoomFactor:P0}）", () => _page.ZoomFactor = 1);
        Item("放大网页", () => ChangeZoom(.1));
        menu.Items.Add(new Separator());
        foreach (var (label, mode) in new[] { ("持续运行", PaneRunMode.Continuous), ("节能", PaneRunMode.Eco), ("暂停", PaneRunMode.Paused) })
        {
            var choice = new MenuItem { Header = label, IsCheckable = true, IsChecked = _page.RunMode == mode };
            choice.Click += (_, _) => _page.RunMode = mode;
            menu.Items.Add(choice);
        }
        Item("运行与定时刷新设置…", () => new PaneOptionsWindow(_page) { Owner = Window.GetWindow(this) }.ShowDialog());
        if (_page.RefreshIntervalSeconds > 0)
            Item(_page.AutoRefreshPaused ? "恢复定时刷新" : "暂停定时刷新", () => _page.AutoRefreshPaused = !_page.AutoRefreshPaused);
        menu.Items.Add(new Separator());
        Item("收藏 / 取消收藏", AddBookmark);
        Item("最大化 / 还原", () => MaximizeRequested?.Invoke(this, EventArgs.Empty));
        Item("关闭此页面（保留空白窗格）", ClosePage);
        menu.PlacementTarget = _header; menu.IsOpen = true;
    }
    private void TimerTick()
    {
        if (!_ready || _disposed || _page == null) return;
        var now = DateTimeOffset.UtcNow;
        var remaining = _schedule.RemainingSeconds(_page, now);
        _countdown.Text = _page.RunMode == PaneRunMode.Paused ? "暂停" :
            _page.AutoRefreshPaused && _page.RefreshIntervalSeconds > 0 ? "计时暂停" : remaining is int seconds ? $"{seconds}s" : "";
        if (_schedule.TakeDue(_page, now)) Reload();
    }
    public void HandlePaste()
    {
        var urls = MainViewModel.ExtractUrls(Clipboard.GetText());
        if (urls.Count > 1) MultiUrlPasteRequested?.Invoke(this, urls);
        else if (urls.Count == 1 && _page != null) _page.Url = urls[0];
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer.Stop();
        if (_page != null) _page.PropertyChanged -= PageChanged;
        _ready = false; _webView?.Dispose(); _webView = null; _host.Content = null;
    }
}
