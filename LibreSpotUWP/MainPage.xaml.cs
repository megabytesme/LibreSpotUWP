using LibreSpotUWP.Helpers;
using LibreSpotUWP.Models;
using LibreSpotUWP.Services;
using System;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace LibreSpotUWP
{
    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            this.InitializeComponent();
            ApplyAppearanceStyling();
            NavListBox.SelectedIndex = 0;
        }

        protected async override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
        }

        private void ApplyAppearanceStyling()
        {
            var mode = AppearanceService.Current;


            if (mode == AppearanceMode.Win10_1709)
            {
#if UWP1709
                try
                {
                    this.Background = (Brush)Application.Current.Resources["AppBackgroundAcrylic"];
                }
                catch
                {
                }

                try
                {
                    RootSplitView.PaneBackground =
                        (Brush)Application.Current.Resources["SystemControlAcrylicWindowBrush"];
                }
                catch
                {
                }
#endif
            }
            else
            {
                this.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
                RootSplitView.PaneBackground = (Brush)Application.Current.Resources["SystemControlBackgroundChromeMediumLowBrush"];
            }
        }

        private async Task<bool> EnsureAuthenticatedAsync()
        {
            var auth = App.SpotifyAuth;

            if (auth.Current == null)
                return false;

            if (auth.Current.IsExpired)
            {
                await auth.RefreshAsync();

                if (auth.Current.IsExpired)
                    return false;
            }

            return true;
        }

        public async void NavigateTo(string pageTag)
        {
            bool requiresAuth = pageTag == "Home";

            if (requiresAuth)
            {
                if (!await EnsureAuthenticatedAsync())
                {
                    var settingsType = NavigationHelper.GetPageType("Settings");
                    if (ContentFrame.CurrentSourcePageType != settingsType)
                        ContentFrame.Navigate(settingsType);
                    return;
                }
            }

            foreach (var item in NavListBox.Items)
            {
                if (item is ListBoxItem lbi && (string)lbi.Tag == pageTag)
                {
                    NavListBox.SelectedItem = lbi;
                    break;
                }
            }

            foreach (var item in BottomNavListBox.Items)
            {
                if (item is ListBoxItem lbi && (string)lbi.Tag == pageTag)
                {
                    BottomNavListBox.SelectedItem = lbi;
                    break;
                }
            }

            var pageType = NavigationHelper.GetPageType(pageTag);
            if (ContentFrame.CurrentSourcePageType != pageType)
                ContentFrame.Navigate(pageType);
        }

        private void HamburgerButton_Click(object sender, RoutedEventArgs e)
        {
            RootSplitView.IsPaneOpen = !RootSplitView.IsPaneOpen;
        }

        private async void NavListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var listBox = sender as ListBox;
            if (listBox == null || listBox.SelectedItem == null)
                return;

            if (listBox.SelectedItem is ListBoxItem item)
            {
                string tag = item.Tag?.ToString();

                if (tag == "Account")
                {
                    FlyoutBase.ShowAttachedFlyout(item);
                    listBox.SelectedIndex = -1;
                    return;
                }

                if (listBox == NavListBox)
                    BottomNavListBox.SelectedIndex = -1;
                else
                    NavListBox.SelectedIndex = -1;

                await Task.Yield();
                NavigateTo(tag);
            }
        }
    }
}