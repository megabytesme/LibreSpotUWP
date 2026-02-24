using LibreSpotUWP.Controls;
using LibreSpotUWP.ViewModels;
using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using System.Linq;
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

            TrackList.ArtistClicked += (s, artistId) => NavigateToMain("Artist", artistId);
            TrackList.AlbumClicked += (s, albumId) => NavigateToMain("Album", albumId);
            PlayActions.PlayRequested += (s, e) => ViewModel.PlayAlbum();
            PlayActions.ShuffleRequested += (s, e) => ViewModel.ShuffleAlbum();
            TrackList.TrackClicked += OnTrackClicked;
            TrackList.LoadMoreRequested += OnLoadMoreRequested;
        }

        private void NavigateToMain(string type, string id)
        {
            var frame = Window.Current.Content as Frame;
            var main = frame?.Content as MainPage;
            if (type == "Artist") main?.NavigateToArtist(id);
            else main?.NavigateToAlbum(id);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            string albumId = e.Parameter as string;
            await ViewModel.LoadAsync(albumId);

            HeaderControl.SetAlbum(ViewModel.Album);

            var tracks = MapToFullTracks(ViewModel.Tracks.Items);
            TrackList.AddTracks(tracks, true, 0);
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
                var newTracks = MapToFullTracks(ViewModel.LastLoadedBatch);
                int offset = ViewModel.TotalTracksLoaded - ViewModel.LastLoadedBatch.Count;
                TrackList.AddTracks(newTracks, false, offset);
            }
        }

        private IEnumerable<FullTrack> MapToFullTracks(IEnumerable<SimpleTrack> simpleTracks)
        {
            return simpleTracks.Select(st => new FullTrack
            {
                Name = st.Name,
                Artists = st.Artists,
                DurationMs = st.DurationMs,
                Uri = st.Uri,
                Id = st.Id
            });
        }

        public async void OnTrackClicked(object sender, TrackClickedEventArgs e)
        {
            var trackUri = (e.Track as FullTrack)?.Uri ?? (e.Track as SimpleTrack)?.Uri;
            if (trackUri == null || ViewModel.Album == null) return;

            await App.Media.PlayAsync($"spotify:album:{ViewModel.Album.Id}", trackUri);
        }
    }
}