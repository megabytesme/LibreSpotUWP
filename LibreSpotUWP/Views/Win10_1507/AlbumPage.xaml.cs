using LibreSpotUWP.Controls;
using LibreSpotUWP.ViewModels;
using SpotifyAPI.Web;
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
                ViewModel.PlayAlbum();
            };

            PlayActions.ShuffleRequested += (s, e) =>
            {
                ViewModel.ShuffleAlbum();
            };

            TrackList.TrackClicked += OnTrackClicked;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            string albumId = e.Parameter as string;
            await ViewModel.LoadAsync(albumId);

            HeaderControl.SetAlbum(ViewModel.Album);
            TrackList.SetTracks(ViewModel.Tracks.Items);
        }

        public async void OnTrackClicked(object sender, TrackClickedEventArgs e)
        {
            var trackUri = (e.Track as FullTrack)?.Uri ?? (e.Track as SimpleTrack)?.Uri;
            if (trackUri == null) return;

            await App.Media.PlayAsync($"spotify:album:{ViewModel.Album.Id}", trackUri);
        }
    }
}