using LibreSpotUWP.Controls;
using LibreSpotUWP.Helpers;
using LibreSpotUWP.ViewModels;
using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LibreSpotUWP.Views.Win11
{
    public sealed partial class LikedSongsPage : Page
    {
        public LikedSongsPageViewModel ViewModel { get; } = new LikedSongsPageViewModel();

        public LikedSongsPage()
        {
            InitializeComponent();
            DataContext = ViewModel;
            SortComboBox.SelectedIndex = 0;

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
            var main = PlaybackNavigationHelper.FindShell(this);
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
                TrackList.IsTrackPersistedResolver = track => App.OfflineCatalog.IsTrackPersisted(track?.Uri);
                TrackList.AddTracks(MapToFullTracks(ViewModel.GetOrderedTracks()), true, 0);
                UpdateSummary();
            }
        }

        private async void OnTrackClicked(object sender, TrackClickedEventArgs e)
        {
            var trackUri = (e.Track as FullTrack)?.Uri ?? (e.Track as SimpleTrack)?.Uri;
            if (string.IsNullOrWhiteSpace(trackUri))
                return;

            await App.Media.PlayAsync("spotify:collection:tracks", trackUri);
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
                UpdateSummary();
                TrackList.IsTrackPersistedResolver = track => App.OfflineCatalog.IsTrackPersisted(track?.Uri);
                TrackList.AddTracks(MapToFullTracks(ViewModel.GetOrderedTracks()), true, 0);
            }
            finally
            {
                SetIsLoading(false, null);
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
                () => LoadLikedSongsAsync(true));
        }

        private static IEnumerable<FullTrack> MapToFullTracks(IEnumerable<SavedTrack> savedTracks)
        {
            return savedTracks
                .Select(item => item?.Track)
                .Where(track => track != null);
        }

        private void UpdateSummary()
        {
            var duration = ViewModel.LoadedDuration;
            var durationText = $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}";
            var approximate = ViewModel.HasMoreTracks ? "+" : string.Empty;
            SummaryText.Text = $"{ViewModel.SongCount} songs • {durationText}{approximate}";
        }

        private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(SortComboBox.SelectedItem is ComboBoxItem item))
                return;

            ViewModel.SortDescending = !string.Equals(item.Tag as string, "asc", StringComparison.OrdinalIgnoreCase);
            TrackList.IsTrackPersistedResolver = track => App.OfflineCatalog.IsTrackPersisted(track?.Uri);
            TrackList.AddTracks(MapToFullTracks(ViewModel.GetOrderedTracks()), true, 0);
            UpdateSummary();
        }

        private static string BuildCacheTooltip(DateTimeOffset? cachedAt)
        {
            return cachedAt.HasValue
                ? $"Cached on {cachedAt.Value.LocalDateTime:dd MMM yyyy} at {cachedAt.Value.LocalDateTime:HH:mm:ss}"
                : "Cached data is being shown. Last refresh: Unknown.";
        }

        private void SetIsLoading(bool isLoading, string message)
        {
            LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            LoadingText.Text = message ?? "Loading liked songs...";
        }
    }
}


