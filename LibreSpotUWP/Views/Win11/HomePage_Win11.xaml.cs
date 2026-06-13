using LibreSpotUWP.Exceptions;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using LibreSpotUWP.ViewModels;
using SpotifyAPI.Web;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LibreSpotUWP.Views.Win11
{
    public sealed partial class HomePage_Win11 : Page
    {
        public HomePageViewModel ViewModel { get; } = new HomePageViewModel();

        private ISpotifyAuthService _auth;
        private ISpotifyWebService _spotify;
        private CancellationTokenSource _cts;

        public HomePage_Win11()
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
                Debug.WriteLine("Homepage Load Failed: " + ex.Message);
            }
        }

        private void HomeItem_Click(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem;
            var shell = Helpers.PlaybackNavigationHelper.FindShell(this);
            if (shell == null)
                return;

            switch (item)
            {
                case FullAlbum album:
                    shell.NavigateToAlbum(album.Id);
                    Debug.WriteLine($"Navigating to album: {album.Name}");
                    break;

                case SavedAlbum saved:
                    shell.NavigateToAlbum(saved.Album.Id);
                    Debug.WriteLine($"Navigating to saved album: {saved.Album.Name}");
                    break;

                case FullArtist artist:
                    shell.NavigateToArtist(artist.Id);
                    Debug.WriteLine($"Navigating to artist: {artist.Name}");
                    break;

                case FullPlaylist playlist:
                    shell.NavigateToPlaylist(playlist.Id);
                    Debug.WriteLine($"Navigating to playlist: {playlist.Name}");
                    break;

                case FullTrack track:
                    shell.NavigateToAlbum(track.Album.Id);
                    Debug.WriteLine($"Navigating to track: {track.Name}");
                    break;

                case OfflineAlbumEntry offlineAlbum:
                    shell.NavigateToAlbum(offlineAlbum.AlbumId);
                    break;

                case OfflinePlaylistEntry offlinePlaylist:
                    shell.NavigateToPlaylist(offlinePlaylist.PlaylistId);
                    break;

                case OfflineTrackEntry offlineTrack:
                    _ = App.Media.PlayAsync(offlineTrack.TrackUri, null);
                    break;

                default:
                    Debug.WriteLine("Unknown item type clicked: " + item.GetType().Name);
                    break;
            }
        }

        private void UpdateStatusBanner()
        {
            CacheIndicator.Visibility = Visibility.Collapsed;

            var shell = Helpers.PlaybackNavigationHelper.FindShell(this);
            if (shell == null)
                return;

            if (string.IsNullOrWhiteSpace(ViewModel.StatusMessage))
            {
                shell.ClearCacheStatus();
                return;
            }

            shell.SetCacheStatus(
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


