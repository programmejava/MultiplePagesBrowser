namespace MultiplePagesBrowser.Models;

/// <summary>不在加载或暂停时积压刷新；恢复后从完整间隔重新计时。</summary>
public class RefreshSchedule
{
    private DateTimeOffset? _due;
    public void Reset() => _due = null;
    public int? RemainingSeconds(PageItem page, DateTimeOffset now)
    {
        if (page.RefreshIntervalSeconds <= 0 || page.AutoRefreshPaused ||
            page.RunMode == PaneRunMode.Paused || page.IsLoading || page.Url == "about:blank")
        {
            Reset();
            return null;
        }
        _due ??= now.AddSeconds(page.RefreshIntervalSeconds);
        return Math.Max(0, (int)Math.Ceiling((_due.Value - now).TotalSeconds));
    }
    public bool TakeDue(PageItem page, DateTimeOffset now)
    {
        if (RemainingSeconds(page, now) != 0) return false;
        _due = now.AddSeconds(page.RefreshIntervalSeconds);
        return true;
    }
}
