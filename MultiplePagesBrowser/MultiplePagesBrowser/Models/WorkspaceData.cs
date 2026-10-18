using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using MultiplePagesBrowser.ViewModels;

namespace MultiplePagesBrowser.Models;

public class PaneSnapshot
{
    public string Url { get; set; } = "about:blank";
    public string ProfileFolder { get; set; } = "";
    public double ZoomFactor { get; set; } = 1;
    public bool IsMuted { get; set; } = true;
    public PaneRunMode RunMode { get; set; }
    public int RefreshIntervalSeconds { get; set; }
    public bool AutoRefreshPaused { get; set; }
    public static PaneSnapshot FromPage(PageItem page) => new()
    {
        Url = page.Url, ProfileFolder = Path.GetFileName(page.UserDataFolder),
        ZoomFactor = page.ZoomFactor, IsMuted = page.IsMuted, RunMode = page.RunMode,
        RefreshIntervalSeconds = page.RefreshIntervalSeconds, AutoRefreshPaused = page.AutoRefreshPaused
    };
    public PageItem ToPage(int index) => new()
    {
        Index = index, Url = MainViewModel.NormalizeUrl(Url ?? "") ?? "about:blank",
        UserDataFolder = Path.Combine(Path.GetDirectoryName(MainViewModel.SharedUserDataFolder)!,
            Regex.IsMatch(ProfileFolder ?? "", @"^Profile_\d{1,5}$") ? ProfileFolder! : $"Profile_{index}"),
        ZoomFactor = ZoomFactor, IsMuted = IsMuted, RunMode = RunMode,
        RefreshIntervalSeconds = RefreshIntervalSeconds, AutoRefreshPaused = AutoRefreshPaused
    };
}

public class WorkspaceData
{
    public string Name { get; set; } = "工作区";
    public int LayoutIndex { get; set; } = 2;
    public int ActiveIndex { get; set; }
    public SoundMode SoundMode { get; set; } = SoundMode.ActiveOnly;
    public List<double> ColumnWeights { get; set; } = new();
    public List<double> RowWeights { get; set; } = new();
    public List<PaneSnapshot> Panes { get; set; } = new();

    public WorkspaceData ValidatedCopy()
    {
        int layout = Math.Clamp(LayoutIndex, 0, MainViewModel.LayoutPresets.Length - 1);
        var (cols, rows) = MainViewModel.LayoutPresets[layout];
        return new WorkspaceData
        {
            Name = string.IsNullOrWhiteSpace(Name) ? "工作区" : Name.Trim(),
            LayoutIndex = layout, ActiveIndex = Math.Clamp(ActiveIndex, 0, MainViewModel.LayoutCount(layout) - 1),
            SoundMode = Enum.IsDefined(SoundMode) ? SoundMode : SoundMode.ActiveOnly,
            ColumnWeights = ValidWeights(ColumnWeights, cols, layout == 7),
            RowWeights = ValidWeights(RowWeights, rows, false),
            Panes = (Panes ?? new()).Where(p => p != null).Take(MainViewModel.LayoutCount(layout))
                .Select((p, i) => PaneSnapshot.FromPage(p.ToPage(i))).ToList()
        };
    }
    private static List<double> ValidWeights(List<double>? weights, int count, bool main)
        => weights?.Count == count && weights.All(v => double.IsFinite(v) && v >= .05 && v <= 10000)
            ? weights.ToList() : Enumerable.Range(0, count).Select(i => main && i == 0 ? 2d : 1d).ToList();
}

/// <summary>只在显式保存时写入；原子替换避免中断后损坏工作区文件。</summary>
public class WorkspaceStore
{
    private readonly string _path;
    public WorkspaceStore(string? path = null) => _path = path ?? Path.Combine(
        Path.GetDirectoryName(MainViewModel.SharedUserDataFolder)!, "workspaces.json");
    public List<WorkspaceData> Load()
    {
        if (!File.Exists(_path)) return new();
        var items = JsonSerializer.Deserialize<List<WorkspaceData>>(File.ReadAllText(_path))
            ?? throw new InvalidDataException("工作区文件内容为空。");
        return items.Where(w => w != null).Select(w => w.ValidatedCopy()).ToList();
    }
    public void Save(IEnumerable<WorkspaceData> items)
    {
        var json = JsonSerializer.Serialize(items.Select(w => w.ValidatedCopy()),
            new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _path, true);
    }
}
