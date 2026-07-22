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
    public sealed partial class SearchPage : Page
    {
        public SearchPageViewModel ViewModel { get; } = new SearchPageViewModel();
        private string _query;

        public SearchPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var query = e.Parameter as string;
            if (string.IsNullOrWhiteSpace(query))
                return;

            _query = query;
            await ViewModel.LoadAsync(query);
            UpdateStatusBanner();
        }

        private async void OnItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem;

            if (item is FullArtist artist)
                GetMainPage()?.NavigateToArtist(artist.Id);

            else if (item is SimpleAlbum album)
                GetMainPage()?.NavigateToAlbum(album.Id);

            else if (item is FullPlaylist playlist)
                GetMainPage()?.NavigateToPlaylist(playlist.Id);

            else if (item is FullTrack track)
            {
                var queue = (ViewModel.Tracks ?? new List<FullTrack>())
                    .Select(candidate => candidate?.Uri)
                    .Where(uri => !string.IsNullOrWhiteSpace(uri))
                    .ToList();
                await App.Media.PlayAsync(track.Uri, null, queue, ViewModel.Tracks?.IndexOf(track) ?? -1);
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
                RefreshSearchAsync);
        }

        private MainPage GetMainPage()
        {
            return (Window.Current.Content as Frame)?.Content as MainPage;
        }

        public string GetImageUrl(IList<SpotifyAPI.Web.Image> images)
        {
            return (images != null && images.Count > 0) ? images[0].Url : null;
        }

        public string GetAlbumImageUrl(SpotifyAPI.Web.FullTrack track)
        {
            return (track?.Album?.Images != null && track.Album.Images.Count > 0)
                ? track.Album.Images[0].Url
                : null;
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshSearchAsync();
        }

        private async Task RefreshSearchAsync()
        {
            if (string.IsNullOrWhiteSpace(_query))
                return;

            try
            {
                await ViewModel.LoadAsync(_query, true);
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
