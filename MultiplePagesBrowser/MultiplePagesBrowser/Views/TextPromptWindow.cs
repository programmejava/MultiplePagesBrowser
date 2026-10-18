using System.Windows;
using System.Windows.Controls;

namespace MultiplePagesBrowser.Views;

public class TextPromptWindow : Window
{
    private readonly TextBox _input;
    public string Value => _input.Text.Trim();
    public TextPromptWindow(string title, string label, string initial)
    {
        Title = title; Width = 360; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = label });
        _input = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 14), MaxLength = 80 };
        panel.Children.Add(_input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "保存", IsDefault = true, Padding = new Thickness(18, 5, 18, 5) };
        save.Click += (_, _) => { if (Value.Length > 0) DialogResult = true; else _input.Focus(); };
        buttons.Children.Add(save);
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(18, 5, 18, 5) });
        panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }
}
