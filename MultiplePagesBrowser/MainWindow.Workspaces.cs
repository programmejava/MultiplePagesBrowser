using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MultiplePagesBrowser.Controls;
using MultiplePagesBrowser.Models;
using MultiplePagesBrowser.Views;

namespace MultiplePagesBrowser;

public partial class MainWindow
{
    private readonly WorkspaceStore _workspaceStore = new();
    private List<WorkspaceData> _workspaces = new();
    private bool _workspaceLoadFailed;

    private void LayoutSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LayoutSelector.SelectedIndex >= 0 && LayoutSelector.SelectedIndex != _vm.LayoutIndex)
            _vm.LayoutIndex = LayoutSelector.SelectedIndex;
    }
    private void SoundSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SoundSelector.SelectedIndex >= 0 && SoundSelector.SelectedIndex != (int)_vm.SoundMode)
            _vm.SoundMode = (SoundMode)SoundSelector.SelectedIndex;
    }
    private void ResetProportions_Click(object sender, RoutedEventArgs e)
    {
        _vm.ResetProportions();
        RefreshGrid();
    }
    private void DisposeTiles()
    {
        if (_isTileMaximized) RestoreTileFromMaximize();
        foreach (var tile in _tilePool.Values) tile.Dispose();
        PageGrid.Children.Clear();
        _activeTiles.Clear();
        _tilePool.Clear();
    }
    private void BuildResizableGrid()
    {
        PageGrid.ColumnDefinitions.Clear();
        PageGrid.RowDefinitions.Clear();
        for (int c = 0; c < _vm.Columns; c++)
        {
            if (c > 0) PageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            PageGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(_vm.ColumnWeights[c], GridUnitType.Star), MinWidth = 90
            });
        }
        for (int r = 0; r < _vm.Rows; r++)
        {
            if (r > 0) PageGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            PageGrid.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(_vm.RowWeights[r], GridUnitType.Star), MinHeight = 75
            });
        }
        for (int c = 1; c < _vm.Columns; c++)
        {
            var splitter = Splitter(GridResizeDirection.Columns);
            Grid.SetColumn(splitter, c * 2 - 1);
            Grid.SetRowSpan(splitter, _vm.Rows * 2 - 1);
            PageGrid.Children.Add(splitter);
        }
        for (int r = 1; r < _vm.Rows; r++)
        {
            var splitter = Splitter(GridResizeDirection.Rows);
            Grid.SetRow(splitter, r * 2 - 1);
            if (_vm.LayoutIndex == 7) Grid.SetColumn(splitter, 2);
            else Grid.SetColumnSpan(splitter, _vm.Columns * 2 - 1);
            PageGrid.Children.Add(splitter);
        }
    }
    private GridSplitter Splitter(GridResizeDirection direction)
    {
        var splitter = new GridSplitter
        {
            ResizeDirection = direction, ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromRgb(35, 54, 75)),
            ShowsPreview = true, Cursor = direction == GridResizeDirection.Columns ? Cursors.SizeWE : Cursors.SizeNS
        };
        splitter.DragCompleted += (_, _) => CaptureProportions();
        splitter.PreviewKeyUp += (_, _) => CaptureProportions();
        return splitter;
    }
    private void CaptureProportions()
    {
        if (PageGrid.ActualWidth <= 0 || PageGrid.ActualHeight <= 0) return;
        for (int c = 0; c < _vm.Columns; c++)
            _vm.ColumnWeights[c] = Math.Max(.05, PageGrid.ColumnDefinitions[c * 2].ActualWidth / 100);
        for (int r = 0; r < _vm.Rows; r++)
            _vm.RowWeights[r] = Math.Max(.05, PageGrid.RowDefinitions[r * 2].ActualHeight / 100);
    }
    private void PlaceTile(WebTile tile, int index)
    {
        Grid.SetRowSpan(tile, 1);
        Grid.SetColumnSpan(tile, 1);
        if (_vm.LayoutIndex == 7)
        {
            Grid.SetRow(tile, index == 2 ? 2 : 0);
            Grid.SetColumn(tile, index == 0 ? 0 : 2);
            if (index == 0) Grid.SetRowSpan(tile, 3);
        }
        else
        {
            Grid.SetRow(tile, index / _vm.Columns * 2);
            Grid.SetColumn(tile, index % _vm.Columns * 2);
        }
    }

    private void ReloadWorkspaces()
    {
        try { _workspaces = _workspaceStore.Load(); }
        catch (Exception ex)
        {
            _workspaceLoadFailed = true;
            StatusBar.Text = $"工作区读取失败，已阻止覆盖原文件：{ex.Message}";
        }
        RebindWorkspaces(null);
    }
    private void RebindWorkspaces(string? name)
    {
        WorkspaceSelector.ItemsSource = null;
        WorkspaceSelector.ItemsSource = _workspaces;
        WorkspaceSelector.SelectedItem = _workspaces.FirstOrDefault(w => w.Name == name);
    }
    private void OpenWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceSelector.SelectedItem is not WorkspaceData selected)
        { StatusBar.Text = "请先选择工作区"; return; }
        _vm.RestoreWorkspace(selected);
        StatusBar.Text = $"已打开工作区：{selected.Name}（未保存的页面配置已替换）";
    }
    private void SaveWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceSelector.SelectedItem is WorkspaceData selected) SaveWorkspace(selected.Name);
        else SaveWorkspaceAs_Click(sender, e);
    }
    private void SaveWorkspaceAs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextPromptWindow("另存工作区", "工作区名称", "新工作区") { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (_workspaces.Any(w => string.Equals(w.Name, dialog.Value, StringComparison.OrdinalIgnoreCase)))
        { StatusBar.Text = "名称已存在，请选择该工作区后点击保存，或使用其他名称"; return; }
        SaveWorkspace(dialog.Value);
    }
    private void SaveWorkspace(string name)
    {
        if (_workspaceLoadFailed) { StatusBar.Text = "工作区文件读取失败，请修复文件后重启，避免覆盖原数据"; return; }
        // 最大化时保留原比例；平铺时读取最新分隔线位置。
        if (!_isTileMaximized) CaptureProportions();
        var snapshot = _vm.CaptureWorkspace(name);
        var updated = _workspaces.Where(w => w.Name != name).Append(snapshot).ToList();
        try
        {
            _workspaceStore.Save(updated);
            _workspaces = updated;
            RebindWorkspaces(name);
            StatusBar.Text = $"已保存工作区：{name}";
        }
        catch (Exception ex) { StatusBar.Text = $"工作区保存失败：{ex.Message}"; }
    }
    private void DeleteWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceSelector.SelectedItem is not WorkspaceData selected) return;
        if (MessageBox.Show(this, $"删除工作区“{selected.Name}”？当前打开的页面会保留。", "删除工作区",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (_workspaceLoadFailed) return;
        var updated = _workspaces.Where(w => w != selected).ToList();
        try
        {
            _workspaceStore.Save(updated);
            _workspaces = updated;
            RebindWorkspaces(null);
            StatusBar.Text = "工作区已删除，当前页面仍保留";
        }
        catch (Exception ex) { StatusBar.Text = $"删除工作区失败：{ex.Message}"; }
    }
}
