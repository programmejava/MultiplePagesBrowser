using System.IO;
using System.Windows;
using System.Windows.Controls;
using MultiplePagesBrowser.Models;

namespace MultiplePagesBrowser.Views
{
    public partial class PluginsWindow : Window
    {
        private bool _suppressCheckEvents;

        public PluginsWindow()
        {
            InitializeComponent();
            RefreshExtensions();
            RefreshScripts();
        }

        // ══════════════════════════════════════════════════════════
        //  Chrome 扩展 Tab
        // ══════════════════════════════════════════════════════════

        private void RefreshExtensions()
        {
            _suppressCheckEvents = true;
            var list = ExtensionManager.GetInstalledExtensions();
            ExtensionList.ItemsSource = null;
            ExtensionList.ItemsSource = list;
            _suppressCheckEvents = false;

            int enabled = list.Count(x => x.Enabled);
            ExtStatus.Text = list.Count == 0
                ? "未检测到扩展，将解压后的扩展文件夹放入扩展目录后点刷新"
                : $"共 {list.Count} 个扩展，{enabled} 个已启用（禁用/启用更改后重启程序生效）";

            ClearExtDetail();
            BtnRemoveExt.IsEnabled = false;
        }

        private void ExtensionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ExtensionList.SelectedItem is ExtensionInfo ext)
            {
                BtnRemoveExt.IsEnabled    = true;
                ExtDetailName.Text        = ext.Name;
                ExtDetailVersion.Text     = string.IsNullOrEmpty(ext.Version) ? "-" : ext.Version;
                ExtDetailFolder.Text      = ext.FolderName;
                ExtDetailDesc.Text        = string.IsNullOrEmpty(ext.Description) ? "-" : ext.Description;
                ExtDetailStatus.Text      = ext.Enabled ? "已启用" : "已禁用";
                ExtDetailStatus.Foreground = ext.Enabled
                    ? System.Windows.Media.Brushes.LightGreen
                    : System.Windows.Media.Brushes.Salmon;
            }
            else
            {
                BtnRemoveExt.IsEnabled = false;
                ClearExtDetail();
            }
        }

        private void ClearExtDetail()
        {
            ExtDetailName.Text    = "-";
            ExtDetailVersion.Text = "-";
            ExtDetailFolder.Text  = "-";
            ExtDetailDesc.Text    = "-";
            ExtDetailStatus.Text  = "-";
            ExtDetailStatus.Foreground = System.Windows.Media.Brushes.Gray;
        }

        private void ExtEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressCheckEvents) return;
            if (sender is CheckBox cb && cb.DataContext is ExtensionInfo ext)
            {
                ExtensionManager.SetEnabled(ext.FolderName, ext.Enabled);
                ExtDetailStatus.Text = ext.Enabled ? "已启用" : "已禁用";
                ExtDetailStatus.Foreground = ext.Enabled
                    ? System.Windows.Media.Brushes.LightGreen
                    : System.Windows.Media.Brushes.Salmon;
                var all = ExtensionList.ItemsSource as List<ExtensionInfo>;
                int total   = all?.Count ?? 0;
                int enabled = all?.Count(x => x.Enabled) ?? 0;
                ExtStatus.Text = $"共 {total} 个扩展，{enabled} 个已启用（禁用/启用更改后重启程序生效）";
            }
        }

        private void BtnOpenExtDir_Click(object sender, RoutedEventArgs e)
        {
            ExtensionManager.OpenExtensionsFolder();
            ExtStatus.Text = "已打开扩展目录，放入扩展后点「刷新列表」";
        }

        private void BtnRefreshExt_Click(object sender, RoutedEventArgs e) => RefreshExtensions();

        private void BtnRemoveExt_Click(object sender, RoutedEventArgs e)
        {
            if (ExtensionList.SelectedItem is not ExtensionInfo ext) return;
            var r = MessageBox.Show(
                $"确定要删除扩展「{ext.Name}」吗？\n将删除整个扩展文件夹：{ext.FolderName}",
                "删除扩展", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            try
            {
                Directory.Delete(ext.FolderPath, recursive: true);
                ExtStatus.Text = $"已删除「{ext.Name}」，重启程序后生效";
                RefreshExtensions();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"删除失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ══════════════════════════════════════════════════════════
        //  用户脚本 Tab
        // ══════════════════════════════════════════════════════════

        private List<ScriptViewModel> _scripts = new();

        private void RefreshScripts()
        {
            _suppressCheckEvents = true;
            _scripts = UserScriptManager.GetAllScripts()
                           .Select(s => new ScriptViewModel(s)).ToList();
            ScriptList.ItemsSource = null;
            ScriptList.ItemsSource = _scripts;
            _suppressCheckEvents = false;

            int enabled = _scripts.Count(s => s.Enabled);
            ScriptStatus.Text = _scripts.Count == 0
                ? "暂无脚本，点「创建示例脚本」或手动放入 .js 文件"
                : $"共 {_scripts.Count} 个脚本，{enabled} 个已启用";

            ClearScriptDetail();
            BtnRemoveScript.IsEnabled = false;
            BtnEditScript.IsEnabled   = false;
        }

        private void ScriptList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ScriptList.SelectedItem is ScriptViewModel s)
            {
                BtnRemoveScript.IsEnabled = true;
                BtnEditScript.IsEnabled   = true;
                ScDetailName.Text    = s.Name;
                ScDetailVersion.Text = string.IsNullOrEmpty(s.Version) ? "-" : s.Version;
                ScDetailDesc.Text    = string.IsNullOrEmpty(s.Description) ? "-" : s.Description;
                ScDetailRunAt.Text   = s.RunAt;
                ScDetailMatch.Text   = s.MatchPatterns.Count == 0
                    ? "(全部页面)"
                    : string.Join("\n", s.MatchPatterns);
                ScDetailFile.Text    = s.FileName;
            }
            else
            {
                BtnRemoveScript.IsEnabled = false;
                BtnEditScript.IsEnabled   = false;
                ClearScriptDetail();
            }
        }

        private void ClearScriptDetail()
        {
            ScDetailName.Text    = "-";
            ScDetailVersion.Text = "-";
            ScDetailDesc.Text    = "-";
            ScDetailRunAt.Text   = "-";
            ScDetailMatch.Text   = "-";
            ScDetailFile.Text    = "-";
        }

        private void ScriptEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressCheckEvents) return;
            if (sender is CheckBox cb && cb.DataContext is ScriptViewModel s)
            {
                UserScriptManager.SetEnabled(s.FileName, s.Enabled);
                int enabled = _scripts.Count(x => x.Enabled);
                ScriptStatus.Text = $"共 {_scripts.Count} 个脚本，{enabled} 个已启用";
            }
        }

        private void BtnOpenScriptDir_Click(object sender, RoutedEventArgs e)
        {
            UserScriptManager.OpenScriptsFolder();
            ScriptStatus.Text = "已打开脚本目录，放入 .js 文件后点「刷新」";
        }

        private void BtnCreateSample_Click(object sender, RoutedEventArgs e)
        {
            UserScriptManager.CreateExampleScripts();
            RefreshScripts();
            ScriptStatus.Text = "示例脚本已创建（HideAds 已启用，DarkMode 默认禁用）";
        }

        private void BtnEditScript_Click(object sender, RoutedEventArgs e)
        {
            if (ScriptList.SelectedItem is not ScriptViewModel s) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = s.FilePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开编辑器：{ex.Message}");
            }
        }

        private void BtnRefreshScripts_Click(object sender, RoutedEventArgs e)
        {
            RefreshScripts();
            ScriptStatus.Text = "脚本列表已刷新";
        }

        private void BtnRemoveScript_Click(object sender, RoutedEventArgs e)
        {
            if (ScriptList.SelectedItem is not ScriptViewModel s) return;
            var r = MessageBox.Show(
                $"确定要删除脚本「{s.Name}」吗？\n将删除文件：{s.FileName}",
                "删除脚本", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            try
            {
                File.Delete(s.FilePath);
                RefreshScripts();
                ScriptStatus.Text = $"已删除脚本：{s.Name}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"删除失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }

    internal class ScriptViewModel : UserScript
    {
        public string MatchSummary { get; }

        public ScriptViewModel(UserScript s)
        {
            FilePath    = s.FilePath;
            FileName    = s.FileName;
            Name        = s.Name;
            Version     = s.Version;
            Description = s.Description;
            Source      = s.Source;
            RunAt       = s.RunAt;
            Enabled     = s.Enabled;
            foreach (var p in s.MatchPatterns)   MatchPatterns.Add(p);
            foreach (var p in s.ExcludePatterns) ExcludePatterns.Add(p);

            MatchSummary = MatchPatterns.Count == 0
                ? "(全部页面)"
                : MatchPatterns.Count == 1
                    ? MatchPatterns[0]
                    : $"{MatchPatterns[0]}  +{MatchPatterns.Count - 1}";
        }
    }
}
