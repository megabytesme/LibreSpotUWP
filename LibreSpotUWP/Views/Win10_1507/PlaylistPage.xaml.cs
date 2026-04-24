using LibreSpotUWP.Controls;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using LibreSpotUWP.ViewModels;
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

namespace LibreSpotUWP.Views
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
                var frame = Window.Current.Content as Frame;
                var main = frame?.Content as MainPage;
                main?.NavigateToArtist(artistId);
            };

            TrackList.AlbumClicked += (s, albumId) =>
            {
                var frame = Window.Current.Content as Frame;
                var main = frame?.Content as MainPage;
                main?.NavigateToAlbum(albumId);
            };

            PlayActions.PlayRequested += (s, e) =>
            {
                ViewModel.PlayPlaylist();
            };

            PlayActions.ShuffleRequested += (s, e) =>
            {
                ViewModel.ShufflePlaylist();
            };
            PlayActions.PersistRequested += async (s, e) => await TogglePlaylistPersistenceAsync();

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
            await ViewModel.LoadAsync(playlistId);

            HeaderControl.SetPlaylist(ViewModel.Playlist);
            UpdateStatusBanner();
            PlayActions.SetPersisted(App.OfflineCatalog.IsPlaylistPersisted(ViewModel.Playlist?.Id));

            var tracks = ViewModel.Tracks?.Items?.Select(t => t.Track as FullTrack).Where(t => t != null)
                ?? Enumerable.Empty<FullTrack>();
            TrackList.IsTrackPersistedResolver = track => App.OfflineCatalog.IsTrackPersisted(track?.Uri);
            TrackList.AddTracks(tracks, true, 0);
        }

        private void UpdateStatusBanner()
        {
            CacheIndicator.Visibility = Visibility.Collapsed;

            var mainPage = GetMainPage();
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

        private async Task TogglePlaylistPersistenceAsync()
        {
            if (ViewModel.Playlist == null || ViewModel.Tracks?.Items == null)
                return;

            await EnsureAllPlaylistTracksLoadedAsync();

            var tracks = ViewModel.Tracks.Items.Select(t => t.Track as FullTrack).Where(t => t != null).ToList();
            var persisted = App.OfflineCatalog.IsPlaylistPersisted(ViewModel.Playlist.Id);
            await App.OfflineCatalog.SetPlaylistPersistedAsync(ViewModel.Playlist, tracks, !persisted);
            PlayActions.SetPersisted(!persisted);
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

            await ViewModel.LoadAsync(ViewModel.Playlist.Id, true);
            HeaderControl.SetPlaylist(ViewModel.Playlist);
            UpdateStatusBanner();
            TrackList.AddTracks(
                ViewModel.Tracks?.Items?.Select(t => t.Track as FullTrack).Where(t => t != null)
                    ?? Enumerable.Empty<FullTrack>(),
                true,
                0);
        }

        private MainPage GetMainPage()
        {
            return (Window.Current.Content as Frame)?.Content as MainPage;
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
                : "Cached data is being shown.";
        }
    }
}
