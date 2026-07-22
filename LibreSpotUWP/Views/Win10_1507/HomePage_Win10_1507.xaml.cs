using LibreSpotUWP.Exceptions;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using LibreSpotUWP.ViewModels;
using LibreSpotUWP.Services;
using SpotifyAPI.Web;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LibreSpotUWP.Views.Win10_1507
{
    public sealed partial class HomePage_Win10_1507 : Page
    {
        public HomePageViewModel ViewModel { get; } = new HomePageViewModel();

        private ISpotifyAuthService _auth;
        private ISpotifyWebService _spotify;
        private CancellationTokenSource _cts;

        public HomePage_Win10_1507()
        {
            InitializeComponent();
            DataContext = this;
            Loaded += HomePage_Loaded;
            Unloaded += (s, e) => _cts?.Cancel();
        }

        private async void HomePage_Loaded(object sender, RoutedEventArgs e)
        {
            _auth = App.SpotifyAuth;
            _spotify = App.SpotifyWeb;

            if (Helpers.ConnectivityHelper.HasInternetAccess() && !await EnsureAuthenticatedAsync())
                return;

            await LoadHomepageAsync();
        }

        private async Task<bool> EnsureAuthenticatedAsync()
        {
            try
            {
                var token = await _auth.EnsureValidAccessTokenAsync();
                return !string.IsNullOrEmpty(token);
            }
            catch
            {
                return false;
            }
        }

        private async Task LoadHomepageAsync()
        {
            try
            {
                _cts?.Cancel();
                _cts = new CancellationTokenSource();

                if (Helpers.ConnectivityHelper.HasInternetAccess())
                    await ViewModel.LoadAsync(_spotify, _cts.Token);
                else
                    await ViewModel.LoadOfflineAsync(App.OfflineCatalog);

                UpdateStatusBanner();
            }
            catch (OperationCanceledException) { }
            catch (SpotifyWebException ex)
            {
                LogService.Warn("Homepage load failed: " + ex.Message);
            }
        }

        private void HomeItem_Click(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem;

            var frame = Window.Current.Content as Frame;
            var mainPage = frame?.Content as MainPage;
            if (mainPage == null)
                return;

            switch (item)
            {
                case FullAlbum album:
                    mainPage.NavigateToAlbum(album.Id);
                    LogService.Info($"Navigating to album: {album.Name}");
                    break;

                case SavedAlbum saved:
                    mainPage.NavigateToAlbum(saved.Album.Id);
                    LogService.Info($"Navigating to saved album: {saved.Album.Name}");
                    break;

                case FullArtist artist:
                    mainPage.NavigateToArtist(artist.Id);
                    LogService.Info($"Navigating to artist: {artist.Name}");
                    break;

                case FullPlaylist playlist:
                    mainPage.NavigateToPlaylist(playlist.Id);
                    LogService.Info($"Navigating to playlist: {playlist.Name}");
                    break;

                case FullTrack track:
                    mainPage.NavigateToAlbum(track.Album.Id);
                    LogService.Info($"Navigating to track: {track.Name}");
                    break;

                case OfflineAlbumEntry offlineAlbum:
                    mainPage.NavigateToAlbum(offlineAlbum.AlbumId);
                    break;

                case OfflinePlaylistEntry offlinePlaylist:
                    mainPage.NavigateToPlaylist(offlinePlaylist.PlaylistId);
                    break;

                case OfflineTrackEntry offlineTrack:
                    var offlineGroup = ViewModel.GroupedHomeContent
                        .FirstOrDefault(group => group.Items.Contains(offlineTrack));
                    var offlineQueue = offlineGroup?.Items
                        .OfType<OfflineTrackEntry>()
                        .Select(track => track.TrackUri)
                        .Where(uri => !string.IsNullOrWhiteSpace(uri))
                        .ToList();
                    _ = App.Media.PlayAsync(
                        offlineTrack.TrackUri,
                        null,
                        offlineQueue,
                        offlineQueue?.IndexOf(offlineTrack.TrackUri) ?? -1);
                    break;

                default:
                    LogService.Info("Unknown item type clicked: " + item.GetType().Name);
                    break;
            }
        }

        private void UpdateStatusBanner()
        {
            CacheIndicator.Visibility = Visibility.Collapsed;

            var mainPage = (Window.Current.Content as Frame)?.Content as MainPage;
            if (mainPage == null)
                return;

            if (string.IsNullOrWhiteSpace(ViewModel.StatusMessage))
            {
                mainPage.ClearCacheStatus();
                return;
            }

            mainPage.SetCacheStatus(
                BuildCacheTooltip(ViewModel.CachedAt),
                Helpers.ConnectivityHelper.HasInternetAccess(),
                RefreshHomeAsync);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshHomeAsync();
        }

        private async Task RefreshHomeAsync()
        {
            try
            {
                _cts?.Cancel();
                _cts = new CancellationTokenSource();

                if (Helpers.ConnectivityHelper.HasInternetAccess())
                    await ViewModel.LoadAsync(_spotify, _cts.Token, true);
                else
                    await ViewModel.LoadOfflineAsync(App.OfflineCatalog);

                UpdateStatusBanner();
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static string BuildCacheTooltip(DateTimeOffset? cachedAt)
        {
            return cachedAt.HasValue
                ? $"Cached on {cachedAt.Value.LocalDateTime:dd MMM yyyy} at {cachedAt.Value.LocalDateTime:HH:mm:ss}"
                : "Cached data is being shown. Last refresh: Unknown.";
        }
    }
}
