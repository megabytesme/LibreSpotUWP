using LibreSpotUWP.ViewModels;
using SpotifyAPI.Web;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LibreSpotUWP.Views
{
    public sealed partial class SearchPage : Page
    {
        public SearchPageViewModel ViewModel { get; } = new SearchPageViewModel();

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

            await ViewModel.LoadAsync(query);
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem;

            if (item is FullArtist artist)
                GetMainPage()?.NavigateToArtist(artist.Id);

            else if (item is SimpleAlbum album)
                GetMainPage()?.NavigateToAlbum(album.Id);

            else if (item is FullPlaylist playlist)
                GetMainPage()?.NavigateToPlaylist(playlist.Id);

            else if (item is FullTrack track)
                App.Media.PlayAsync("", track.Uri);
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
    }
}
