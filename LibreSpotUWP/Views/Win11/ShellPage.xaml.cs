using LibreSpotUWP.Controls;
using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Services;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace LibreSpotUWP.Views.Win11
{
    public sealed partial class ShellPage : Page, IAppShell
    {
        private readonly List<string> _history = new List<string>();
        private Func<Task> _cacheRefreshAction;
        private string _cacheStatusTooltip;

        public ShellPage()
        {
            InitializeComponent();
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                AppViewBackButtonVisibility.Disabled;

            Window.Current.SizeChanged += (s, e) => UpdateMediaBarVisibility();
            UpdateMediaBarVisibility();
        }

        protected async override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            await HeaderAccountControl.Initialize();

            if (ContentFrame.Content == null)
            {
                var startupTag = GetStartupPageTag();
                NavigateTo(startupTag, true);
                SelectNavigationItem(startupTag);
            }
        }

        private string GetStartupPageTag()
        {
            if (App.SpotifyAuth?.Current == null || App.SpotifyAuth.Current.IsExpired)
                return "Settings";

            if (UserSettings.RememberLastPage)
            {
                var lastPage = Windows.Storage.ApplicationData.Current.LocalSettings.Values.TryGetValue("LastOpenPage", out object value)
                    ? value as string
                    : null;

                if (!string.IsNullOrWhiteSpace(lastPage))
                    return lastPage;
            }

            return "Home";
        }

        private void UpdateMediaBarVisibility()
        {
            if (FullWindowFrame.Visibility == Visibility.Visible)
            {
                WideMediaBar.Visibility = Visibility.Collapsed;
                NarrowMediaBar.Visibility = Visibility.Collapsed;
                return;
            }

            double currentWidth = Window.Current.Bounds.Width;
            WideMediaBar.Visibility = currentWidth < 612 ? Visibility.Collapsed : Visibility.Visible;
            NarrowMediaBar.Visibility = currentWidth < 612 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void NavView_SelectionChanged(Microsoft.UI.Xaml.Controls.NavigationView sender, Microsoft.UI.Xaml.Controls.NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                await Task.Yield();
                NavigateTo("Settings", true);
                return;
            }

            if (args.SelectedItemContainer == null)
                return;

            var tag = args.SelectedItemContainer.Tag?.ToString();
            if (string.IsNullOrWhiteSpace(tag))
                return;

            if (tag == "Account")
            {
                FlyoutBase.ShowAttachedFlyout(AccountItem);
                return;
            }

            await Task.Yield();
            NavigateTo(tag, true);
        }

        public void NavigateToAlbum(string id) => NavigateTo("Album:" + id);
        public void NavigateToArtist(string id) => NavigateTo("Artist:" + id);
        public void NavigateToPlaylist(string id) => NavigateTo("Playlist:" + id);
        public void NavigateToUserProfile(string id) => NavigateTo("User:" + id);

        public async void NavigateTo(string pageTag, bool forceReload = false)
        {
            ClearCacheStatus();

            if (_history.Count == 0 || _history[_history.Count - 1] != pageTag)
                _history.Add(pageTag);
            PersistLastOpenPage(pageTag);

            if (string.Equals(pageTag, "Player", StringComparison.Ordinal))
            {
                var playerType = NavigationHelper.GetPageType(pageTag);
                if (forceReload || FullWindowFrame.CurrentSourcePageType != playerType)
                    FullWindowFrame.Navigate(playerType);

                SetFullWindowMode(true);
                SelectNavigationItem(pageTag);
                UpdateBackButton();
                return;
            }

            SetFullWindowMode(false);

            if (pageTag == "Home" && !await EnsureAuthenticatedAsync())
            {
                var settingsType = NavigationHelper.GetPageType("Settings");
                if (ContentFrame.CurrentSourcePageType != settingsType)
                    ContentFrame.Navigate(settingsType);

                SelectNavigationItem("Settings");
                UpdateBackButton();
                return;
            }

            if (pageTag.StartsWith("Search:", StringComparison.OrdinalIgnoreCase) ||
                pageTag.StartsWith("Album:", StringComparison.OrdinalIgnoreCase) ||
                pageTag.StartsWith("Artist:", StringComparison.OrdinalIgnoreCase) ||
                pageTag.StartsWith("Playlist:", StringComparison.OrdinalIgnoreCase) ||
                pageTag.StartsWith("User:", StringComparison.OrdinalIgnoreCase))
            {
                var parameter = pageTag.Substring(pageTag.IndexOf(':') + 1);
                ContentFrame.Navigate(NavigationHelper.GetPageType(pageTag), parameter);
                UpdateBackButton();
                return;
            }

            var pageType = NavigationHelper.GetPageType(pageTag);
            if (forceReload || ContentFrame.CurrentSourcePageType != pageType)
                ContentFrame.Navigate(pageType);

            SelectNavigationItem(pageTag);
            UpdateBackButton();
        }

        private void NavigationSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            var query = (args.QueryText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                NavigateTo("Search", true);
                SelectNavigationItem("Search");
                return;
            }

            NavigateTo("Search:" + query, true);
            SelectNavigationItem("Search");
        }

        public void SetCacheStatus(string tooltip, bool showRefreshButton, Func<Task> refreshAction)
        {
            CacheStatusPanel.Visibility = Visibility.Visible;
            CacheStatusText.Text = "Cached Content";
            CacheRefreshButton.Visibility = showRefreshButton ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(CacheStatusPanel, tooltip);
            ToolTipService.SetToolTip(CacheRefreshButton, tooltip);
            _cacheStatusTooltip = tooltip;
            _cacheRefreshAction = refreshAction;
        }

        public void ClearCacheStatus()
        {
            CacheStatusPanel.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(CacheStatusPanel, null);
            ToolTipService.SetToolTip(CacheRefreshButton, null);
            _cacheStatusTooltip = null;
            _cacheRefreshAction = null;
        }

        private async Task<bool> EnsureAuthenticatedAsync()
        {
            var token = await App.SpotifyAuth.EnsureValidAccessTokenAsync();
            return !string.IsNullOrEmpty(token);
        }

        private void SelectNavigationItem(string tag)
        {
            if (string.Equals(tag, "Settings", StringComparison.Ordinal))
            {
                NavView.SelectedItem = NavView.SettingsItem;
                return;
            }

            foreach (var item in NavView.MenuItems)
            {
                if (item is Microsoft.UI.Xaml.Controls.NavigationViewItem navItem && string.Equals(navItem.Tag as string, tag, StringComparison.Ordinal))
                {
                    NavView.SelectedItem = navItem;
                    return;
                }
            }

            foreach (var item in NavView.FooterMenuItems)
            {
                if (item is Microsoft.UI.Xaml.Controls.NavigationViewItem navItem && string.Equals(navItem.Tag as string, tag, StringComparison.Ordinal))
                {
                    NavView.SelectedItem = navItem;
                    return;
                }
            }

            NavView.SelectedItem = null;
        }

        private void SetFullWindowMode(bool enabled)
        {
            FullWindowFrame.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            NavView.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
            MediaBarHost.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
            CacheStatusPanel.Visibility = enabled ? Visibility.Collapsed : CacheStatusPanel.Visibility;
            UpdateMediaBarVisibility();
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (_history.Count <= 1)
                return;

            e.Handled = true;
            _history.RemoveAt(_history.Count - 1);
            var previous = _history[_history.Count - 1];

            if (previous.StartsWith("Search:", StringComparison.OrdinalIgnoreCase) ||
                previous.StartsWith("Album:", StringComparison.OrdinalIgnoreCase) ||
                previous.StartsWith("Artist:", StringComparison.OrdinalIgnoreCase) ||
                previous.StartsWith("Playlist:", StringComparison.OrdinalIgnoreCase) ||
                previous.StartsWith("User:", StringComparison.OrdinalIgnoreCase))
            {
                SetFullWindowMode(false);
                var parameter = previous.Substring(previous.IndexOf(':') + 1);
                ContentFrame.Navigate(NavigationHelper.GetPageType(previous), parameter);
            }
            else if (string.Equals(previous, "Player", StringComparison.Ordinal))
            {
                var playerType = NavigationHelper.GetPageType(previous);
                if (FullWindowFrame.CurrentSourcePageType != playerType)
                    FullWindowFrame.Navigate(playerType);

                SetFullWindowMode(true);
                SelectNavigationItem(previous);
            }
            else
            {
                SetFullWindowMode(false);
                ContentFrame.Navigate(NavigationHelper.GetPageType(previous));
                SelectNavigationItem(previous);
            }

            UpdateBackButton();
        }

        private void UpdateBackButton()
        {
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                _history.Count > 1 ? AppViewBackButtonVisibility.Visible : AppViewBackButtonVisibility.Disabled;
        }

        private static void PersistLastOpenPage(string pageTag)
        {
            if (!UserSettings.RememberLastPage ||
                string.Equals(pageTag, "Player", StringComparison.OrdinalIgnoreCase))
                return;

            Windows.Storage.ApplicationData.Current.LocalSettings.Values["LastOpenPage"] = pageTag;
        }

        private void HeaderAccountControl_LoadingStateChanged(object sender, bool isLoading)
        {
            AccountLoadingRing.IsActive = isLoading;
            AccountPersonPicture.Opacity = isLoading ? 0.35 : 1.0;
        }

        private void HeaderAccountControl_UserChanged(object sender, SpotifyAccountControl.UserChangedEventArgs e)
        {
            var user = e.User;
            if (user != null)
            {
                var imageUrl = user.Images != null && user.Images.Count > 0
                    ? user.Images[0].Url
                    : null;

                if (!string.IsNullOrWhiteSpace(imageUrl))
                    AccountPersonPicture.ProfilePicture = new BitmapImage(new Uri(imageUrl));
                else
                    AccountPersonPicture.ProfilePicture = null;

                AccountNameText.Text = !string.IsNullOrWhiteSpace(user.DisplayName)
                    ? user.DisplayName
                    : user.Id;
            }
            else
            {
                AccountPersonPicture.ProfilePicture = null;
                AccountNameText.Text = "Account";
            }
        }

        private async void CacheStatusText_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_cacheStatusTooltip))
                return;

            var dialog = new ContentDialog
            {
                Title = "Cached Content",
                Content = _cacheStatusTooltip,
                PrimaryButtonText = "OK"
            };

            await dialog.ShowAsync();
        }

        private async void CacheRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            var action = _cacheRefreshAction;
            if (action == null)
                return;

            try
            {
                await action();
            }
            catch (Exception ex)
            {
                LogService.Error(ex, "Cache refresh action failed");
            }
        }
    }
}
