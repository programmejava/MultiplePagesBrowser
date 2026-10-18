using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;
using MultiplePagesBrowser.Controls;
using MultiplePagesBrowser.Models;
using MultiplePagesBrowser.ViewModels;

internal static class BrowserTests
{
    private static T Field<T>(WebTile tile, string name) => (T)typeof(WebTile)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tile)!;
    private static async Task Until(Func<Task<bool>> condition, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(35);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(description);
    }

    public static void Run(Action<bool, string> check)
    {
        // 独立本地 HTTP 页面、独立测试用户目录，不导航到外网。
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        _ = Serve(listener, stop.Token);
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string origin = $"http://127.0.0.1:{port}";
        string profiles = Path.GetFullPath(Path.Combine("artifacts", "browser-tests-" + Guid.NewGuid().ToString("N")));
        var vm = new MainViewModel(new AppSettings { DefaultLayoutIndex = 5, SharedCookies = false });
        vm.SoundMode = SoundMode.Custom;
        for (int i = 0; i < 2; i++)
        {
            vm.Pages[i].Url = origin + "/pane" + i;
            vm.Pages[i].UserDataFolder = Path.Combine(profiles, "Profile_" + i);
        }
        vm.Pages[0].IsMuted = false;
        WebTile.ViewModel = vm;
        var first = new WebTile { Page = vm.Pages[0] };
        var second = new WebTile { Page = vm.Pages[1] };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(second, 1); grid.Children.Add(first); grid.Children.Add(second);
        var window = new Window
        {
            Title = "MultiGrid local integration checks", Content = grid, Width = 1100, Height = 600,
            ShowActivated = false, ShowInTaskbar = false, Left = -15000, Top = -15000,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Exception? failure = null;
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Until(() => Task.FromResult(Field<bool>(first, "_ready") && Field<bool>(second, "_ready")), "WebView2 initialization");
                var browser = Field<WebView2>(first, "_webView");
                var other = Field<WebView2>(second, "_webView");
                await Until(async () => await browser.ExecuteScriptAsync("document.body?.dataset.ready") == "\"yes\"", "First page load");
                await Until(async () => await other.ExecuteScriptAsync("document.body?.dataset.ready") == "\"yes\"", "Second page load");
                check(true, "Both WebView2 panes load local pages");
                vm.Pages[0].ZoomFactor = .8;
                check(Math.Abs(browser.ZoomFactor - .8) < .001, "Pane zoom reaches the real WebView2");
                vm.ActivePage = vm.Pages[1];
                await Until(() => Task.FromResult(!browser.CoreWebView2.IsMuted && other.CoreWebView2.IsMuted), "Independent sound policy");
                check(!vm.Pages[0].IsMuted, "Changing active pane preserves custom sound");
                vm.SoundMode = SoundMode.Muted; first.ApplySettings(); second.ApplySettings();
                await Until(() => Task.FromResult(browser.CoreWebView2.IsMuted && other.CoreWebView2.IsMuted), "Global mute");
                vm.ActivePage = vm.Pages[0];
                first.ApplySettings();
                await Task.Delay(200);
                check(browser.CoreWebView2.IsMuted, "Global mute survives activation in the browser");
                vm.SoundMode = SoundMode.Custom; first.ApplySettings();
                vm.ActivePage = vm.Pages[1];
                int ticks = int.Parse(await browser.ExecuteScriptAsync("Number(document.body.dataset.ticks)"));
                await Until(async () => int.Parse(await browser.ExecuteScriptAsync("Number(document.body.dataset.ticks)")) > ticks,
                    "Inactive continuous pane keeps running");
                check(vm.Pages[0].RunMode == PaneRunMode.Continuous, "Non-active continuous pane remains live");
                vm.ActivePage = vm.Pages[0];
                await browser.ExecuteScriptAsync("history.pushState({},'', '/spa-route')");
                await Until(() => Task.FromResult(vm.AddressBarText == origin + "/spa-route"), "SPA address synchronization");
                check(true, "SPA route updates the address bar");
                vm.Pages[0].RunMode = PaneRunMode.Paused;
                await Until(() => Task.FromResult(Field<ContentPresenter>(first, "_host").Visibility == Visibility.Hidden), "Pause hides WebView2");
                vm.Pages[0].Url = origin + "/resumed";
                await Task.Delay(200);
                check(browser.CoreWebView2.Source.EndsWith("/spa-route"), "Paused pane defers new navigation");
                vm.Pages[0].RunMode = PaneRunMode.Continuous;
                await Until(() => Task.FromResult(browser.CoreWebView2.Source.EndsWith("/resumed") && !vm.Pages[0].IsLoading), "Resume applies deferred navigation");
                check(Field<ContentPresenter>(first, "_host").Visibility == Visibility.Visible, "Resume restores the browser");
                int loads = int.Parse(await browser.ExecuteScriptAsync("Number(localStorage.loads)"));
                vm.Pages[0].RefreshIntervalSeconds = 1;
                await Until(async () => int.Parse(await browser.ExecuteScriptAsync("Number(localStorage.loads)")) > loads, "Automatic refresh reaches the real browser");
                vm.Pages[0].AutoRefreshPaused = true;
                await Until(() => Task.FromResult(!vm.Pages[0].IsLoading), "Refresh completes");
                int pausedLoads = int.Parse(await browser.ExecuteScriptAsync("Number(localStorage.loads)"));
                await Task.Delay(2400);
                check(int.Parse(await browser.ExecuteScriptAsync("Number(localStorage.loads)")) == pausedLoads, "Paused automatic refresh must stop reloading");
                vm.Pages[0].RefreshIntervalSeconds = 0;
                check(Math.Abs(browser.ZoomFactor - .8) < .001, "Zoom survives navigation and automatic refresh");
                Console.WriteLine("PASS: live WebView2 integration (isolated local pages/profiles).");
            }
            catch (Exception ex)
            {
                failure = ex;
                Console.Error.WriteLine("Browser status: " + Field<string>(first, "_lastStatus") + " | " + Field<string>(second, "_lastStatus"));
            }
            finally { first.Dispose(); second.Dispose(); window.Close(); app.Shutdown(); }
        };
        try { app.Run(window); }
        finally { stop.Cancel(); listener.Stop(); }
        if (failure != null) throw new InvalidOperationException("Live browser integration failed", failure);
    }

    private static async Task Serve(TcpListener listener, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellation);
                _ = Respond(client, cancellation);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (cancellation.IsCancellationRequested) { }
    }
    private static async Task Respond(TcpClient client, CancellationToken cancellation)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                while (await reader.ReadLineAsync(cancellation) is { Length: > 0 }) { }
                const string html = """
                    <!doctype html><html><head><title>Local MultiGrid test</title></head><body>
                    <h1>Local browser test</h1><script>
                    localStorage.loads=String(Number(localStorage.loads||0)+1);
                    document.body.dataset.ready='yes';let ticks=0;
                    document.body.dataset.ticks='0';setInterval(()=>document.body.dataset.ticks=String(++ticks),100);
                    </script></body></html>
                    """;
                byte[] body = Encoding.UTF8.GetBytes(html);
                byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, cancellation); await stream.WriteAsync(body, cancellation);
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
        }
    }
}
