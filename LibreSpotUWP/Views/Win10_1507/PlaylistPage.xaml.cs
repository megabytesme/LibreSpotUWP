using LibreSpotUWP.Controls;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using LibreSpotUWP.ViewModels;
using SpotifyAPI.Web;
using System;
using System.Linq;
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

            TrackList.LoadMoreRequested += OnLoadMoreRequested;
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
                var newTracks = ViewModel.LastLoadedBatch
                    .Select(t => t.Track as FullTrack)
                    .Where(t => t != null);

                int offset = ViewModel.TotalTracksLoaded - ViewModel.LastLoadedBatch.Count;
                TrackList.AddTracks(newTracks, false, offset);
            }
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            string playlistId = e.Parameter as string;
            await ViewModel.LoadAsync(playlistId);

            HeaderControl.SetPlaylist(ViewModel.Playlist);

            var tracks = ViewModel.Tracks.Items.Select(t => t.Track as FullTrack).Where(t => t != null);
            TrackList.AddTracks(tracks, true, 0);
        }

        public async void OnTrackClicked(object sender, TrackClickedEventArgs e)
        {
            var trackUri = (e.Track as FullTrack)?.Uri ?? (e.Track as SimpleTrack)?.Uri;
            if (trackUri == null) return;

            await App.Media.PlayAsync($"spotify:playlist:{ViewModel.Playlist.Id}", trackUri);
        }
    }
}