using LibreSpotUWP.Controls;
using LibreSpotUWP.ViewModels;
using SpotifyAPI.Web;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LibreSpotUWP.Views.Win10_1709
{
    public sealed partial class ArtistPage : Page
    {
        public ArtistPageViewModel ViewModel { get; } = new ArtistPageViewModel();

        public ArtistPage()
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

            TrackList.TrackClicked += OnTrackClicked;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            string artistId = e.Parameter as string;
            await ViewModel.LoadAsync(artistId);

            if (ViewModel.Artist != null)
                HeaderControl.SetArtist(ViewModel.Artist);
            UpdateStatusBanner();

            TrackList.IsTrackPersistedResolver = track => App.OfflineCatalog.IsTrackPersisted(track?.Uri);
            TrackList.AddTracks(ViewModel.TopTracks ?? new System.Collections.Generic.List<FullTrack>(), true);

            AlbumsGrid.SetAlbums(ViewModel.Albums?.Items ?? new System.Collections.Generic.List<SimpleAlbum>());
        }

        public async void OnTrackClicked(object sender, TrackClickedEventArgs e)
        {
            var queue = (ViewModel.TopTracks ?? new System.Collections.Generic.List<FullTrack>())
                .Select(track => track?.Uri)
                .Where(uri => !string.IsNullOrWhiteSpace(uri))
                .ToList();
            if (e.Track is FullTrack ft)
            {
                await App.Media.PlayAsync(ft.Uri, null, queue, e.Index);
            }
            else if (e.Track is SimpleTrack st)
            {
                await App.Media.PlayAsync(st.Uri, null, queue, e.Index);
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
                RefreshArtistAsync);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshArtistAsync();
        }

        private async Task RefreshArtistAsync()
        {
            if (ViewModel.Artist == null)
                return;

            try
            {
                await ViewModel.LoadAsync(ViewModel.Artist.Id, true);
                if (ViewModel.Artist != null)
                    HeaderControl.SetArtist(ViewModel.Artist);
                UpdateStatusBanner();
                TrackList.AddTracks(ViewModel.TopTracks ?? new System.Collections.Generic.List<FullTrack>(), true);
                AlbumsGrid.SetAlbums(ViewModel.Albums?.Items ?? new System.Collections.Generic.List<SimpleAlbum>());
            }
            catch (OperationCanceledException)
            {
            }
        }

        private MainPage GetMainPage()
        {
            return (Window.Current.Content as Frame)?.Content as MainPage;
        }

        private static string BuildCacheTooltip(DateTimeOffset? cachedAt)
        {
            return cachedAt.HasValue
                ? $"Cached on {cachedAt.Value.LocalDateTime:dd MMM yyyy} at {cachedAt.Value.LocalDateTime:HH:mm:ss}"
                : "Cached data is being shown. Last refresh: Unknown.";
        }
    } 
}


