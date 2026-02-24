using LibreSpotUWP.Interfaces;
using SpotifyAPI.Web;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Diagnostics;

namespace LibreSpotUWP.ViewModels
{
    public class PlaylistPageViewModel
    {
        private readonly ISpotifyWebService _web = App.SpotifyWeb;
        private bool _isLoading = false;

        public FullPlaylist Playlist { get; private set; }
        public Paging<PlaylistTrack<IPlayableItem>> Tracks { get; private set; }
        public List<PlaylistTrack<IPlayableItem>> LastLoadedBatch { get; private set; } = new List<PlaylistTrack<IPlayableItem>>();
        public bool HasMoreTracks => Tracks != null && Tracks.Items.Count < (Tracks.Total ?? 0);
        public int TotalTracksLoaded => Tracks?.Items?.Count ?? 0;

        public async Task LoadAsync(string id)
        {
            var playlistResponse = await _web.GetPlaylistAsync(id);
            Playlist = playlistResponse.Value;

            var tracksResponse = await _web.GetPlaylistItemsAsync(id);
            Tracks = tracksResponse.Value;

            LastLoadedBatch = Tracks?.Items?.ToList() ?? new List<PlaylistTrack<IPlayableItem>>();
        }

        public async Task LoadMoreTracksAsync()
        {
            if (!HasMoreTracks || _isLoading)
            {
                return;
            }

            if (Tracks?.Next == null)
            {
                return;
            }

            if (_isLoading)
            {
                return;
            }

            try
            {
                _isLoading = true;

                var result = await _web.GetNextPageAsync(Tracks);

                if (result?.Value != null)
                {
                    var nextPaging = result.Value;

                    LastLoadedBatch = nextPaging.Items?.ToList() ?? new List<PlaylistTrack<IPlayableItem>>();

                    var fullList = Tracks.Items.ToList();
                    fullList.AddRange(nextPaging.Items);

                    Tracks = nextPaging;
                    Tracks.Items = fullList;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PlaylistPageViewModel] ERROR during pagination: {ex.Message}");
            }
            finally
            {
                _isLoading = false;
            }
        }

        public async void PlayPlaylist()
        {
            if (Playlist == null) return;

            await App.Media.SetShuffleAsync(false);
            await App.Media.PlayAsync($"spotify:playlist:{Playlist.Id}", "");
        }

        public async void ShufflePlaylist()
        {
            if (Playlist == null) return;

            await App.Media.SetShuffleAsync(true);
            await App.Media.PlayAsync($"spotify:playlist:{Playlist.Id}", "");
        }
    }
}