using LibreSpotUWP.Helpers;
using LibreSpotUWP.Models;
using LibreSpotUWP.Services;
using LibreSpotUWP.Views;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
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

        private DispatcherTimer _searchDebounceTimer;
        private string _pendingSearchQuery;

        public MainPage()
        {
            InitializeComponent();
            ApplyAppearanceStyling();
            NavListBox.SelectedIndex = 0;

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

        public void NavigateToAlbum(string id) => NavigateTo("Album:" + id);

        public void NavigateToArtist(string id) => NavigateTo("Artist:" + id);

        public void NavigateToPlaylist(string id) => NavigateTo("Playlist:" + id);

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

            if (pageTag.StartsWith("Search:", StringComparison.OrdinalIgnoreCase))
            {
                var query = pageTag.Substring(7);
                ContentFrame.Navigate(typeof(SearchPage), query);
                UpdateBackButton();
                return;
            }

            if (pageTag.StartsWith("Album:"))
            {
                ContentFrame.Navigate(typeof(AlbumPage), pageTag.Substring(6));
                UpdateBackButton();
                return;
            }

            if (pageTag.StartsWith("Artist:"))
            {
                ContentFrame.Navigate(typeof(ArtistPage), pageTag.Substring(7));
                UpdateBackButton();
                return;
            }

            if (pageTag.StartsWith("Playlist:"))
            {
                ContentFrame.Navigate(typeof(PlaylistPage), pageTag.Substring(9));
                UpdateBackButton();
                return;
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

                if (tag == "Home")
                {
                    if (ContentFrame.CurrentSourcePageType != NavigationHelper.GetPageType("Home"))
                        NavigateTo("Home");

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

            if (previous.StartsWith("Search:", StringComparison.OrdinalIgnoreCase))
            {
                var query = previous.Substring(7);
                ContentFrame.Navigate(typeof(SearchPage), query);
                UpdateBackButton();
                return;
            }

            if (previous.StartsWith("Album:"))
            {
                ContentFrame.Navigate(typeof(AlbumPage), previous.Substring(6));
                UpdateBackButton();
                return;
            }

            if (previous.StartsWith("Artist:"))
            {
                ContentFrame.Navigate(typeof(ArtistPage), previous.Substring(7));
                UpdateBackButton();
                return;
            }

            if (previous.StartsWith("Playlist:"))
            {
                ContentFrame.Navigate(typeof(PlaylistPage), previous.Substring(9));
                UpdateBackButton();
                return;
            }

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
        private void RootSplitView_PaneOpened(object sender, object e)
        {
            SearchBox.Visibility = Visibility.Visible;
            SearchIconButton.Visibility = Visibility.Collapsed;
        }

        private void RootSplitView_PaneClosed(object sender, object e)
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

        private void HomeItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (ContentFrame.CurrentSourcePageType == NavigationHelper.GetPageType("Home"))
            {
                ForceNavigateHome();
            }
            else
            {
                NavigateTo("Home");
            }
        }

        private void ForceNavigateHome()
        {
            _history.Clear();
            _history.Add("Home");

            HidePlayer();

            var homeType = NavigationHelper.GetPageType("Home");
            ContentFrame.Navigate(homeType);

            NavListBox.SelectedIndex = 0;
            BottomNavListBox.SelectedIndex = -1;

            UpdateBackButton();
        }
    }
}