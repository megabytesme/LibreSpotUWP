using LibreSpotUWP.Exceptions;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.ViewModels;
using SpotifyAPI.Web;
using System;
using System.Diagnostics;
using System.Threading;
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
        private CancellationTokenSource _cts;

        public HomePage_Win10_1507()
        {
            this.InitializeComponent();
            this.DataContext = this;
            this.Loaded += HomePage_Loaded;
            this.Unloaded += (s, e) => _cts?.Cancel();
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
            if (_auth.Current == null) return false;

            if (_auth.Current.IsExpired)
            {
                try
                {
                    await _auth.RefreshAsync();
                }
                catch
                {
                    return false;
                }
            }
            return !_auth.Current.IsExpired;
        }

        private async Task LoadHomepageAsync()
        {
            try
            {
                _cts?.Cancel();
                _cts = new CancellationTokenSource();

                await ViewModel.LoadAsync(_spotify, _cts.Token);
            }
            catch (OperationCanceledException) {  }
            catch (SpotifyWebException ex)
            {
                System.Diagnostics.Debug.WriteLine("Homepage Load Failed: " + ex.Message);
            }
        }

        private void HomeItem_Click(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem;

            var frame = (Window.Current.Content as Frame);
            var mainPage = frame?.Content as MainPage;
            if (mainPage == null)
                return;

            switch (item)
            {
                case FullAlbum album:
                    mainPage.NavigateToAlbum(album.Id);
                    Debug.WriteLine($"Navigating to album: {album.Name}");
                    break;

                case SavedAlbum saved:
                    mainPage.NavigateToAlbum(saved.Album.Id);
                    Debug.WriteLine($"Navigating to saved album: {saved.Album.Name}");
                    break;

                case FullArtist artist:
                    mainPage.NavigateToArtist(artist.Id);
                    Debug.WriteLine($"Navigating to artist: {artist.Name}");
                    break;

                case FullPlaylist playlist:
                    mainPage.NavigateToPlaylist(playlist.Id);
                    Debug.WriteLine($"Navigating to playlist: {playlist.Name}");
                    break;

                case FullTrack track:
                    mainPage.NavigateToAlbum(track.Album.Id);
                    Debug.WriteLine($"Navigating to track: {track.Name}");
                    break;

                default:
                    Debug.WriteLine("Unknown item type clicked: " + item.GetType().Name);
                    break;
            }
        }
    }
}