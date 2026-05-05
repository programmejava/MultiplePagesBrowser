using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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

        /// <summary>请求最大化/还原该格子（由 MainWindow 订阅处理）</summary>
        public event EventHandler? MaximizeRequested;

        // ── UI 元素 ───────────────────────────────────────────────
        private readonly Border _tileBorder;
        private readonly ContentPresenter _webViewHost;
        private readonly Border _loadingOverlay;
        private readonly TextBlock _muteIcon;
        private readonly TextBlock _indexLabel;
        private readonly Border _activateOverlay;
        private readonly Border _headerBar;   // Header 行（WPF 层，不受 Airspace 影响）

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

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);
        private static bool IsCtrlKeyDown() => (GetKeyState(0x11) & 0x8000) != 0; // VK_CONTROL=0x11
        public WebTile()
        {
            // ── 构建 UI 树 ────────────────────────────────────────
            // 布局：
            //   Row 0 — Header 条（纯 WPF，不受 WebView2 Airspace 遮挡）
            //   Row 1 — WebView2 内容区 + 激活遮罩 + 加载指示器（叠加在 WebView2 上，
            //            加载层仅在 WebView2 初始化前可见，之后 WebView2 会遮挡，但此时已不需要）

            _indexLabel = new TextBlock
            {
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xBE, 0xC5)),
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };

            _muteIcon = new TextBlock
            {
                Text = "🔇",
                FontSize = 10,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };

            // ── Header 条（Row 0）──────────────────────────────────
            // 双击 Header 触发最大化；单击激活格子
            var headerContent = new Grid();
            var numLabel = new TextBlock
            {
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x7D, 0x8B)),
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(6, 0, 0, 0)
            };
            // 把 _indexLabel 合并到 headerContent
            headerContent.Children.Add(numLabel);
            headerContent.Children.Add(new TextBlock
            {
                Text = "⛶",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0x40, 0x55, 0x65)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "双击最大化/还原此格子 (F11)"
            });
            var muteInHeader = _muteIcon;
            muteInHeader.HorizontalAlignment = HorizontalAlignment.Right;
            muteInHeader.Margin = new Thickness(0, 0, 6, 0);
            headerContent.Children.Add(muteInHeader);

            // 重用 _indexLabel → 改为 numLabel（二者指向同一文字，用 numLabel）
            // 为了不破坏后续 _indexLabel 引用，让 _indexLabel 与 numLabel 同步
            _indexLabel = numLabel;   // 重新指向 header 里的 label

            _headerBar = new Border
            {
                Height = 18,
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0D, 0x0D, 0x1B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0x4F, 0xC3, 0xF7)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Cursor = Cursors.Hand,
                ToolTip = "双击最大化/还原 (F11)",
                Child = headerContent
            };
            _headerBar.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    MaximizeRequested?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                }
                else
                {
                    // 单击也激活格子
                    if (_page != null && !_page.IsActive)
                        TileActivationRequested?.Invoke(this, _page);
                    e.Handled = true;
                }
            };

            // ── 加载指示器（初始化期间显示，WebView2 就绪后几乎不可见）──
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

            // WebView2 区（Row 1）
            var contentGrid = new Grid();
            contentGrid.Children.Add(_webViewHost);
            contentGrid.Children.Add(_loadingOverlay);
            contentGrid.Children.Add(_activateOverlay);

            // 外层 Grid：Row0=Header  Row1=内容
            var outerGrid = new Grid();
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(_headerBar, 0);
            Grid.SetRow(contentGrid, 1);
            outerGrid.Children.Add(_headerBar);
            outerGrid.Children.Add(contentGrid);

            _tileBorder = new Border
            {
                BorderThickness = new Thickness(2),
                BorderBrush = InactiveBrush,
                CornerRadius = new CornerRadius(4),
                ClipToBounds = true,
                Child = outerGrid
            };

            Content = _tileBorder;
            MinWidth = 120;
            MinHeight = 90;
        }

        /// <summary>由 MainWindow 在 Window_KeyDown 中转发 Ctrl+V 给当前激活格子（地址栏有焦点时用）</summary>
        public void HandlePaste()
        {
            string text = Clipboard.GetText().Trim();
            if (IsValidUrl(text) && _page != null)
                _page.Url = text;
        }

        // ── 持久注入的快捷键拦截脚本 ─────────────────────────────
        // AddScriptToExecuteOnDocumentCreatedAsync 确保每个页面加载前都注入，
        // 即使页面跳转也不丢失。脚本发送消息给 C# 侧处理。
        private static string GetAcceleratorScript() => """
            (function(){
              if(window.__hostKeysBound) return;
              window.__hostKeysBound = true;
              document.addEventListener('keydown', function(e){
                var ctrl = e.ctrlKey || e.metaKey;
                if(ctrl && (e.key === 'v' || e.key === 'V')){
                  // 只在没有文字输入焦点时拦截（避免阻断 input/textarea 粘贴）
                  var tag = document.activeElement ? document.activeElement.tagName : '';
                  if(tag !== 'INPUT' && tag !== 'TEXTAREA' && !document.activeElement.isContentEditable){
                    e.preventDefault();
                    e.stopPropagation();
                    window.chrome.webview.postMessage('ctrl-v');
                  }
                } else if(ctrl && (e.key === 'l' || e.key === 'L')){
                  e.preventDefault();
                  window.chrome.webview.postMessage('ctrl-l');
                } else if(e.key === 'F11'){
                  e.preventDefault();
                  window.chrome.webview.postMessage('f11');
                } else if(e.key === 'Escape'){
                  window.chrome.webview.postMessage('esc');
                }
              }, {capture:true});
            })();
            """;

        // 旧的 CoreWebView2_AcceleratorKeyPressed（已用 JS 方案替代）
        private void CoreWebView2_AcceleratorKeyPressed(object? sender,
            CoreWebView2AcceleratorKeyPressedEventArgs e) { }

        /// <summary>格子内按 Ctrl+V 检测到多链接时触发，由 MainWindow 处理布局扩展</summary>
        public event EventHandler<List<string>>? MultiUrlPasteRequested;

        /// <summary>格子内按 Ctrl+L 时触发，请求聚焦地址栏</summary>
        public event EventHandler? AddressFocusRequested;

        /// <summary>格子内按 Esc 时触发</summary>
        public event EventHandler? EscapePressed;

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
                    _headerBar.Visibility =
                        (ViewModel?.Settings.ShowTileNumbers ?? true)
                            ? Visibility.Visible : Visibility.Collapsed;
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
                if (_page != null)
                {
                    _page.IsLoading = false;
                    // 用真实 Source 同步，避免 SPA history.pushState 导致 _page.Url 过期
                    string realUrl = _webView.CoreWebView2.Source;
                    if (!string.IsNullOrEmpty(realUrl) && realUrl != _page.Url)
                    {
                        _isNavigating = true;
                        _page.Url = realUrl;
                        _isNavigating = false;
                    }
                }
                Dispatcher.Invoke(() => _loadingOverlay.Visibility = Visibility.Collapsed);
                // 每次页面加载完成后重新注入激活脚本（新页面会清除旧脚本）
                InjectActivationScript();
            };

            // ── 拦截 WebView2 内的加速键（解决 Ctrl+V 等快捷键被 Chromium 吃掉的问题）──
            // AcceleratorKeyPressed 在此版本 WPF WebView2 中不直接暴露，
            // 改用 AddScriptToExecuteOnDocumentCreatedAsync 持久注入 JS 拦截。
            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                GetAcceleratorScript());

            // 收到 JS 消息时处理
            _webView.CoreWebView2.WebMessageReceived += (s, e) =>
            {
                string msg = e.TryGetWebMessageAsString();
                if (msg == "tile-clicked" && _page != null && !_page.IsActive)
                {
                    Dispatcher.Invoke(() => TileActivationRequested?.Invoke(this, _page));
                }
                else if (msg == "copy-url")
                {
                    Dispatcher.Invoke(() =>
                    {
                        string src = _webView?.CoreWebView2?.Source ?? _page?.Url ?? string.Empty;
                        if (!string.IsNullOrEmpty(src) && src != "about:blank")
                            Clipboard.SetText(src);
                    });
                }
                else if (msg == "ctrl-v")
                {
                    // Ctrl+V 在 WebView2 内触发：读剪贴板，是 URL 就导航，否则不处理（让浏览器粘贴文字）
                    Dispatcher.Invoke(() =>
                    {
                        string clip = Clipboard.GetText().Trim();
                        var urls = MainViewModel.ExtractUrls(clip);
                        if (urls.Count > 1)
                            MultiUrlPasteRequested?.Invoke(this, urls);
                        else if (urls.Count == 1 && _page != null)
                            _page.Url = urls[0];
                        // 如果不是 URL，不消费，让 Chromium 执行默认粘贴
                    });
                }
                else if (msg == "ctrl-l")
                {
                    Dispatcher.Invoke(() => AddressFocusRequested?.Invoke(this, EventArgs.Empty));
                }
                else if (msg == "f11")
                {
                    Dispatcher.Invoke(() => MaximizeRequested?.Invoke(this, EventArgs.Empty));
                }
                else if (msg == "esc")
                {
                    Dispatcher.Invoke(() => EscapePressed?.Invoke(this, EventArgs.Empty));
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
            // Header 条颜色跟随激活状态
            _headerBar.Background = isActive
                ? new SolidColorBrush(Color.FromArgb(0xFF, 0x0A, 0x1E, 0x35))
                : new SolidColorBrush(Color.FromArgb(0xFF, 0x0D, 0x0D, 0x1B));
            _headerBar.BorderBrush = isActive
                ? new SolidColorBrush(Color.FromArgb(0xAA, 0x4F, 0xC3, 0xF7))
                : new SolidColorBrush(Color.FromArgb(0x44, 0x4F, 0xC3, 0xF7));
            _indexLabel.Foreground = isActive
                ? new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7))
                : new SolidColorBrush(Color.FromRgb(0x60, 0x7D, 0x8B));
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

            // 复制页面链接（始终读 CoreWebView2.Source，反映 SPA 路由后的真实地址）
            string currentUrl = _webView?.CoreWebView2?.Source ?? _page?.Url ?? string.Empty;
            bool hasPageUrl = !string.IsNullOrEmpty(currentUrl) && currentUrl != "about:blank";
            var copyItem = env.CreateContextMenuItem(
                "🔗  复制页面链接", null, CoreWebView2ContextMenuItemKind.Command);
            copyItem.IsEnabled = hasPageUrl;
            copyItem.CustomItemSelected += (s, _) =>
                Dispatcher.Invoke(() =>
                {
                    string url = _webView?.CoreWebView2?.Source ?? _page?.Url ?? string.Empty;
                    if (!string.IsNullOrEmpty(url)) Clipboard.SetText(url);
                });
            items.Add(copyItem);

            // 用外部浏览器打开（同样用 Source）
            var extItem = env.CreateContextMenuItem(
                "🌐  用外部浏览器打开", null, CoreWebView2ContextMenuItemKind.Command);
            extItem.IsEnabled = hasPageUrl;
            extItem.CustomItemSelected += (s, _) =>
                Dispatcher.Invoke(OpenInExternalBrowser);
            items.Add(extItem);

            // 分割线
            items.Add(env.CreateContextMenuItem(
                string.Empty, null, CoreWebView2ContextMenuItemKind.Separator));

            // 收藏到书签 / 取消收藏
            string bmUrl = _webView?.CoreWebView2?.Source ?? _page?.Url ?? string.Empty;
            bool hasBmUrl = !string.IsNullOrEmpty(bmUrl) && bmUrl != "about:blank";
            bool isBookmarked = hasBmUrl && BookmarkStore.Contains(bmUrl);
            var bmItem = env.CreateContextMenuItem(
                isBookmarked ? "★  从书签移除" : "☆  添加到书签",
                null, CoreWebView2ContextMenuItemKind.Command);
            bmItem.IsEnabled = hasBmUrl;
            bmItem.CustomItemSelected += (s, _) =>
                Dispatcher.Invoke(() =>
                {
                    string u = _webView?.CoreWebView2?.Source ?? _page?.Url ?? string.Empty;
                    if (string.IsNullOrEmpty(u)) return;
                    string title = _page?.Title ?? u;
                    if (BookmarkStore.Contains(u))
                        BookmarkStore.Remove(u);
                    else
                        BookmarkStore.Add(u, title);
                });
            items.Add(bmItem);
        }

        // ── 辅助方法 ──────────────────────────────────────────────────────────────

        /// <summary>将当前格子页面添加到书签（由 MainWindow Ctrl+D 调用）</summary>
        public void AddBookmark()
        {
            string url = _webView?.CoreWebView2?.Source ?? _page?.Url ?? string.Empty;
            if (string.IsNullOrEmpty(url) || url == "about:blank") return;
            string title = _page?.Title ?? url;
            if (BookmarkStore.Contains(url))
                BookmarkStore.Remove(url);
            else
                BookmarkStore.Add(url, title);
        }

        private static bool IsValidUrl(string text) =>
            Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        private void OpenInExternalBrowser()
        {
            // 优先用 CoreWebView2.Source，它始终反映当前真实地址（含 SPA 路由跳转后的地址）
            string url = _webView?.CoreWebView2?.Source ?? _page?.Url ?? string.Empty;
            if (string.IsNullOrEmpty(url) || url == "about:blank") return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch { /* 忽略启动外部浏览器失败 */ }
        }
    }
}
