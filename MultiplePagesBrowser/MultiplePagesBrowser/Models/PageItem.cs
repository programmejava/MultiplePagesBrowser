using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MultiplePagesBrowser.Models;

public enum PaneRunMode { Continuous, Eco, Paused }
public enum SoundMode { ActiveOnly, Custom, Muted }

/// <summary>窗格身份与位置分离；换位时浏览器实例和账号数据跟随对象。</summary>
public class PageItem : INotifyPropertyChanged
{
    private int _index;
    private string _url = "about:blank", _title = "新页面";
    private bool _isActive, _isMuted = true, _isLoading, _autoRefreshPaused;
    private double _zoomFactor = 1;
    private int _refreshIntervalSeconds;
    private PaneRunMode _runMode;

    public int Index { get => _index; set => Set(ref _index, value); }
    public string UserDataFolder { get; set; } = string.Empty;
    public string Url { get => _url; set => Set(ref _url, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    // 用户的声音偏好；实际静音还受全局 SoundMode 控制。
    public bool IsMuted { get => _isMuted; set => Set(ref _isMuted, value); }
    public bool IsLoading { get => _isLoading; set => Set(ref _isLoading, value); }
    public double ZoomFactor
    {
        get => _zoomFactor;
        set => Set(ref _zoomFactor, double.IsFinite(value) ? Math.Clamp(value, .25, 3) : 1);
    }
    public PaneRunMode RunMode
    {
        get => _runMode;
        set => Set(ref _runMode, Enum.IsDefined(value) ? value : PaneRunMode.Continuous);
    }
    public int RefreshIntervalSeconds
    {
        get => _refreshIntervalSeconds;
        set => Set(ref _refreshIntervalSeconds, Math.Clamp(value, 0, 86400));
    }
    public bool AutoRefreshPaused { get => _autoRefreshPaused; set => Set(ref _autoRefreshPaused, value); }
    public bool ShouldMute(SoundMode mode) => mode switch
    {
        SoundMode.Muted => true,
        SoundMode.ActiveOnly => !IsActive,
        _ => IsMuted
    };
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
