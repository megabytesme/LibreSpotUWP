using LibreSpotUWP.Controls;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using LibreSpotUWP.ViewModels;
using LibreSpotUWP.Helpers;
using SpotifyAPI.Web;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace LibreSpotUWP.Views.Win11
{
    public sealed partial class PlaylistPage : Page
    {
        public PlaylistPageViewModel ViewModel { get; } = new PlaylistPageViewModel();

        public PlaylistPage()
        {
            InitializeComponent();
            DataContext = ViewModel;

            TrackList.ArtistClicked += (s, artistId) =>
            {
                PlaybackNavigationHelper.FindShell(this)?.NavigateToArtist(artistId);
            };

            TrackList.AlbumClicked += (s, albumId) =>
            {
                PlaybackNavigationHelper.FindShell(this)?.NavigateToAlbum(albumId);
            };

            PlayActions.PlayRequested += (s, e) =>
            {
                ViewModel.PlayPlaylist();
            };

            PlayActions.ShuffleRequested += (s, e) =>
            {
                ViewModel.ShufflePlaylist();
            };
            PlayActions.AddToRequested += async (s, e) => await TogglePlaylistFollowedAsync();
            PlayActions.DownloadRequested += async (s, e) => await TogglePlaylistPersistenceAsync();

            TrackList.TrackClicked += OnTrackClicked;
            TrackList.TrackPersistRequested += OnTrackPersistRequested;

            TrackList.LoadMoreRequested += OnLoadMoreRequested;
        }

        private async void OnLoadMoreRequested(object sender, EventArgs e)
        {
            if (!ViewModel.HasMoreTracks)
            {
                TrackList.SetIsLoading(false);
                return;
            }

            await ViewModel.LoadMoreTracksAsync();

            if (ViewModel.LastLoadedBatch.Any())
            {
                var newTracks = ViewModel.LastLoadedBatch
                    .Select(t => t.Track as FullTrack)
                    .Where(t => t != null);

                int offset = ViewModel.TotalTracksLoaded - ViewModel.LastLoadedBatch.Count;
                TrackList.AddTracks(newTracks, false, offset);
            }
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            string playlistId = e.Parameter as string;
            SetIsLoading(true, "Loading playlist...");
            try
            {
                await ViewModel.LoadAsync(playlistId);

                HeaderControl.SetPlaylist(ViewModel.Playlist);
                UpdateStatusBanner();
                PlayActions.SetDownloaded(App.OfflineCatalog.IsPlaylistPersisted(ViewModel.Playlist?.Id));
                await UpdatePlaylistFollowedStateAsync();

                var tracks = ViewModel.Tracks?.Items?.Select(t => t.Track as FullTrack).Where(t => t != null)
                    ?? Enumerable.Empty<FullTrack>();
                TrackList.IsTrackPersistedResolver = track => App.OfflineCatalog.IsTrackPersisted(track?.Uri);
                TrackList.AddTracks(tracks, true, 0);
            }
            finally
            {
                SetIsLoading(false);
            }
        }

        private void UpdateStatusBanner()
        {
            CacheIndicator.Visibility = Visibility.Collapsed;

            var shell = PlaybackNavigationHelper.FindShell(this);
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
                RefreshPlaylistAsync);
        }

        public async void OnTrackClicked(object sender, TrackClickedEventArgs e)
        {
            var trackUri = (e.Track as FullTrack)?.Uri ?? (e.Track as SimpleTrack)?.Uri;
            if (trackUri == null) return;

            await App.Media.PlayAsync($"spotify:playlist:{ViewModel.Playlist.Id}", trackUri);
        }

        private async void OnTrackPersistRequested(object sender, TrackClickedEventArgs e)
        {
            if (!(e.Track is FullTrack track))
                return;

            var persisted = App.OfflineCatalog.IsTrackPersisted(track.Uri);
            await App.OfflineCatalog.SetTrackPersistedAsync(track, !persisted);
            TrackList.IsTrackPersistedResolver = fullTrack => App.OfflineCatalog.IsTrackPersisted(fullTrack?.Uri);
            TrackList.AddTracks(ViewModel.Tracks.Items.Select(t => t.Track as FullTrack).Where(t => t != null), true, 0);
        }

        private async Task UpdatePlaylistFollowedStateAsync()
        {
            if (ViewModel.Playlist == null)
                return;

            try
            {
                var followed = await App.SpotifyWeb.CheckPlaylistFollowedAsync(ViewModel.Playlist.Id);
                PlayActions.SetAdded(followed, "Remove playlist from library", "Add playlist to library");
            }
            catch
            {
                PlayActions.SetAdded(false, "Remove playlist from library", "Add playlist to library");
            }
        }

        private async Task TogglePlaylistFollowedAsync()
        {
            if (ViewModel.Playlist == null || !Helpers.ConnectivityHelper.HasInternetAccess())
                return;

            try
            {
                var followed = await App.SpotifyWeb.CheckPlaylistFollowedAsync(ViewModel.Playlist.Id);
                await App.SpotifyWeb.SetPlaylistFollowedAsync(ViewModel.Playlist.Id, !followed);
                PlayActions.SetAdded(!followed, "Remove playlist from library", "Add playlist to library");
            }
            catch
            {
                await UpdatePlaylistFollowedStateAsync();
            }
        }

        private async Task TogglePlaylistPersistenceAsync()
        {
            if (ViewModel.Playlist == null || ViewModel.Tracks?.Items == null)
                return;

            await EnsureAllPlaylistTracksLoadedAsync();

            var tracks = ViewModel.Tracks.Items.Select(t => t.Track as FullTrack).Where(t => t != null).ToList();
            var persisted = App.OfflineCatalog.IsPlaylistPersisted(ViewModel.Playlist.Id);
            await App.OfflineCatalog.SetPlaylistPersistedAsync(ViewModel.Playlist, tracks, !persisted);
            PlayActions.SetDownloaded(App.OfflineCatalog.IsPlaylistPersisted(ViewModel.Playlist.Id));
            TrackList.IsTrackPersistedResolver = track => App.OfflineCatalog.IsTrackPersisted(track?.Uri);
            TrackList.AddTracks(tracks, true, 0);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshPlaylistAsync();
        }

        private async Task RefreshPlaylistAsync()
        {
            if (ViewModel.Playlist == null)
                return;

            SetIsLoading(true, "Refreshing playlist...");
            try
            {
                await ViewModel.LoadAsync(ViewModel.Playlist.Id, true);
                HeaderControl.SetPlaylist(ViewModel.Playlist);
                UpdateStatusBanner();
                PlayActions.SetDownloaded(App.OfflineCatalog.IsPlaylistPersisted(ViewModel.Playlist?.Id));
                await UpdatePlaylistFollowedStateAsync();
                TrackList.AddTracks(
                    ViewModel.Tracks?.Items?.Select(t => t.Track as FullTrack).Where(t => t != null)
                        ?? Enumerable.Empty<FullTrack>(),
                    true,
                    0);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                SetIsLoading(false);
            }
        }

        private async Task EnsureAllPlaylistTracksLoadedAsync()
        {
            while (ViewModel.HasMoreTracks)
            {
                await ViewModel.LoadMoreTracksAsync();
            }
        }

        private static string BuildCacheTooltip(DateTimeOffset? cachedAt)
        {
            return cachedAt.HasValue
                ? $"Cached on {cachedAt.Value.LocalDateTime:dd MMM yyyy} at {cachedAt.Value.LocalDateTime:HH:mm:ss}"
                : "Cached data is being shown. Last refresh: Unknown.";
        }

        private void SetIsLoading(bool isLoading, string message = null)
        {
            LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            LoadingText.Text = message ?? "Loading playlist...";
        }
    }
}


