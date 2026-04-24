using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using SpotifyAPI.Web;
using System.Collections.Generic;
using System;
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
        public string StatusMessage { get; private set; }
        public DateTimeOffset? CachedAt { get; private set; }

        public List<SimpleTrack> LastLoadedBatch { get; private set; } = new List<SimpleTrack>();
        public bool HasMoreTracks => Tracks != null && Tracks.Items.Count < (Tracks.Total ?? 0);
        public int TotalTracksLoaded => Tracks?.Items?.Count ?? 0;

        public async Task LoadAsync(string id, bool forceRefresh = false)
        {
            try
            {
                var albumResponse = await _web.GetAlbumAsync(id, forceRefresh);
                var tracksResponse = await _web.GetAlbumTracksAsync(id, forceRefresh);

                Album = albumResponse.Value;
                Tracks = tracksResponse.Value;
                LastLoadedBatch = Tracks?.Items?.ToList() ?? new List<SimpleTrack>();
                StatusMessage = BuildStatusMessage(albumResponse, tracksResponse);
                CachedAt = GetCachedAt(albumResponse, tracksResponse);
            }
            catch (Exception)
            {
                Album = null;
                Tracks = new Paging<SimpleTrack> { Items = new List<SimpleTrack>() };
                LastLoadedBatch = new List<SimpleTrack>();
                StatusMessage = ConnectivityHelper.HasInternetAccess()
                    ? "This album could not be loaded right now."
                    : "Offline. This album is not available because it has not been cached yet.";
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
            if (Album == null)
                return;

            await App.Media.SetShuffleAsync(false);
            await App.Media.PlayAsync($"spotify:album:{Album.Id}", "");
        }

        public async void ShuffleAlbum()
        {
            if (Album == null)
                return;

            await App.Media.SetShuffleAsync(true);
            await App.Media.PlayAsync($"spotify:album:{Album.Id}", "");
        }

        private static string BuildStatusMessage<T1, T2>(CacheResponse<T1> first, CacheResponse<T2> second)
        {
            var offline = !ConnectivityHelper.HasInternetAccess();
            if (first?.IsOfflineFallback == true || second?.IsOfflineFallback == true)
                return "Offline. Album details are being shown from cache.";

            if (offline)
                return "Offline. This album is only available here because it was cached earlier.";

            if (first?.IsFromCache == true || second?.IsFromCache == true)
                return "Showing cached album details.";

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
