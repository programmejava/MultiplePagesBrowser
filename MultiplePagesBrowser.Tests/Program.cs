using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MultiplePagesBrowser;
using MultiplePagesBrowser.Controls;
using MultiplePagesBrowser.Models;
using MultiplePagesBrowser.ViewModels;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType()
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static void Call(object instance, string name, params object[] args) => instance.GetType()
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args);

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            StateAndWorkspaceTests();
            RefreshTests();
            WindowTests(args.FirstOrDefault(a => !a.StartsWith("--")));
            if (args.Contains("--browser")) BrowserTests.Run(Check);
            Console.WriteLine($"PASS: {_checks} checks (state, workspaces, refresh timing, WPF layout/maximize regression).");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void StateAndWorkspaceTests()
    {
        var vm = new MainViewModel(new AppSettings { DefaultLayoutIndex = -20 });
        Check(vm.LayoutIndex == 0, "Invalid startup index must be clamped");
        vm.LayoutIndex = 4;
        var hidden = vm.Pages[15]; hidden.Url = "https://hidden.example/";
        vm.LayoutIndex = 5;
        vm.NavigateAll("example.com");
        Check(vm.VisibleCount == 2 && vm.Pages.Count == 16, "Visible and retained pages are different collections");
        Check(hidden.Url == "https://hidden.example/", "Sync navigation must not affect hidden pages");
        var first = vm.Pages[0]; var second = vm.Pages[1];
        string profile = first.UserDataFolder;
        vm.ActivePage = first;
        first.IsMuted = false; first.ZoomFactor = .7; first.RunMode = PaneRunMode.Eco;
        first.RefreshIntervalSeconds = 45;
        vm.SwapPages(first, second);
        Check(vm.Pages[1] == first && first.Index == 1 && first.UserDataFolder == profile, "Swap must preserve page identity/profile");
        Check(vm.ActivePage == first && first.IsActive, "Swap must preserve active page");
        vm.ActivePage = second;
        Check(!first.IsMuted && first.ZoomFactor == .7, "Activation must not change per-pane preferences");
        vm.SoundMode = SoundMode.Muted;
        vm.ActivePage = first;
        Check(first.ShouldMute(vm.SoundMode), "Global mute must survive activation");
        vm.SoundMode = SoundMode.Custom;
        Check(!first.ShouldMute(vm.SoundMode), "Custom mode must restore stored preference");
        first.Url = "https://changed.example/";
        Check(vm.AddressBarText == first.Url, "Source changes must update the address bar");
        vm.ColumnWeights[0] = 2.7; vm.ColumnWeights[1] = 1.2;
        var snapshot = vm.CaptureWorkspace("监控");
        var json = JsonSerializer.Serialize(snapshot);
        var restored = JsonSerializer.Deserialize<WorkspaceData>(json)!;
        bool reset = false; vm.PagesResetting += () => reset = true;
        vm.RestoreWorkspace(restored);
        var pane = vm.Pages[1];
        Check(reset && vm.Pages.Count == 2, "Workspace restore must clear the old browser pool and hidden pages");
        Check(pane.Url == first.Url && pane.UserDataFolder == profile, "Workspace round trip must preserve order and profile");
        Check(pane.ZoomFactor == .7 && pane.RunMode == PaneRunMode.Eco && pane.RefreshIntervalSeconds == 45,
            "Workspace round trip must preserve pane configuration");
        Check(vm.ColumnWeights.SequenceEqual(new[] { 2.7, 1.2 }) && vm.ActivePage == pane,
            "Workspace round trip must preserve proportions and selection");
        var malformed = JsonSerializer.Deserialize<WorkspaceData>("""
            {"LayoutIndex":7,"ColumnWeights":[-1,0],"RowWeights":null,"SoundMode":999,
             "Panes":[{"Url":"file:///secret","ProfileFolder":"../../other","ZoomFactor":99,"RunMode":999,"RefreshIntervalSeconds":-1},null]}
            """)!;
        vm.RestoreWorkspace(malformed);
        Check(vm.VisibleCount == 3 && vm.Pages.Count == 3 && vm.ColumnWeights[0] == 2, "Malformed workspace must receive a valid master layout");
        Check(vm.Pages[0].Url == "about:blank" && vm.Pages[0].ZoomFactor == 3 && vm.Pages[0].RefreshIntervalSeconds == 0,
            "Unsafe URLs and invalid values must be normalized");
        Check(Path.GetFileName(vm.Pages[0].UserDataFolder) == "Profile_0" && vm.Pages[0].RunMode == PaneRunMode.Continuous,
            "Workspace paths cannot escape the profile directory");
        vm.NavigateMultiple(Enumerable.Range(0, 20).Select(i => $"https://example.com/{i}").ToList());
        Check(vm.VisibleCount == 16 && vm.Pages[15].Url.EndsWith("/15"), "Batch navigation must cap at 16");
        vm.NavigateMultiple(new() { "https://a.example", "https://b.example" });
        Check(vm.LayoutIndex == 5, "Two links should use the two-column layout");
        vm.NavigateMultiple(new() { "https://a.example", "https://b.example", "https://c.example" });
        Check(vm.LayoutIndex == 6, "Three links should use the three-column layout");
        Check(MainViewModel.ExtractUrls("a.example b.example\nc.example").Count == 3, "URL parser must support spaces and lines");
        Check(MainViewModel.NormalizeUrl(null) == null, "Null settings/URL values must not crash startup");
        Check(new UserScript { MatchPatterns = { "https://example.com/*" } }.MatchesUrl("https://example.com/a/b"),
            "User script path glob must support nested paths");
        var old = JsonSerializer.Deserialize<SessionData>("""{"LayoutIndex":1,"Urls":["https://example.com/"]}""")!;
        Check(old.HasData && old.Workspace == null && old.LayoutIndex == 1, "Legacy sessions must remain readable");
        string folder = Path.GetFullPath(Path.Combine("artifacts", "tests-" + Guid.NewGuid().ToString("N")));
        string file = Path.Combine(folder, "workspaces.json");
        try
        {
            var store = new WorkspaceStore(file);
            Check(store.Load().Count == 0, "Missing workspace file should be empty");
            store.Save(new[] { snapshot });
            Check(store.Load().Single().Name == "监控" && !File.Exists(file + ".tmp"), "Workspace persistence must round trip atomically");
            File.WriteAllText(file, "{invalid");
            bool failed = false;
            try { store.Load(); } catch (JsonException) { failed = true; }
            Check(failed && File.ReadAllText(file) == "{invalid", "Corrupt workspace files must not be silently overwritten");
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
            if (File.Exists(file + ".tmp")) File.Delete(file + ".tmp");
            if (Directory.Exists(folder)) Directory.Delete(folder);
        }
    }
    private static void RefreshTests()
    {
        var page = new PageItem { Url = "https://example.com/", RefreshIntervalSeconds = 10 };
        var schedule = new RefreshSchedule();
        var now = DateTimeOffset.Parse("2026-10-17T00:00:00Z");
        Check(schedule.RemainingSeconds(page, now) == 10, "Refresh should wait a full interval");
        Check(!schedule.TakeDue(page, now.AddSeconds(9)), "Refresh must not fire early");
        Check(schedule.TakeDue(page, now.AddSeconds(10)) && !schedule.TakeDue(page, now.AddSeconds(10)), "A deadline must fire once");
        page.AutoRefreshPaused = true;
        Check(!schedule.TakeDue(page, now.AddHours(1)), "Paused refresh must not fire");
        page.AutoRefreshPaused = false;
        Check(schedule.RemainingSeconds(page, now.AddHours(1)) == 10, "Resume must start a full interval");
        page.RunMode = PaneRunMode.Paused;
        Check(!schedule.TakeDue(page, now.AddHours(2)), "Paused browser must not refresh");
        page.RunMode = PaneRunMode.Continuous; page.IsLoading = true;
        Check(!schedule.TakeDue(page, now.AddHours(3)), "Loading page must not be interrupted by refresh");
        page.IsLoading = false;
        Check(schedule.RemainingSeconds(page, now.AddHours(4)) == 10, "Loading completion must restart countdown");
        page.Url = "about:blank";
        Check(!schedule.TakeDue(page, now.AddHours(5)), "Blank panes must not auto refresh");
    }
    private static void WindowTests(string? renderPath)
    {
        // 构建真实 WPF 控件但不显示窗口、不启动 Chromium，不读取或写入浏览器用户目录。
        var window = new MainWindow();
        var vm = Field<MainViewModel>(window, "_vm");
        var grid = (Grid)window.FindName("PageGrid");
        try
        {
            for (int layout = 0; layout < MainViewModel.LayoutPresets.Length; layout++)
            {
                vm.LayoutIndex = layout;
                Check(grid.Children.OfType<WebTile>().Count() == vm.VisibleCount &&
                    grid.ColumnDefinitions.Count == vm.Columns * 2 - 1 && grid.RowDefinitions.Count == vm.Rows * 2 - 1,
                    $"Layout {layout} must have the correct pane count and splitter tracks");
            }
            vm.LayoutIndex = 5;
            Check(grid.Children.OfType<WebTile>().Count() == 2 && grid.Children.OfType<GridSplitter>().Count() == 1,
                "Two-column layout must contain two panes and a splitter");
            var original = grid.Children.OfType<WebTile>().First();
            var source = original.Page!;
            vm.SwapPages(source, vm.Pages[1]);
            Check(grid.Children.OfType<WebTile>().Single(t => t.Page == source) == original,
                "UI swap must reuse the same WebTile");
            vm.LayoutIndex = 7;
            var main = grid.Children.OfType<WebTile>().Single(t => t.Page!.Index == 0);
            Check(Grid.GetRowSpan(main) == 3 && grid.Children.OfType<WebTile>().Count() == 3,
                "Master pane must span the two detail rows");
            Call(window, "MaximizeTile", main);
            Check(((Border)window.FindName("MaximizedContainer")).Child == main, "Maximize must reparent the original control");
            vm.NavigateMultiple(new() { "https://a.example/", "https://b.example/" });
            Check(!Field<bool>(window, "_isTileMaximized") && grid.Children.OfType<WebTile>().Count() == 2,
                "Batch navigation while maximized must restore before rebuilding");
            Call(window, "MaximizeTile", grid.Children.OfType<WebTile>().First());
            vm.ApplySettings();
            Check(!Field<bool>(window, "_isTileMaximized"), "Settings while maximized must restore before rebuilding");
            Call(window, "MaximizeTile", grid.Children.OfType<WebTile>().First());
            vm.RestoreWorkspace(new WorkspaceData { LayoutIndex = 6 });
            Check(!Field<bool>(window, "_isTileMaximized") && grid.Children.OfType<WebTile>().Count() == 3,
                "Workspace restore while maximized must detach and dispose the old controls");
            if (renderPath != null)
            {
                vm.LayoutIndex = 7;
                var root = (FrameworkElement)window.Content;
                void Render(int width, int height, string path)
                {
                    root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                    using var stream = File.Create(path); encoder.Save(stream);
                }
                Render(1360, 820, renderPath);
                vm.LayoutIndex = 4;
                Render(800, 500, Path.ChangeExtension(renderPath, ".small.png"));
            }
        }
        finally { Call(window, "DisposeTiles"); }
    }
}
