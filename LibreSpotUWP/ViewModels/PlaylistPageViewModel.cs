using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace LibreSpotUWP.ViewModels
{
    public class PlaylistPageViewModel
    {
        private readonly ISpotifyWebService _web = App.SpotifyWeb;
        private bool _isLoading = false;

        public FullPlaylist Playlist { get; private set; }
        public Paging<PlaylistTrack<IPlayableItem>> Tracks { get; private set; }
        public string StatusMessage { get; private set; }
        public DateTimeOffset? CachedAt { get; private set; }
        public List<PlaylistTrack<IPlayableItem>> LastLoadedBatch { get; private set; } = new List<PlaylistTrack<IPlayableItem>>();
        public bool HasMoreTracks => Tracks != null && Tracks.Items.Count < (Tracks.Total ?? 0);
        public int TotalTracksLoaded => Tracks?.Items?.Count ?? 0;

        public async Task LoadAsync(string id, bool forceRefresh = false)
        {
            try
            {
                var playlistResponse = await _web.GetPlaylistAsync(id, forceRefresh);
                var tracksResponse = await _web.GetPlaylistItemsAsync(id, forceRefresh);

                Playlist = playlistResponse.Value;
                Tracks = tracksResponse.Value;
                LastLoadedBatch = Tracks?.Items?.ToList() ?? new List<PlaylistTrack<IPlayableItem>>();
                StatusMessage = BuildStatusMessage(playlistResponse, tracksResponse);
                CachedAt = GetCachedAt(playlistResponse, tracksResponse);
            }
            catch (Exception)
            {
                Playlist = null;
                Tracks = new Paging<PlaylistTrack<IPlayableItem>> { Items = new List<PlaylistTrack<IPlayableItem>>() };
                LastLoadedBatch = new List<PlaylistTrack<IPlayableItem>>();
                StatusMessage = ConnectivityHelper.HasInternetAccess()
                    ? "This playlist could not be loaded right now."
                    : "Offline. This playlist is not available because it has not been cached yet.";
                CachedAt = null;
            }
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
            if (Playlist == null)
                return;

            await App.Media.SetShuffleAsync(false);
            await App.Media.PlayAsync($"spotify:playlist:{Playlist.Id}", "");
        }

        public async void ShufflePlaylist()
        {
            if (Playlist == null)
                return;

            await App.Media.SetShuffleAsync(true);
            await App.Media.PlayAsync($"spotify:playlist:{Playlist.Id}", "");
        }

        private static string BuildStatusMessage<T1, T2>(CacheResponse<T1> first, CacheResponse<T2> second)
        {
            var offline = !ConnectivityHelper.HasInternetAccess();
            if (first?.IsOfflineFallback == true || second?.IsOfflineFallback == true)
                return "Offline. Playlist details are being shown from cache.";

            if (offline)
                return "Offline. This playlist is only available here because it was cached earlier.";

            if (first?.IsFromCache == true || second?.IsFromCache == true)
                return "Showing cached playlist details.";

            return null;
        }

        private static DateTimeOffset? GetCachedAt<T1, T2>(CacheResponse<T1> first, CacheResponse<T2> second)
        {
            DateTimeOffset? best = null;

            if (first?.IsFromCache == true || first?.IsOfflineFallback == true)
                best = first.Timestamp;

            if (second?.IsFromCache == true || second?.IsOfflineFallback == true)
                best = !best.HasValue || second.Timestamp > best.Value ? second.Timestamp : best;

            return best;
        }
    }
}
