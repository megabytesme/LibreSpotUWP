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
    public sealed partial class LikedSongsPage : Page
    {
        public LikedSongsPageViewModel ViewModel { get; } = new LikedSongsPageViewModel();

        public LikedSongsPage()
        {
            InitializeComponent();
            DataContext = ViewModel;

            TrackList.ArtistClicked += (s, artistId) => NavigateToMain("Artist", artistId);
            TrackList.AlbumClicked += (s, albumId) => NavigateToMain("Album", albumId);
            TrackList.TrackClicked += OnTrackClicked;
            TrackList.TrackPersistRequested += OnTrackPersistRequested;
            TrackList.LoadMoreRequested += OnLoadMoreRequested;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await LoadLikedSongsAsync(false);
        }

        private void NavigateToMain(string type, string id)
        {
            var frame = Window.Current.Content as Frame;
            var main = frame?.Content as MainPage;
            if (type == "Artist")
                main?.NavigateToArtist(id);
            else
                main?.NavigateToAlbum(id);
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
                var offset = ViewModel.TotalTracksLoaded - ViewModel.LastLoadedBatch.Count;
                TrackList.AddTracks(MapToFullTracks(ViewModel.LastLoadedBatch), false, offset);
            }
        }

        private async void OnTrackClicked(object sender, TrackClickedEventArgs e)
        {
            var trackUri = (e.Track as FullTrack)?.Uri ?? (e.Track as SimpleTrack)?.Uri;
            if (string.IsNullOrWhiteSpace(trackUri))
                return;

            await App.Media.PlayAsync(trackUri, null);
        }

        private async void OnTrackPersistRequested(object sender, TrackClickedEventArgs e)
        {
            if (!(e.Track is FullTrack track))
                return;

            var persisted = App.OfflineCatalog.IsTrackPersisted(track.Uri);
            await App.OfflineCatalog.SetTrackPersistedAsync(track, !persisted);
            TrackList.IsTrackPersistedResolver = fullTrack => App.OfflineCatalog.IsTrackPersisted(fullTrack?.Uri);
            TrackList.AddTracks(MapToFullTracks(ViewModel.Tracks?.Items ?? new List<SavedTrack>()), true, 0);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadLikedSongsAsync(true);
        }

        private async Task LoadLikedSongsAsync(bool forceRefresh)
        {
            SetIsLoading(true, forceRefresh ? "Refreshing liked songs..." : "Loading liked songs...");
            try
            {
                await ViewModel.LoadAsync(forceRefresh);
                UpdateStatusBanner();
                TrackList.IsTrackPersistedResolver = track => App.OfflineCatalog.IsTrackPersisted(track?.Uri);
                TrackList.AddTracks(MapToFullTracks(ViewModel.Tracks?.Items ?? new List<SavedTrack>()), true, 0);
            }
            finally
            {
                SetIsLoading(false, null);
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
                () => LoadLikedSongsAsync(true));
        }

        private MainPage GetMainPage()
        {
            return (Window.Current.Content as Frame)?.Content as MainPage;
        }

        private static IEnumerable<FullTrack> MapToFullTracks(IEnumerable<SavedTrack> savedTracks)
        {
            return savedTracks
                .Select(item => item?.Track)
                .Where(track => track != null);
        }

        private static string BuildCacheTooltip(DateTimeOffset? cachedAt)
        {
            return cachedAt.HasValue
                ? $"Cached on {cachedAt.Value.LocalDateTime:dd MMM yyyy} at {cachedAt.Value.LocalDateTime:HH:mm:ss}"
                : "Cached data is being shown.";
        }

        private void SetIsLoading(bool isLoading, string message)
        {
            LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            LoadingText.Text = message ?? "Loading liked songs...";
        }
    }
}
