using System.Windows;
using System.Windows.Controls;
using MultiplePagesBrowser.Models;
using MultiplePagesBrowser.ViewModels;

namespace MultiplePagesBrowser.Views
{
    public partial class BookmarksWindow : Window
    {
        private readonly MainViewModel _vm;

        /// <summary>用户选择了在格子内打开的 URL，由 MainWindow 处理</summary>
        public event Action<string>? OpenUrlRequested;

        public BookmarksWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            BookmarkStore.Load();
            RefreshList();

            SearchBox.TextChanged += SearchBox_TextChanged;
            SearchBox.GotFocus += (s, e) =>
                SearchPlaceholder.Visibility = Visibility.Collapsed;
            SearchBox.LostFocus += (s, e) =>
            {
                if (string.IsNullOrEmpty(SearchBox.Text))
                    SearchPlaceholder.Visibility = Visibility.Visible;
            };
        }

        private void RefreshList(string filter = "")
        {
            var items = BookmarkStore.Items
                .Where(b => string.IsNullOrEmpty(filter) ||
                            b.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            b.Url.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            BookmarkList.ItemsSource = items;
            TxtCount.Text = $"（共 {BookmarkStore.Items.Count} 条）";
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
            => RefreshList(SearchBox.Text);

        private void BookmarkList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (BookmarkList.SelectedItem is Bookmark b)
                OpenUrlRequested?.Invoke(b.Url);
        }

        private void CtxOpen_Click(object sender, RoutedEventArgs e)
        {
            if (BookmarkList.SelectedItem is Bookmark b)
                OpenUrlRequested?.Invoke(b.Url);
        }

        private void CtxOpenNew_Click(object sender, RoutedEventArgs e)
        {
            if (BookmarkList.SelectedItem is not Bookmark b) return;
            // 找第一个空白格子，没有则在激活格子打开
            var blank = _vm.Pages.FirstOrDefault(p =>
                p.Url == "about:blank" || string.IsNullOrEmpty(p.Url));
            if (blank != null)
            {
                _vm.ActivePage = blank;
                blank.Url = b.Url;
            }
            else
            {
                OpenUrlRequested?.Invoke(b.Url);
            }
        }

        private void CtxDelete_Click(object sender, RoutedEventArgs e)
        {
            if (BookmarkList.SelectedItem is Bookmark b)
            {
                BookmarkStore.Remove(b.Url);
                RefreshList(SearchBox.Text);
            }
        }

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            if (BookmarkList.SelectedItem is Bookmark b)
                OpenUrlRequested?.Invoke(b.Url);
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (BookmarkList.SelectedItem is Bookmark b)
            {
                BookmarkStore.Remove(b.Url);
                RefreshList(SearchBox.Text);
            }
        }

        private void BtnClearAll_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("确定清空所有书签？此操作不可撤销。",
                    "确认清空", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            BookmarkStore.Clear();
            RefreshList();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
