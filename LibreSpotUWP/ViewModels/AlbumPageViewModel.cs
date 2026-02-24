using LibreSpotUWP.Interfaces;
using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LibreSpotUWP.ViewModels
{
    public class AlbumPageViewModel
    {
        private readonly ISpotifyWebService _web = App.SpotifyWeb;
        private bool _isLoading = false;

        public FullAlbum Album { get; private set; }
        public Paging<SimpleTrack> Tracks { get; private set; }

        public List<SimpleTrack> LastLoadedBatch { get; private set; } = new List<SimpleTrack>();
        public bool HasMoreTracks => Tracks != null && Tracks.Items.Count < (Tracks.Total ?? 0);
        public int TotalTracksLoaded => Tracks?.Items?.Count ?? 0;

        public async Task LoadAsync(string id)
        {
            Album = (await _web.GetAlbumAsync(id)).Value;
            Tracks = (await _web.GetAlbumTracksAsync(id)).Value;

            LastLoadedBatch = Tracks?.Items?.ToList() ?? new List<SimpleTrack>();
        }

        public async Task LoadMoreTracksAsync()
        {
            if (!HasMoreTracks || _isLoading || Tracks?.Next == null)
                return;

            try
            {
                _isLoading = true;
                var result = await _web.GetNextPageAsync(Tracks);

                if (result?.Value != null)
                {
                    var nextPaging = result.Value;
                    LastLoadedBatch = nextPaging.Items?.ToList() ?? new List<SimpleTrack>();

                    var fullList = Tracks.Items.ToList();
                    fullList.AddRange(nextPaging.Items);

                    Tracks = nextPaging;
                    Tracks.Items = fullList;
                }
            }
            finally
            {
                _isLoading = false;
            }
        }

        public async void PlayAlbum()
        {
            if (Album == null) return;
            await App.Media.SetShuffleAsync(false);
            await App.Media.PlayAsync($"spotify:album:{Album.Id}", "");
        }

        public async void ShuffleAlbum()
        {
            if (Album == null) return;
            await App.Media.SetShuffleAsync(true);
            await App.Media.PlayAsync($"spotify:album:{Album.Id}", "");
        }
    }
}