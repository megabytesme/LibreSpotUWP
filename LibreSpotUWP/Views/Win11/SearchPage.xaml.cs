using LibreSpotUWP.ViewModels;
using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;

namespace LibreSpotUWP.Views.Win11
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

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem;

            if (item is FullArtist artist)
                GetShell()?.NavigateToArtist(artist.Id);

            else if (item is SimpleAlbum album)
                GetShell()?.NavigateToAlbum(album.Id);

            else if (item is FullPlaylist playlist)
                GetShell()?.NavigateToPlaylist(playlist.Id);

            else if (item is FullTrack track)
                App.Media.PlayAsync(track.Uri, null);
        }

        private void UpdateStatusBanner()
        {
            CacheIndicator.Visibility = Visibility.Collapsed;

            var shell = GetShell();
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
                RefreshSearchAsync);
        }

        private IAppShell GetShell()
        {
            return PlaybackNavigationHelper.FindShell(this);
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


