using System.Windows;
using MultiplePagesBrowser.Models;
using MultiplePagesBrowser.ViewModels;

namespace MultiplePagesBrowser.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly MainViewModel _vm;

        public SettingsWindow(AppSettings settings, MainViewModel vm)
        {
            InitializeComponent();
            _settings = settings;
            _vm = vm;
            LoadValues();
        }

        private void LoadValues()
        {
            TxtHomePage.Text = _settings.HomePage;
            ChkSharedCookies.IsChecked = _settings.SharedCookies;
            ChkPauseVideos.IsChecked = _settings.PauseVideosWhenInactive;
            ChkLowMemory.IsChecked = _settings.LowMemoryForInactive;
            SliderGap.Value = _settings.GridGap;
            ChkShowNumbers.IsChecked = _settings.ShowTileNumbers;

            CmbLayout.Items.Clear();
            foreach (var label in _vm.LayoutLabels)
                CmbLayout.Items.Add(label);
            CmbLayout.SelectedIndex = _settings.DefaultLayoutIndex;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            _settings.HomePage = TxtHomePage.Text.Trim();
            _settings.SharedCookies = ChkSharedCookies.IsChecked == true;
            _settings.PauseVideosWhenInactive = ChkPauseVideos.IsChecked == true;
            _settings.LowMemoryForInactive = ChkLowMemory.IsChecked == true;
            _settings.DefaultLayoutIndex = CmbLayout.SelectedIndex;
            _settings.GridGap = (int)SliderGap.Value;
            _settings.ShowTileNumbers = ChkShowNumbers.IsChecked == true;
            _settings.Save();
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
            => DialogResult = false;
    }
}
