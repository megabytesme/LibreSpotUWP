using LibreSpotUWP.Controls;
using LibreSpotUWP.ViewModels;
using SpotifyAPI.Web;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LibreSpotUWP.Views
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

            HeaderControl.SetArtist(ViewModel.Artist);

            TrackList.AddTracks(ViewModel.TopTracks, true);

            AlbumsGrid.SetAlbums(ViewModel.Albums.Items);
        }

        public async void OnTrackClicked(object sender, TrackClickedEventArgs e)
        {
            if (e.Track is FullTrack ft)
            {
                await App.Media.PlayAsync(ft.Uri, "");
            }
            else if (e.Track is SimpleTrack st)
            {
                await App.Media.PlayAsync(st.Uri, "");
            }
        }
    } 
}
