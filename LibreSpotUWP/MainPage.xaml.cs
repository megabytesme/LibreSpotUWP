using LibreSpotUWP.Helpers;
using LibreSpotUWP.Models;
using LibreSpotUWP.Services;
using LibreSpotUWP.Views;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using static LibreSpotUWP.Controls.SpotifyAccountControl;

namespace LibreSpotUWP
{
    public sealed partial class MainPage : Page
    {
        private readonly List<string> _history = new List<string>();
        private bool _isPlayerOpen = false;

        private DispatcherTimer _searchDebounceTimer;
        private string _pendingSearchQuery;
        private Func<Task> _cacheRefreshAction;
        private string _cacheStatusTooltip;
        private int _suppressedSelectionChanges;
        private string _currentNavTag = "Home";

        public MainPage()
        {
            InitializeComponent();

            RootSplitView.RegisterPropertyChangedCallback(SplitView.IsPaneOpenProperty, (s, dp) => {
                if (RootSplitView.IsPaneOpen)
                    RootSplitView_PaneOpened(RootSplitView, null);
                else
                    RootSplitView_PaneClosed(RootSplitView, null);
            });

            ApplyAppearanceStyling();
            SetSelectedNavigationTag(_currentNavTag);

            _searchDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;

            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                AppViewBackButtonVisibility.Disabled;

            SearchBox.Visibility = Visibility.Collapsed;
            SearchIconButton.Visibility = Visibility.Visible;

            UpdateMediaBarVisibility();

            Window.Current.SizeChanged += (s, e) =>
            {
                UpdateMediaBarVisibility();
            };
        }

        private void UpdateMediaBarVisibility()
        {
            double currentWidth = Window.Current.Bounds.Width;

            if (currentWidth < 612)
            {
                WideMediaBar.Visibility = Visibility.Collapsed;
                NarrowMediaBar.Visibility = Visibility.Visible;
            }
            else
            {
                WideMediaBar.Visibility = Visibility.Visible;
                NarrowMediaBar.Visibility = Visibility.Collapsed;
            }
        }

        protected async override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            UpdateMediaBarVisibility();
            if (ContentFrame.Content == null)
                NavigateTo("Home", true);

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
            var token = await App.SpotifyAuth.EnsureValidAccessTokenAsync();
            return !string.IsNullOrEmpty(token);
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

            if (pageTag == "Player")
            {
                ShowPlayer();
                SetSelectedNavigationTag(pageTag);
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

                    SetSelectedNavigationTag("Settings");

                    UpdateBackButton();
                    return;
                }
            }

            if (pageTag.StartsWith("Search:", StringComparison.OrdinalIgnoreCase))
            {
                var query = pageTag.Substring(7);
                ContentFrame.Navigate(typeof(SearchPage), query);
                SetSelectedNavigationTag(null);
                UpdateBackButton();
                return;
            }

            if (pageTag.StartsWith("Album:"))
            {
                ContentFrame.Navigate(typeof(AlbumPage), pageTag.Substring(6));
                SetSelectedNavigationTag(null);
                UpdateBackButton();
                return;
            }

            if (pageTag.StartsWith("Artist:"))
            {
                ContentFrame.Navigate(typeof(ArtistPage), pageTag.Substring(7));
                SetSelectedNavigationTag(null);
                UpdateBackButton();
                return;
            }

            if (pageTag.StartsWith("Playlist:"))
            {
                ContentFrame.Navigate(typeof(PlaylistPage), pageTag.Substring(9));
                SetSelectedNavigationTag(null);
                UpdateBackButton();
                return;
            }

            if (pageTag.StartsWith("User:"))
            {
                ContentFrame.Navigate(typeof(UserProfilePage), pageTag.Substring(5));
                SetSelectedNavigationTag(null);
                UpdateBackButton();
                return;
            }

            if (pageTag == "Lyrics")
            {
                ContentFrame.Navigate(NavigationHelper.GetPageType(pageTag));
                SetSelectedNavigationTag(null);
                UpdateBackButton();
                return;
            }

            var pageType = NavigationHelper.GetPageType(pageTag);
            if (forceReload || ContentFrame.CurrentSourcePageType != pageType)
                ContentFrame.Navigate(pageType);

            SetSelectedNavigationTag(pageTag);

            UpdateBackButton();
        }

        private void HamburgerButton_Click(object sender, RoutedEventArgs e)
        {
            RootSplitView.IsPaneOpen = !RootSplitView.IsPaneOpen;
        }

        private async void NavListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressedSelectionChanges > 0)
            {
                _suppressedSelectionChanges--;
                return;
            }

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

                if (tag == "Home")
                {
                    if (ContentFrame.CurrentSourcePageType != NavigationHelper.GetPageType("Home"))
                        NavigateTo("Home");

                    return;
                }

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
                SetSelectedNavigationTag(previous);
                UpdateBackButton();
                return;
            }

            HidePlayer();

            if (previous.StartsWith("Search:", StringComparison.OrdinalIgnoreCase))
            {
                var query = previous.Substring(7);
                ContentFrame.Navigate(typeof(SearchPage), query);
                SetSelectedNavigationTag(null);
                UpdateBackButton();
                return;
            }

            if (previous.StartsWith("Album:"))
            {
                ContentFrame.Navigate(typeof(AlbumPage), previous.Substring(6));
                SetSelectedNavigationTag(null);
                UpdateBackButton();
                return;
            }

            if (previous.StartsWith("Artist:"))
            {
                ContentFrame.Navigate(typeof(ArtistPage), previous.Substring(7));
                SetSelectedNavigationTag(null);
                UpdateBackButton();
                return;
            }

            if (previous.StartsWith("Playlist:"))
            {
                ContentFrame.Navigate(typeof(PlaylistPage), previous.Substring(9));
                SetSelectedNavigationTag(null);
                UpdateBackButton();
                return;
            }

            if (previous.StartsWith("User:"))
            {
                ContentFrame.Navigate(typeof(UserProfilePage), previous.Substring(5));
                SetSelectedNavigationTag(null);
                UpdateBackButton();
                return;
            }

            if (previous == "Lyrics")
            {
                ContentFrame.Navigate(NavigationHelper.GetPageType(previous));
                SetSelectedNavigationTag(null);
                UpdateBackButton();
                return;
            }

            var pageType = NavigationHelper.GetPageType(previous);
            if (ContentFrame.CurrentSourcePageType != pageType)
                ContentFrame.Navigate(pageType);
            SetSelectedNavigationTag(previous);

            UpdateBackButton();
        }

        private void UpdateBackButton()
        {
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                _history.Count > 1
                ? AppViewBackButtonVisibility.Visible
                : AppViewBackButtonVisibility.Disabled;
        }

        private void HeaderAccountControl_UserChanged(
            object sender,
            LibreSpotUWP.Controls.SpotifyAccountControl.UserChangedEventArgs e)
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

                AccountProfileBrush.ImageSource = ImageUriHelper.CreateBitmapImage(img, useFallback: true);

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

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var text = SearchBox.Text?.Trim();

            if (string.IsNullOrEmpty(text))
                return;

            _pendingSearchQuery = text;
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }

        private void SearchDebounceTimer_Tick(object sender, object e)
        {
            _searchDebounceTimer.Stop();

            if (!string.IsNullOrEmpty(_pendingSearchQuery))
            {
                NavigateTo($"Search:{_pendingSearchQuery}");
            }
        }

        private void RootSplitView_PaneOpened(SplitView sender, object args)
        {
            SearchBox.Visibility = Visibility.Visible;
            SearchIconButton.Visibility = Visibility.Collapsed;
        }

        private void RootSplitView_PaneClosed(SplitView sender, object args)
        {
            SearchBox.Visibility = Visibility.Collapsed;
            SearchIconButton.Visibility = Visibility.Visible;
        }

        private void SearchIconButton_Click(object sender, RoutedEventArgs e)
        {
            FlyoutSearchBox.Text = SearchBox.Text;
            SearchFlyout.ShowAt(SearchIconButton);
            FlyoutSearchBox.Focus(FocusState.Programmatic);
        }

        private void HandleSearchTextChanged(string text)
        {
            var trimmed = text?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return;

            _pendingSearchQuery = trimmed;
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }

        private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                NavigateTo($"Search:{SearchBox.Text.Trim()}");
            }
        }

        private void FlyoutSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchBox.Text != FlyoutSearchBox.Text)
                SearchBox.Text = FlyoutSearchBox.Text;

            HandleSearchTextChanged(FlyoutSearchBox.Text);
        }

        private void FlyoutSearchBox_KeyDown(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                var text = FlyoutSearchBox.Text?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    NavigateTo($"Search:{text}");
                    SearchFlyout.Hide();
                }
            }
        }

        private void NavItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (!(sender is ListBoxItem item))
                return;

            var tag = item.Tag?.ToString();
            if (string.IsNullOrWhiteSpace(tag))
                return;

            if (tag == "Account")
            {
                FlyoutBase.ShowAttachedFlyout(item);
                return;
            }

            if (tag == "Home")
            {
                if (ContentFrame.CurrentSourcePageType == NavigationHelper.GetPageType("Home"))
                    ForceNavigateHome();
                else
                    NavigateTo("Home", true);
                return;
            }

            NavigateTo(tag, true);
        }

        private void ForceNavigateHome()
        {
            _history.Clear();
            _history.Add("Home");
            ClearCacheStatus();

            HidePlayer();

            var homeType = NavigationHelper.GetPageType("Home");
            ContentFrame.Navigate(homeType);

            SetSelectedNavigationTag("Home");

            UpdateBackButton();
        }

        private void SetSelectedNavigationTag(string tag)
        {
            _currentNavTag = tag;

            _suppressedSelectionChanges = 2;
            NavListBox.SelectedItem = FindListBoxItemByTag(NavListBox, tag);
            BottomNavListBox.SelectedItem = FindListBoxItemByTag(BottomNavListBox, tag);
        }

        private static ListBoxItem FindListBoxItemByTag(ListBox listBox, string tag)
        {
            if (listBox == null || string.IsNullOrWhiteSpace(tag))
                return null;

            foreach (var item in listBox.Items)
            {
                if (item is ListBoxItem lbi && string.Equals(lbi.Tag as string, tag, StringComparison.Ordinal))
                    return lbi;
            }

            return null;
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

        private async void CacheStatusText_Tapped(object sender, TappedRoutedEventArgs e)
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
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogService.Error(ex, "Cache refresh action failed");
            }
        }
    }
}
