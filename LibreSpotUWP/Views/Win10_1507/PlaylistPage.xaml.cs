using LibreSpotUWP.Controls;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using LibreSpotUWP.ViewModels;
using SpotifyAPI.Web;
using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace LibreSpotUWP.Views
{
    public sealed partial class PlaylistPage : Page
    {
        public PlaylistPageViewModel ViewModel { get; } = new PlaylistPageViewModel();

        public PlaylistPage()
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
                ViewModel.PlayPlaylist();
            };

            PlayActions.ShuffleRequested += (s, e) =>
            {
                ViewModel.ShufflePlaylist();
            };

            TrackList.TrackClicked += OnTrackClicked;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            string playlistId = e.Parameter as string;
            await ViewModel.LoadAsync(playlistId);

            HeaderControl.SetPlaylist(ViewModel.Playlist);
            TrackList.SetPlaylistTracks(ViewModel.Tracks.Items);
        }

        public async void OnTrackClicked(object sender, TrackClickedEventArgs e)
        {
            var trackUri = (e.Track as FullTrack)?.Uri ?? (e.Track as SimpleTrack)?.Uri;
            if (trackUri == null) return;

            await App.Media.PlayAsync($"spotify:playlist:{ViewModel.Playlist.Id}", trackUri);
        }
    }
}