using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MultiplePagesBrowser.Models;
using MultiplePagesBrowser.ViewModels;

namespace MultiplePagesBrowser.Controls
{
    /// <summary>
    /// 单个页面格子控件（纯代码构建，无 XAML），封装 WebView2 并解决：
    ///   1. 性能优化  - 非焦点格子挂起后台计时器（TrySuspendAsync）
    ///   2. 声音隔离  - 只有激活格子可以发声，其余默认静音
    ///   3. 防页面劫持 - 拦截 NewWindowRequested，在当前格子内加载
    ///   4. Cookie 共享/隔离 - 由 ViewModel.SharedCookies 控制
    /// </summary>
    public class WebTile : UserControl
    {
        /// <summary>由 MainWindow 注入，供所有 WebTile 读取共享设置</summary>
        public static MainViewModel? ViewModel { get; set; }
        // ── UI 元素 ───────────────────────────────────────────────
        private readonly Border _tileBorder;
        private readonly ContentPresenter _webViewHost;
        private readonly Border _loadingOverlay;
        private readonly TextBlock _muteIcon;
        private readonly TextBlock _indexLabel;
        private readonly Border _activateOverlay;

        // ── 状态 ──────────────────────────────────────────────────
        private WebView2? _webView;
        private PageItem? _page;
        private bool _webViewReady = false;
        private string? _pendingUrl = null;
        private bool _isNavigating = false;  // 防止导航事件触发循环

        private static readonly SolidColorBrush ActiveBrush =
            new(Color.FromRgb(0x4F, 0xC3, 0xF7));
        private static readonly SolidColorBrush InactiveBrush =
            new(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        private static bool IsCtrlDown() => false; // 保留供未来扩展，当前不使用
        public WebTile()
        {
            // ── 构建 UI 树 ────────────────────────────────────────

            _indexLabel = new TextBlock
            {
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xBE, 0xC5)),
                FontWeight = FontWeights.Bold
            };

            var indexBadge = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(4, 4, 0, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                Child = _indexLabel
            };

            _muteIcon = new TextBlock
            {
                Text = "🔇",
                FontSize = 10,
                Foreground = Brushes.White
            };

            var muteBadge = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 4, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(3, 1, 3, 1),
                Child = _muteIcon
            };

            var loadingBar = new ProgressBar
            {
                IsIndeterminate = true,
                Width = 120,
                Height = 4,
                Foreground = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x4A)),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var loadingText = new TextBlock
            {
                Text = "加载中...",
                Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0xCA, 0xF9)),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 12
            };

            var loadingStack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            loadingStack.Children.Add(loadingBar);
            loadingStack.Children.Add(loadingText);

            _loadingOverlay = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1A, 0x1A, 0x2E)),
                Visibility = Visibility.Collapsed,
                Child = loadingStack
            };

            _activateOverlay = new Border
            {
                Background = Brushes.Transparent,
                IsHitTestVisible = true,
                Visibility = Visibility.Visible
            };
            _activateOverlay.MouseLeftButtonDown += ActivateOverlay_MouseLeftButtonDown;

            _webViewHost = new ContentPresenter();

            var innerGrid = new Grid();
            innerGrid.Children.Add(_webViewHost);
            innerGrid.Children.Add(_loadingOverlay);
            innerGrid.Children.Add(muteBadge);
            innerGrid.Children.Add(indexBadge);
            innerGrid.Children.Add(_activateOverlay);

            _tileBorder = new Border
            {
                BorderThickness = new Thickness(2),
                BorderBrush = InactiveBrush,
                CornerRadius = new CornerRadius(4),
                ClipToBounds = true,
                Child = innerGrid
            };

            Content = _tileBorder;
            MinWidth = 120;
            MinHeight = 90;

            // Ctrl+V 在 WPF 层拦截（WebView2 已获焦时 WPF KeyDown 不会触发，
            // 但 MainWindow.xaml.cs 的 Window_KeyDown 可以转发给 ActiveTile）
        }

        /// <summary>由 MainWindow 在 Window_KeyDown 中转发 Ctrl+V 给当前激活格子</summary>
        public void HandlePaste()
        {
            string text = Clipboard.GetText().Trim();
            if (IsValidUrl(text) && _page != null)
                _page.Url = text;
        }

        // ── 公开属性：绑定数据模型 ────────────────────────────────

        public PageItem? Page
        {
            get => _page;
            set
            {
                if (_page != null)
                    _page.PropertyChanged -= Page_PropertyChanged;

                _page = value;

                if (_page != null)
                {
                    _page.PropertyChanged += Page_PropertyChanged;
                    _indexLabel.Text = (_page.Index + 1).ToString();
                    // 尊重设置中的编号显示选项
                    var badge = _indexLabel.Parent as Border ?? (_indexLabel.Parent as FrameworkElement);
                    if (badge?.Parent is Border b) b.Visibility =
                        (ViewModel?.Settings.ShowTileNumbers ?? true) ? Visibility.Visible : Visibility.Collapsed;
                    InitWebView();
                }
            }
        }

        // ── WebView2 初始化（含独立 Cookie 隔离）────────────────

        private async void InitWebView()
        {
            if (_page == null) return;

            if (_webView != null)
            {
                _webViewHost.Content = null;
                _webView.Dispose();
                _webView = null;
                _webViewReady = false;
            }

            _webView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            _webViewHost.Content = _webView;

            // ── Cookie 共享/隔离：根据开关选择数据目录 ──────────
            bool shared = ViewModel?.SharedCookies ?? true;
            string dataFolder = shared
                ? MainViewModel.SharedUserDataFolder
                : _page.UserDataFolder;
            Directory.CreateDirectory(dataFolder);

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: dataFolder);

            await _webView.EnsureCoreWebView2Async(env);

            // ── 防页面劫持：拦截新窗口请求 ──────────────────────
            _webView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;

            // ── 右键菜单扩展 ──────────────────────────────────────
            _webView.CoreWebView2.ContextMenuRequested += CoreWebView2_ContextMenuRequested;

            // ── 页面标题同步 ──────────────────────────────────────
            _webView.CoreWebView2.DocumentTitleChanged += (s, e) =>
            {
                if (_page != null)
                    _page.Title = _webView.CoreWebView2.DocumentTitle;
            };

            // ── 导航状态同步 ──────────────────────────────────────
            _webView.CoreWebView2.NavigationStarting += (s, e) =>
            {
                if (_page != null)
                {
                    _page.IsLoading = true;
                    _isNavigating = true;   // 标记：此次 Url 变更由浏览器内部触发，不再触发 NavigateTo
                    _page.Url = e.Uri;
                    _isNavigating = false;
                }
                Dispatcher.Invoke(() => _loadingOverlay.Visibility = Visibility.Visible);
            };

            _webView.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                if (_page != null) _page.IsLoading = false;
                Dispatcher.Invoke(() => _loadingOverlay.Visibility = Visibility.Collapsed);
                // 每次页面加载完成后重新注入激活脚本（新页面会清除旧脚本）
                InjectActivationScript();
            };

            // 收到 JS 消息 "tile-clicked" 时激活本格子
            _webView.CoreWebView2.WebMessageReceived += (s, e) =>
            {
                string msg = e.TryGetWebMessageAsString();
                if (msg == "tile-clicked" && _page != null && !_page.IsActive)
                {
                    Dispatcher.Invoke(() => TileActivationRequested?.Invoke(this, _page));
                }
                else if (msg == "copy-url" && _page != null &&
                         !string.IsNullOrEmpty(_page.Url) && _page.Url != "about:blank")
                {
                    Dispatcher.Invoke(() => Clipboard.SetText(_page.Url));
                }
            };

            _webViewReady = true;

            ApplyMute(_page.IsMuted);

            string url = _pendingUrl ?? _page.Url;
            _pendingUrl = null;
            NavigateTo(url);

            UpdateActiveStyle(_page.IsActive);
        }

        // ── 防页面劫持 ────────────────────────────────────────────

        private void CoreWebView2_NewWindowRequested(object? sender,
            CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            _webView?.CoreWebView2.Navigate(e.Uri);
        }

        // ── 数据模型变更响应 ──────────────────────────────────────

        private void Page_PropertyChanged(object? sender,
            System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(PageItem.Url):
                    if (!_isNavigating)           // 只有外部（用户输入）触发的 Url 变更才导航
                        Dispatcher.Invoke(() => NavigateTo(_page!.Url));
                    break;
                case nameof(PageItem.IsActive):
                    Dispatcher.Invoke(() => UpdateActiveStyle(_page!.IsActive));
                    break;
                case nameof(PageItem.IsMuted):
                    Dispatcher.Invoke(() => ApplyMute(_page!.IsMuted));
                    break;
            }
        }

        private void NavigateTo(string url)
        {
            if (!_webViewReady || _webView?.CoreWebView2 == null)
            {
                _pendingUrl = url;
                return;
            }
            if (url == "about:blank")
                _webView.CoreWebView2.NavigateToString(
                    "<html><body style='background:#1a1a2e;color:#607d8b;" +
                    "font-family:sans-serif;display:flex;justify-content:center;" +
                    "align-items:center;height:100vh;margin:0'>" +
                    "<span style='font-size:18px;opacity:0.5'>空白页面</span></body></html>");
            else
                _webView.CoreWebView2.Navigate(url);
        }

        // ── 声音控制（问题2：声音隔离）───────────────────────────

        private void ApplyMute(bool muted)
        {
            if (!_webViewReady || _webView?.CoreWebView2 == null) return;
            _webView.CoreWebView2.IsMuted = muted;
            _muteIcon.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── 性能优化（问题1）+ 激活样式 ──────────────────────────

        private void UpdateActiveStyle(bool isActive)
        {
            _tileBorder.BorderBrush = isActive ? ActiveBrush : InactiveBrush;
            _activateOverlay.Visibility = Visibility.Collapsed;

            if (!_webViewReady || _webView?.CoreWebView2 == null) return;

            if (isActive)
            {
                _webView.CoreWebView2.Resume();
                // 切回激活时恢复运行中的视频
                if (ViewModel?.Settings.PauseVideosWhenInactive == true)
                    _ = _webView.CoreWebView2.ExecuteScriptAsync(
                        "document.querySelectorAll('video').forEach(v => { if(v.dataset.autoPaused) { v.play(); delete v.dataset.autoPaused; } });");
                if (ViewModel?.Settings.LowMemoryForInactive == true)
                    _webView.CoreWebView2.MemoryUsageTargetLevel =
                        Microsoft.Web.WebView2.Core.CoreWebView2MemoryUsageTargetLevel.Normal;
            }
            else
            {
                // 暂停非激活格子的视频
                if (ViewModel?.Settings.PauseVideosWhenInactive == true)
                    _ = _webView.CoreWebView2.ExecuteScriptAsync(
                        "document.querySelectorAll('video').forEach(v => { if(!v.paused){ v.pause(); v.dataset.autoPaused='1'; } });");
                // 降低内存优先级，让 Chromium 主动回收渲染缓存
                if (ViewModel?.Settings.LowMemoryForInactive == true)
                    _webView.CoreWebView2.MemoryUsageTargetLevel =
                        Microsoft.Web.WebView2.Core.CoreWebView2MemoryUsageTargetLevel.Low;
                _ = _webView.CoreWebView2.TrySuspendAsync();
            }
        }

        // ── 点击激活 ──────────────────────────────────────────────

        private void ActivateOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            TileActivationRequested?.Invoke(this, _page!);
            e.Handled = true;
        }

        /// <summary>用户点击非激活格子时触发</summary>
        public event EventHandler<PageItem>? TileActivationRequested;

        // ── 公开导航方法 ──────────────────────────────────────────

        public void GoBack()    => _webView?.CoreWebView2.GoBack();
        public void GoForward() => _webView?.CoreWebView2.GoForward();

        public void Reload() => _webView?.CoreWebView2.Reload();

        public void StopLoading() => _webView?.CoreWebView2.Stop();

        // ── 注入激活脚本 ──────────────────────────────────────────
        // 同时拦截：
        //   mousedown → "tile-clicked"（激活格子）
        //   Ctrl+C（无选中文字时）→ "copy-url"（复制页面链接）
        //   Ctrl+V（剪贴板是 URL）→ "paste-url:<url>"（粘贴导航）
        private void InjectActivationScript()
        {
            _webView?.CoreWebView2.ExecuteScriptAsync(
                "(function(){" +
                "  if(window.__tileActivationBound) return;" +
                "  window.__tileActivationBound = true;" +
                "  document.addEventListener('mousedown', function(){" +
                "    window.chrome.webview.postMessage('tile-clicked');" +
                "  }, {capture:true, passive:true});" +
                "  document.addEventListener('keydown', function(e){" +
                "    if(!(e.ctrlKey || e.metaKey)) return;" +
                "    if(e.key === 'c' || e.key === 'C'){" +
                "      var sel = window.getSelection ? window.getSelection().toString() : '';" +
                "      if(!sel){ e.preventDefault(); window.chrome.webview.postMessage('copy-url'); }" +
                "    } else if(e.key === 'v' || e.key === 'V'){" +
                // Ctrl+V 粘贴由 C# 侧 PreviewKeyDown 处理，JS 这里不处理
                "    }" +
                "  }, {capture:true});" +
                "})();");
        }

        // ── 右键菜单扩展 ─────────────────────────────────────────────────────────

        private void CoreWebView2_ContextMenuRequested(object? sender,
            CoreWebView2ContextMenuRequestedEventArgs e)
        {
            if (_webView?.CoreWebView2 == null) return;
            var items = e.MenuItems;
            var env   = _webView.CoreWebView2.Environment;

            // 分割线
            items.Add(env.CreateContextMenuItem(
                string.Empty, null, CoreWebView2ContextMenuItemKind.Separator));

            // 粘贴并打开链接（仅当剪贴板含有效 URL 时启用）
            string clipText = Dispatcher.Invoke(() => Clipboard.GetText().Trim());
            bool hasUrl = IsValidUrl(clipText);
            var pasteItem = env.CreateContextMenuItem(
                "📋  粘贴并打开链接", null, CoreWebView2ContextMenuItemKind.Command);
            pasteItem.IsEnabled = hasUrl;
            pasteItem.CustomItemSelected += (s, _) =>
                Dispatcher.Invoke(() => { if (_page != null && hasUrl) _page.Url = clipText; });
            items.Add(pasteItem);

            // 复制页面链接
            bool hasPageUrl = _page != null &&
                !string.IsNullOrEmpty(_page.Url) && _page.Url != "about:blank";
            var copyItem = env.CreateContextMenuItem(
                "🔗  复制页面链接", null, CoreWebView2ContextMenuItemKind.Command);
            copyItem.IsEnabled = hasPageUrl;
            copyItem.CustomItemSelected += (s, _) =>
                Dispatcher.Invoke(() =>
                {
                    if (_page != null && !string.IsNullOrEmpty(_page.Url))
                        Clipboard.SetText(_page.Url);
                });
            items.Add(copyItem);

            // 用外部浏览器打开
            var extItem = env.CreateContextMenuItem(
                "🌐  用外部浏览器打开", null, CoreWebView2ContextMenuItemKind.Command);
            extItem.IsEnabled = hasPageUrl;
            extItem.CustomItemSelected += (s, _) =>
                Dispatcher.Invoke(OpenInExternalBrowser);
            items.Add(extItem);
        }

        // ── 辅助方法 ──────────────────────────────────────────────────────────────

        private static bool IsValidUrl(string text) =>
            Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        private void OpenInExternalBrowser()
        {
            if (_page == null || string.IsNullOrEmpty(_page.Url) || _page.Url == "about:blank")
                return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _page.Url,
                    UseShellExecute = true
                });
            }
            catch { /* 忽略启动外部浏览器失败 */ }
        }
    }
}
