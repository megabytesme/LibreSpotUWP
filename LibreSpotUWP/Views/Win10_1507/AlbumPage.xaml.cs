using LibreSpotUWP.Controls;
using LibreSpotUWP.ViewModels;
using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LibreSpotUWP.Views
{
    public sealed partial class AlbumPage : Page
    {
        public AlbumPageViewModel ViewModel { get; } = new AlbumPageViewModel();

        public AlbumPage()
        {
            InitializeComponent();
            DataContext = ViewModel;

            TrackList.ArtistClicked += (s, artistId) => NavigateToMain("Artist", artistId);
            TrackList.AlbumClicked += (s, albumId) => NavigateToMain("Album", albumId);
            PlayActions.PlayRequested += (s, e) => ViewModel.PlayAlbum();
            PlayActions.ShuffleRequested += (s, e) => ViewModel.ShuffleAlbum();
            PlayActions.AddToRequested += async (s, e) => await ToggleAlbumSavedAsync();
            PlayActions.DownloadRequested += async (s, e) => await ToggleAlbumPersistenceAsync();
            TrackList.TrackClicked += OnTrackClicked;
            TrackList.TrackPersistRequested += OnTrackPersistRequested;
            TrackList.LoadMoreRequested += OnLoadMoreRequested;
        }

        private void NavigateToMain(string type, string id)
        {
            var frame = Window.Current.Content as Frame;
            var main = frame?.Content as MainPage;
            if (type == "Artist") main?.NavigateToArtist(id);
            else main?.NavigateToAlbum(id);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            string albumId = e.Parameter as string;
            SetIsLoading(true, "Loading album...");
            try
            {
                await ViewModel.LoadAsync(albumId);

                HeaderControl.SetAlbum(ViewModel.Album);
                UpdateStatusBanner();
                PlayActions.SetDownloaded(App.OfflineCatalog.IsAlbumPersisted(ViewModel.Album?.Id));
                await UpdateAlbumSavedStateAsync();

                var tracks = MapToFullTracks(ViewModel.Tracks?.Items ?? new List<SimpleTrack>());
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
                RefreshAlbumAsync);
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
                var newTracks = MapToFullTracks(ViewModel.LastLoadedBatch);
                int offset = ViewModel.TotalTracksLoaded - ViewModel.LastLoadedBatch.Count;
                TrackList.AddTracks(newTracks, false, offset);
            }
        }

        private IEnumerable<FullTrack> MapToFullTracks(IEnumerable<SimpleTrack> simpleTracks)
        {
            return simpleTracks.Select(st => new FullTrack
            {
                Name = st.Name,
                Artists = st.Artists,
                DurationMs = st.DurationMs,
                Uri = st.Uri,
                Id = st.Id
            });
        }

        public async void OnTrackClicked(object sender, TrackClickedEventArgs e)
        {
            var trackUri = (e.Track as FullTrack)?.Uri ?? (e.Track as SimpleTrack)?.Uri;
            if (trackUri == null || ViewModel.Album == null) return;

            await App.Media.PlayAsync($"spotify:album:{ViewModel.Album.Id}", trackUri);
        }

        private async void OnTrackPersistRequested(object sender, TrackClickedEventArgs e)
        {
            if (!(e.Track is FullTrack track))
                return;

            var persisted = App.OfflineCatalog.IsTrackPersisted(track.Uri);
            await App.OfflineCatalog.SetTrackPersistedAsync(track, !persisted);
            TrackList.IsTrackPersistedResolver = fullTrack => App.OfflineCatalog.IsTrackPersisted(fullTrack?.Uri);
            TrackList.AddTracks(MapToFullTracks(ViewModel.Tracks.Items), true, 0);
        }

        private async Task UpdateAlbumSavedStateAsync()
        {
            if (ViewModel.Album == null)
                return;

            try
            {
                var saved = await App.SpotifyWeb.CheckAlbumSavedAsync(ViewModel.Album.Id);
                PlayActions.SetAdded(saved, "Remove album from library", "Add album to library");
            }
            catch
            {
                PlayActions.SetAdded(false, "Remove album from library", "Add album to library");
            }
        }

        private async Task ToggleAlbumSavedAsync()
        {
            if (ViewModel.Album == null || !Helpers.ConnectivityHelper.HasInternetAccess())
                return;

            try
            {
                var saved = await App.SpotifyWeb.CheckAlbumSavedAsync(ViewModel.Album.Id);
                await App.SpotifyWeb.SetAlbumSavedAsync(ViewModel.Album.Id, !saved);
                PlayActions.SetAdded(!saved, "Remove album from library", "Add album to library");
            }
            catch
            {
                await UpdateAlbumSavedStateAsync();
            }
        }

        private async Task ToggleAlbumPersistenceAsync()
        {
            if (ViewModel.Album == null || ViewModel.Tracks?.Items == null)
                return;

            await EnsureAllAlbumTracksLoadedAsync();

            var persisted = App.OfflineCatalog.IsAlbumPersisted(ViewModel.Album.Id);
            await App.OfflineCatalog.SetAlbumPersistedAsync(ViewModel.Album, ViewModel.Tracks.Items, !persisted);
            PlayActions.SetDownloaded(!persisted);
            TrackList.IsTrackPersistedResolver = track => App.OfflineCatalog.IsTrackPersisted(track?.Uri);
            TrackList.AddTracks(MapToFullTracks(ViewModel.Tracks.Items), true, 0);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAlbumAsync();
        }

        private async Task RefreshAlbumAsync()
        {
            if (ViewModel.Album == null)
                return;

            SetIsLoading(true, "Refreshing album...");
            try
            {
                await ViewModel.LoadAsync(ViewModel.Album.Id, true);
                HeaderControl.SetAlbum(ViewModel.Album);
                UpdateStatusBanner();
                PlayActions.SetDownloaded(App.OfflineCatalog.IsAlbumPersisted(ViewModel.Album?.Id));
                await UpdateAlbumSavedStateAsync();
                TrackList.AddTracks(MapToFullTracks(ViewModel.Tracks?.Items ?? new List<SimpleTrack>()), true, 0);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                SetIsLoading(false);
            }
        }

        private MainPage GetMainPage()
        {
            return (Window.Current.Content as Frame)?.Content as MainPage;
        }

        private async Task EnsureAllAlbumTracksLoadedAsync()
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
            LoadingText.Text = message ?? "Loading album...";
        }
    }
}
