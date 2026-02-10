using LibreSpotUWP.Exceptions;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.ViewModels;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LibreSpotUWP.Views.Win10_1507
{
    public sealed partial class HomePage_Win10_1507 : Page
    {
        public HomePageViewModel ViewModel { get; } = new HomePageViewModel();

        private ISpotifyAuthService _auth;
        private ISpotifyWebService _spotify;

        public HomePage_Win10_1507()
        {
            InitializeComponent();
            DataContext = this;
            Loaded += HomePage_Loaded;
        }

        private async void HomePage_Loaded(object sender, RoutedEventArgs e)
        {
            _auth = App.SpotifyAuth;
            _spotify = App.SpotifyWeb;

            if (!await EnsureAuthenticatedAsync())
                return;

            await LoadHomepageAsync();
        }

        private async Task<bool> EnsureAuthenticatedAsync()
        {
            if (_auth.Current == null)
                return false;

            if (_auth.Current.IsExpired)
            {
                await _auth.RefreshAsync();
                if (_auth.Current.IsExpired)
                    return false;
            }

            return true;
        }

        private async Task LoadHomepageAsync()
        {
            try
            {
                var recently = await _spotify.GetRecentlyPlayedAsync(20);
                ViewModel.RecentlyPlayed.Clear();
                foreach (var item in recently.Items)
                {
                    ViewModel.RecentlyPlayed.Add(item);
                }

                var playlists = await _spotify.GetCurrentUserPlaylistsAsync();
                ViewModel.UserPlaylists.Clear();
                foreach (var p in playlists.Items)
                {
                    ViewModel.UserPlaylists.Add(p);
                }

                var topArtists = await _spotify.GetUserTopArtistsAsync(20);
                ViewModel.TopArtists.Clear();
                foreach (var a in topArtists.Items)
                {
                    ViewModel.TopArtists.Add(a);
                }

                var topTracks = await _spotify.GetUserTopTracksAsync(20);
                ViewModel.TopTracks.Clear();
                foreach (var t in topTracks.Items)
                {
                    ViewModel.TopTracks.Add(t);
                }
            }
            catch (SpotifyWebException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Homepage Load Failed: {ex.Message}");
            }
        }
    }
}