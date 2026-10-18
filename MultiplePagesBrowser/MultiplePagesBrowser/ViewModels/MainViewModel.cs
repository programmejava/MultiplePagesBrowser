using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using MultiplePagesBrowser.Models;

namespace MultiplePagesBrowser.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    // 保留旧索引，兼容已有设置和会话。
    public static readonly (int cols, int rows)[] LayoutPresets =
        { (1, 1), (2, 2), (3, 3), (4, 3), (4, 4), (2, 1), (3, 1), (2, 2) };
    public string[] LayoutLabels { get; } =
        { "1格", "4格", "9格", "12格", "16格", "双栏", "三栏", "左大右小" };
    public static int LayoutCount(int index) => index == 7 ? 3 :
        LayoutPresets[index].cols * LayoutPresets[index].rows;
    private int _layoutIndex;
    private PageItem? _activePage;
    private string _addressBarText = string.Empty;
    private SoundMode _soundMode = SoundMode.ActiveOnly;
    public AppSettings Settings { get; }
    public static readonly string SharedUserDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MultiplePagesBrowser", "SharedProfile");
    public bool SharedCookies
    {
        get => Settings.SharedCookies;
        set { Settings.SharedCookies = value; OnPropertyChanged(); }
    }
    public ObservableCollection<PageItem> Pages { get; } = new();
    public IEnumerable<PageItem> VisiblePages => Pages.Take(VisibleCount);
    public int Columns => LayoutPresets[_layoutIndex].cols;
    public int Rows => LayoutPresets[_layoutIndex].rows;
    public int VisibleCount => LayoutCount(_layoutIndex);
    public List<double> ColumnWeights { get; private set; } = new();
    public List<double> RowWeights { get; private set; } = new();
    public SoundMode SoundMode
    {
        get => _soundMode;
        set { _soundMode = Enum.IsDefined(value) ? value : SoundMode.ActiveOnly; OnPropertyChanged(); }
    }
    public int LayoutIndex
    {
        get => _layoutIndex;
        set
        {
            if (value < 0 || value >= LayoutPresets.Length) return;
            if (_layoutIndex != value) { _layoutIndex = value; ResetProportions(); }
            OnPropertyChanged();
            ApplyLayout();
        }
    }
    public PageItem? ActivePage
    {
        get => _activePage;
        set
        {
            if (ReferenceEquals(_activePage, value)) return;
            if (_activePage != null)
            {
                _activePage.PropertyChanged -= ActivePageChanged;
                _activePage.IsActive = false;
            }
            _activePage = value;
            if (value != null)
            {
                value.IsActive = true;
                value.PropertyChanged += ActivePageChanged;
            }
            SyncAddress();
            OnPropertyChanged();
        }
    }
    public string AddressBarText
    {
        get => _addressBarText;
        set { _addressBarText = value; OnPropertyChanged(); }
    }
    public event Action? LayoutApplied;
    public event Action? PagesResetting;
    public MainViewModel() : this(AppSettings.Load()) { }
    public MainViewModel(AppSettings settings)
    {
        Settings = settings;
        _layoutIndex = Math.Clamp(settings.DefaultLayoutIndex, 0, LayoutPresets.Length - 1);
        ResetProportions();
        ApplyLayout();
    }
    private void ActivePageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageItem.Url)) SyncAddress();
    }
    private void SyncAddress() => AddressBarText = ActivePage?.Url is { } url && url != "about:blank" ? url : "";
    public void ApplySettings() { OnPropertyChanged(nameof(Settings)); LayoutApplied?.Invoke(); }
    public void ResetProportions()
    {
        ColumnWeights = Enumerable.Repeat(1d, Columns).ToList();
        RowWeights = Enumerable.Repeat(1d, Rows).ToList();
        if (_layoutIndex == 7) ColumnWeights[0] = 2;
    }
    private void ApplyLayout()
    {
        while (Pages.Count < VisibleCount)
        {
            int index = Pages.Count;
            Pages.Add(new PageItem
            {
                Index = index,
                UserDataFolder = Path.Combine(Path.GetDirectoryName(SharedUserDataFolder)!, $"Profile_{index}"),
                Url = NormalizeUrl(Settings.HomePage) ?? "about:blank"
            });
        }
        Reindex();
        if (ActivePage == null || ActivePage.Index >= VisibleCount) ActivePage = Pages[0];
        LayoutApplied?.Invoke();
    }
    private void Reindex() { for (int i = 0; i < Pages.Count; i++) Pages[i].Index = i; }
    public void SwapPages(PageItem source, PageItem target)
    {
        int a = Pages.IndexOf(source), b = Pages.IndexOf(target);
        if (a < 0 || b < 0 || a == b || a >= VisibleCount || b >= VisibleCount) return;
        Pages[a] = target;
        Pages[b] = source;
        Reindex();
        LayoutApplied?.Invoke();
    }
    public static string? NormalizeUrl(string? text)
    {
        text = text?.Trim() ?? "";
        if (text == "about:blank") return text;
        if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsWhiteSpace)) return null;
        if (!text.Contains("://")) text = "https://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri.AbsoluteUri : null;
    }
    public void NavigateActive(string url)
    {
        if (ActivePage != null && NormalizeUrl(url) is { } normalized) ActivePage.Url = normalized;
    }
    public void NavigateAll(string url)
    {
        if (NormalizeUrl(url) is not { } normalized) return;
        foreach (var page in VisiblePages) page.Url = normalized;
    }
    public static List<string> ExtractUrls(string text) => text.Split(
        new[] { '\r', '\n', '\t', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(NormalizeUrl).Where(u => u != null).Cast<string>().ToList();
    public void NavigateMultiple(List<string> urls)
    {
        if (urls.Count == 0) return;
        int needed = Math.Min(urls.Count, 16);
        LayoutIndex = Enumerable.Range(0, LayoutPresets.Length)
            .Where(i => LayoutCount(i) >= needed).OrderBy(LayoutCount).ThenBy(i => i).First();
        for (int i = 0; i < Math.Min(urls.Count, VisibleCount); i++)
            Pages[i].Url = NormalizeUrl(urls[i]) ?? "about:blank";
        ActivePage = Pages[0];
    }
    public WorkspaceData CaptureWorkspace(string name) => new()
    {
        Name = name, LayoutIndex = LayoutIndex,
        ColumnWeights = ColumnWeights.ToList(), RowWeights = RowWeights.ToList(),
        ActiveIndex = ActivePage?.Index ?? 0, SoundMode = SoundMode,
        Panes = VisiblePages.Select(PaneSnapshot.FromPage).ToList()
    };
    public void RestoreWorkspace(WorkspaceData workspace)
    {
        workspace = workspace.ValidatedCopy();
        PagesResetting?.Invoke();
        ActivePage = null;
        Pages.Clear();
        _layoutIndex = workspace.LayoutIndex;
        ColumnWeights = workspace.ColumnWeights;
        RowWeights = workspace.RowWeights;
        SoundMode = workspace.SoundMode;
        foreach (var pane in workspace.Panes) Pages.Add(pane.ToPage(Pages.Count));
        OnPropertyChanged(nameof(LayoutIndex));
        ApplyLayout();
        ActivePage = Pages[Math.Clamp(workspace.ActiveIndex, 0, VisibleCount - 1)];
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
