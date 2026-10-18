using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MultiplePagesBrowser.Models;
using MultiplePagesBrowser.ViewModels;

namespace MultiplePagesBrowser.Controls;

public partial class WebTile
{
    // 同一用户目录明确复用同一个环境创建任务；独立账号使用各自环境。
    private static readonly Dictionary<string, Task<CoreWebView2Environment>> Environments = new(StringComparer.OrdinalIgnoreCase);
    private static Task<CoreWebView2Environment> GetEnvironment(string folder)
    {
        if (!Environments.TryGetValue(folder, out var task) || task.IsFaulted || task.IsCanceled)
        {
            Directory.CreateDirectory(folder);
            task = CoreWebView2Environment.CreateAsync(null, folder,
                new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true });
            Environments[folder] = task;
        }
        return task;
    }
    private async Task InitializeAsync()
    {
        if (_disposed || _initializing || _ready || _page == null) return;
        _initializing = true;
        _message.Visibility = Visibility.Visible;
        _messageText.Text = "正在初始化浏览器…";
        WebView2? browser = null;
        try
        {
            browser = new WebView2();
            _webView = browser; _host.Content = browser;
            var folder = ViewModel?.SharedCookies != false ? MainViewModel.SharedUserDataFolder : _page.UserDataFolder;
            var environment = await GetEnvironment(folder);
            if (_disposed) return;
            await browser.EnsureCoreWebView2Async(environment);
            if (_disposed) return;
            var core = browser.CoreWebView2;
            await ExtensionManager.LoadExtensionsAsync(core);
            if (_disposed) return;
            foreach (var script in UserScriptManager.GetAllScripts().Where(s => s.Enabled && s.RunAt == "document_start"))
            {
                await core.AddScriptToExecuteOnDocumentCreatedAsync(WrapScript(script));
                if (_disposed) return;
            }
            await core.AddScriptToExecuteOnDocumentCreatedAsync(HostScript);
            if (_disposed) return;
            core.NewWindowRequested += (_, e) => { e.Handled = true; if (_page != null) _page.Url = e.Uri; };
            core.ContextMenuRequested += ContextMenuRequested;
            core.DocumentTitleChanged += (_, _) => { if (!_disposed && _page != null) _page.Title = core.DocumentTitle; };
            core.SourceChanged += (_, _) => SyncSource(core.Source);
            core.NavigationStarting += (_, _) =>
            {
                if (_disposed || _page == null) return;
                _page.IsLoading = true; _lastStatus = "加载中…"; UpdateHeader();
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (_disposed || _page == null) return;
                _page.IsLoading = false;
                SyncSource(core.Source);
                _lastStatus = e.IsSuccess ? $"最后加载：{DateTime.Now:HH:mm:ss}" : $"加载失败：{e.WebErrorStatus}（可点击刷新重试）";
                _schedule.Reset(); UpdateHeader();
                if (e.IsSuccess)
                    foreach (var script in UserScriptManager.GetMatchingScripts(core.Source, "document_end"))
                        _ = ExecuteScriptAsync(script.Source);
                _ = ApplyPolicyAsync();
            };
            core.WebMessageReceived += WebMessageReceived;
            browser.ZoomFactorChanged += (_, _) =>
            {
                if (_disposed || _page == null) return;
                _page.ZoomFactor = browser.ZoomFactor;
                if (Math.Abs(browser.ZoomFactor - _page.ZoomFactor) > .001)
                    browser.ZoomFactor = _page.ZoomFactor;
            };
            _ready = true;
            browser.ZoomFactor = _page.ZoomFactor;
            _message.Visibility = Visibility.Collapsed;
            string url = _pendingUrl ?? _page.Url;
            _pendingUrl = null;
            NavigateTo(url);
            await ApplyPolicyAsync();
            if (!_disposed) _timer.Start();
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _ready = false; _suspended = false;
                browser?.Dispose(); _webView = null; _host.Content = null;
                _lastStatus = "浏览器初始化失败：" + ex.Message;
                _messageText.Text = _lastStatus; _message.Visibility = Visibility.Visible;
                UpdateHeader();
            }
        }
        finally { _initializing = false; }
    }
    private void SyncSource(string url)
    {
        if (_disposed || _page == null || string.IsNullOrEmpty(url)) return;
        _syncingUrl = true;
        try { _page.Url = url; }
        finally { _syncingUrl = false; }
    }
    private void NavigateTo(string url)
    {
        if (_disposed || _page == null) return;
        _schedule.Reset();
        if (!_ready || _webView?.CoreWebView2 == null || _page.RunMode == PaneRunMode.Paused)
        { _pendingUrl = url; return; }
        try
        {
            _webView.CoreWebView2.Resume(); _suspended = false;
            var normalized = MainViewModel.NormalizeUrl(url);
            if (normalized == null) { _lastStatus = "地址无效，请输入 http/https 网址"; UpdateHeader(); return; }
            _webView.CoreWebView2.Navigate(normalized);
        }
        catch (Exception ex) { ReportError(ex); }
    }
    private async Task ExecuteScriptAsync(string script)
    {
        try
        {
            if (!_disposed && _ready && _webView?.CoreWebView2 is { } core)
                await core.ExecuteScriptAsync(script);
        }
        catch (Exception ex) { if (!_disposed) ReportError(ex); }
    }
    private void ReportError(Exception error)
    { _lastStatus = error.Message; UpdateHeader(); }

    private const string PauseVideos = "document.querySelectorAll('video').forEach(v=>{if(!v.paused){v.pause();v.dataset.multiGridPaused='1';}});";
    private const string ResumeVideos = "document.querySelectorAll('video').forEach(v=>{if(v.dataset.multiGridPaused){delete v.dataset.multiGridPaused;v.play().catch(()=>{});}});";
    private async Task ApplyPolicyAsync()
    {
        if (_disposed || !_ready) return;
        await _policyGate.WaitAsync();
        try
        {
            if (_disposed || !_ready || _page == null || _webView?.CoreWebView2 is not { } core) return;
            bool pause = _page.RunMode == PaneRunMode.Paused;
            bool eco = _page.RunMode == PaneRunMode.Eco && !_page.IsActive;
            core.IsMuted = pause || _page.ShouldMute(ViewModel?.SoundMode ?? SoundMode.ActiveOnly);
            if (pause)
            {
                await ExecuteScriptAsync(PauseVideos);
                if (_disposed || _page.RunMode != PaneRunMode.Paused) return;
                _host.Visibility = Visibility.Hidden;
                _message.Visibility = Visibility.Visible;
                _messageText.Text = "此窗格已暂停，定时刷新已停止。";
                if (!_suspended) _suspended = await core.TrySuspendAsync();
                if (_disposed) return;
                _messageText.Text = _suspended ? "此窗格已暂停，定时刷新已停止。" : "已暂停显示和视频；浏览器暂未挂起。";
            }
            else
            {
                core.Resume(); _suspended = false;
                _host.Visibility = Visibility.Visible; _message.Visibility = Visibility.Collapsed;
                core.MemoryUsageTargetLevel = eco && ViewModel?.Settings.LowMemoryForInactive == true
                    ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
                await ExecuteScriptAsync(eco && ViewModel?.Settings.PauseVideosWhenInactive == true ? PauseVideos : ResumeVideos);
                if (_disposed) return;
                if (_pendingUrl is { } pending)
                {
                    _pendingUrl = null;
                    NavigateTo(pending);
                }
            }
            UpdateHeader();
        }
        catch (Exception ex) { if (!_disposed) ReportError(ex); }
        finally { _policyGate.Release(); }
    }
    private async Task BrowserActionAsync(Action<CoreWebView2> action)
    {
        if (_page == null || _disposed) return;
        if (_page.RunMode == PaneRunMode.Paused) _page.RunMode = PaneRunMode.Continuous;
        await ApplyPolicyAsync();
        try { if (!_disposed && _ready && _webView?.CoreWebView2 is { } core) action(core); }
        catch (Exception ex) { if (!_disposed) ReportError(ex); }
    }
    public void GoBack() => _ = BrowserActionAsync(c => { if (c.CanGoBack) c.GoBack(); });
    public void GoForward() => _ = BrowserActionAsync(c => { if (c.CanGoForward) c.GoForward(); });
    public void Reload()
    {
        _schedule.Reset();
        if (!_ready) { _ = InitializeAsync(); return; }
        _ = BrowserActionAsync(c => c.Reload());
    }
    public void StopLoading()
    {
        if (_disposed || !_ready) return;
        try { _webView?.CoreWebView2.Stop(); }
        catch (Exception ex) { ReportError(ex); }
    }
    private void WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed || _page == null) return;
        try
        {
            switch (e.TryGetWebMessageAsString())
            {
                case "tile-clicked":
                    if (!_page.IsActive) TileActivationRequested?.Invoke(this, _page);
                    break;
                case "copy-url":
                    if (_page.Url != "about:blank") Clipboard.SetText(_page.Url);
                    break;
                case "ctrl-v": HandlePaste(); break;
                case "ctrl-l": AddressFocusRequested?.Invoke(this, EventArgs.Empty); break;
                case "f11": MaximizeRequested?.Invoke(this, EventArgs.Empty); break;
                case "esc": EscapePressed?.Invoke(this, EventArgs.Empty); break;
            }
        }
        catch (Exception ex) { ReportError(ex); }
    }
    private const string HostScript = """
        (() => {
          if (window.__multiGridHost) return;
          window.__multiGridHost = true;
          const send = message => window.chrome.webview.postMessage(message);
          document.addEventListener('mousedown', () => send('tile-clicked'), {capture:true, passive:true});
          document.addEventListener('keydown', e => {
            const ctrl = e.ctrlKey || e.metaKey;
            const a = document.activeElement;
            const editing = !!a && (a.matches('input,textarea') || a.isContentEditable);
            const key = e.key.toLowerCase();
            if (ctrl && key === 'v' && !editing) {
              e.preventDefault(); send('ctrl-v');
            } else if (ctrl && key === 'c' && !editing && !window.getSelection()?.toString()) {
              e.preventDefault(); send('copy-url');
            } else if (ctrl && key === 'l') {
              e.preventDefault(); send('ctrl-l');
            } else if (e.key === 'F11') {
              e.preventDefault(); send('f11');
            } else if (e.key === 'Escape') send('esc');
          }, true);
        })();
        """;
    private static string WrapScript(UserScript script)
    {
        // 用 JSON 字符串构造 RegExp，避免 URL 中的斜杠破坏 JS 字面量。
        string Test(IEnumerable<string> patterns) => string.Join("||", patterns.Select(p =>
            $"new RegExp({JsonSerializer.Serialize(UserScript.UrlRegex(p))},'i').test(u)"));
        string includes = script.MatchPatterns.Count == 0 ? "true" : Test(script.MatchPatterns);
        string excludes = script.ExcludePatterns.Count == 0 ? "false" : Test(script.ExcludePatterns);
        return $"(()=>{{const u=location.href;if(({includes})&&!({excludes})){{\n{script.Source}\n}}}})();";
    }
    private void ContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        if (_disposed || _webView?.CoreWebView2 is not { } core) return;
        var env = core.Environment;
        void Item(string title, bool enabled, Action action)
        {
            var item = env.CreateContextMenuItem(title, null, CoreWebView2ContextMenuItemKind.Command);
            item.IsEnabled = enabled;
            item.CustomItemSelected += (_, _) => Dispatcher.Invoke(action);
            e.MenuItems.Add(item);
        }
        e.MenuItems.Add(env.CreateContextMenuItem("", null, CoreWebView2ContextMenuItemKind.Separator));
        string clip = "";
        try { clip = Clipboard.GetText(); } catch (System.Runtime.InteropServices.ExternalException) { }
        bool hasPage = _page?.Url is { } url && url != "about:blank";
        Item("粘贴并打开链接", MainViewModel.ExtractUrls(clip).Count > 0, HandlePaste);
        Item("复制页面链接", hasPage, () => Clipboard.SetText(_page!.Url));
        Item("用外部浏览器打开", hasPage, () =>
        {
            try { Process.Start(new ProcessStartInfo(_page!.Url) { UseShellExecute = true }); }
            catch (Exception ex) { ReportError(ex); }
        });
        Item(BookmarkStore.Contains(_page?.Url ?? "") ? "从书签移除" : "添加到书签", hasPage, AddBookmark);
    }
    public void AddBookmark()
    {
        if (_page == null || _page.Url == "about:blank") return;
        if (BookmarkStore.Contains(_page.Url)) BookmarkStore.Remove(_page.Url);
        else BookmarkStore.Add(_page.Url, _page.Title);
    }
}
