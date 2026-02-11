using LibreSpotUWP.Helpers;
using LibreSpotUWP.Models;
using LibreSpotUWP.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using static LibreSpotUWP.Controls.SpotifyAccountControl;

namespace LibreSpotUWP
{
    public sealed partial class MainPage : Page
    {
        private readonly List<string> _history = new List<string>();
        private bool _isPlayerOpen = false;

        public MainPage()
        {
            this.InitializeComponent();
            ApplyAppearanceStyling();
            NavListBox.SelectedIndex = 0;
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                AppViewBackButtonVisibility.Disabled;
        }

        protected async override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            await HeaderAccountControl.Initialize();
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
            if (_history.Count == 0 || _history[_history.Count - 1] != pageTag)
                _history.Add(pageTag);

            if (pageTag == "Player")
            {
                ShowPlayer();
                UpdateBackButton();
                return;
            }

            HidePlayer();

            bool requiresAuth = pageTag == "Home";

            if (requiresAuth)
            {
                if (!await EnsureAuthenticatedAsync())
                {
                    var settingsType = NavigationHelper.GetPageType("Settings");
                    if (ContentFrame.CurrentSourcePageType != settingsType)
                        ContentFrame.Navigate(settingsType);

                    UpdateBackButton();
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

            UpdateBackButton();
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
        public void ShowPlayer()
        {
            var pageType = NavigationHelper.GetPageType("Player");

            if (PlayerOverlay.Content?.GetType() != pageType)
                PlayerOverlay.Navigate(pageType);

            PlayerOverlay.Visibility = Visibility.Visible;
            _isPlayerOpen = true;
        }

        public void HidePlayer()
        {
            PlayerOverlay.Visibility = Visibility.Collapsed;
            _isPlayerOpen = false;
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (_history.Count <= 1)
                return;

            e.Handled = true;

            _history.RemoveAt(_history.Count - 1);

            string previous = _history[_history.Count - 1];

            if (previous == "Player")
            {
                ShowPlayer();
                UpdateBackButton();
                return;
            }

            HidePlayer();

            var pageType = NavigationHelper.GetPageType(previous);
            if (ContentFrame.CurrentSourcePageType != pageType)
                ContentFrame.Navigate(pageType);

            foreach (var item in NavListBox.Items)
            {
                if (item is ListBoxItem lbi && (string)lbi.Tag == previous)
                {
                    NavListBox.SelectedItem = lbi;
                    break;
                }
            }

            foreach (var item in BottomNavListBox.Items)
            {
                if (item is ListBoxItem lbi && (string)lbi.Tag == previous)
                {
                    BottomNavListBox.SelectedItem = lbi;
                    break;
                }
            }

            UpdateBackButton();
        }

        private void UpdateBackButton()
        {
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                _history.Count > 1
                ? AppViewBackButtonVisibility.Visible
                : AppViewBackButtonVisibility.Disabled;
        }

        private void HeaderAccountControl_UserChanged(object sender, UserChangedEventArgs e)
        {
            AccountLoadingRing.IsActive = false;

            var user = e.User;

            if (user != null)
            {
                DefaultAccountIcon.Visibility = Visibility.Collapsed;
                AccountProfileEllipse.Visibility = Visibility.Visible;

                var img = user.Images != null && user.Images.Count > 0
                    ? user.Images[0].Url
                    : null;

                AccountProfileBrush.ImageSource =
                    img != null ? new BitmapImage(new Uri(img)) : null;

                AccountNameText.Text =
                    !string.IsNullOrEmpty(user.DisplayName)
                        ? user.DisplayName
                        : user.Id;
            }
            else
            {
                DefaultAccountIcon.Visibility = Visibility.Visible;
                AccountProfileEllipse.Visibility = Visibility.Collapsed;
                AccountNameText.Text = "Account";
            }
        }

        private void HeaderAccountControl_LoadingStateChanged(object sender, bool isLoading)
        {
            AccountLoadingRing.IsActive = isLoading;

            if (isLoading)
            {
                DefaultAccountIcon.Visibility = Visibility.Collapsed;
                AccountProfileEllipse.Visibility = Visibility.Collapsed;
            }
        }
    }
}