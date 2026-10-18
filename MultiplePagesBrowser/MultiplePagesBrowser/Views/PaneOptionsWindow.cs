using System.Windows;
using System.Windows.Controls;
using MultiplePagesBrowser.Models;

namespace MultiplePagesBrowser.Views;

public class PaneOptionsWindow : Window
{
    public PaneOptionsWindow(PageItem page)
    {
        Title = $"窗格 {page.Index + 1} · 运行与刷新"; Width = 390; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "运行策略" });
        var mode = new ComboBox { ItemsSource = new[] { "持续运行（默认）", "节能（非激活时暂停视频、降低内存）", "暂停（隐藏网页并尝试挂起）" },
            SelectedIndex = (int)page.RunMode, Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(mode);
        panel.Children.Add(new TextBlock { Text = "持续运行不随焦点切换暂停，适合实时数据和聊天。\n暂停模式停止定时刷新；恢复后重新计时。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
        panel.Children.Add(new TextBlock { Text = "自动刷新间隔（秒，0 为关闭，最大 86400）" });
        var interval = new TextBox { Text = page.RefreshIntervalSeconds.ToString(), Margin = new Thickness(0, 8, 0, 10) };
        panel.Children.Add(interval);
        var paused = new CheckBox { Content = "暂停定时刷新", IsChecked = page.AutoRefreshPaused };
        panel.Children.Add(paused);
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "应用", IsDefault = true, Padding = new Thickness(18, 5, 18, 5) };
        save.Click += (_, _) =>
        {
            if (!int.TryParse(interval.Text, out int seconds) || seconds < 0 || seconds > 86400)
            { error.Text = "请输入 0 到 86400 的整数。"; return; }
            page.RunMode = (PaneRunMode)Math.Max(0, mode.SelectedIndex);
            page.RefreshIntervalSeconds = seconds;
            page.AutoRefreshPaused = paused.IsChecked == true;
            DialogResult = true;
        };
        buttons.Children.Add(save);
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(18, 5, 18, 5) });
        panel.Children.Add(buttons); Content = panel;
    }
}
