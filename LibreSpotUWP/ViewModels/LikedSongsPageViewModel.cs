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
    public class LikedSongsPageViewModel
    {
        private readonly ISpotifyWebService _web = App.SpotifyWeb;
        private bool _isLoading;

        public Paging<SavedTrack> Tracks { get; private set; }
        public string StatusMessage { get; private set; }
        public DateTimeOffset? CachedAt { get; private set; }
        public List<SavedTrack> LastLoadedBatch { get; private set; } = new List<SavedTrack>();
        public bool HasMoreTracks => Tracks != null && Tracks.Items.Count < (Tracks.Total ?? 0);
        public int TotalTracksLoaded => Tracks?.Items?.Count ?? 0;

        public async Task LoadAsync(bool forceRefresh = false)
        {
            try
            {
                var response = await _web.GetSavedTracksAsync(forceRefresh);
                Tracks = response.Value ?? new Paging<SavedTrack> { Items = new List<SavedTrack>() };
                LastLoadedBatch = Tracks.Items?.ToList() ?? new List<SavedTrack>();
                StatusMessage = BuildStatusMessage(response);
                CachedAt = response.IsFromCache || response.IsOfflineFallback
                    ? (DateTimeOffset?)response.Timestamp
                    : null;
            }
            catch (Exception)
            {
                Tracks = new Paging<SavedTrack> { Items = new List<SavedTrack>() };
                LastLoadedBatch = new List<SavedTrack>();
                StatusMessage = ConnectivityHelper.HasInternetAccess()
                    ? "Liked Songs could not be loaded right now."
                    : "Offline. Liked Songs is not available because it has not been cached yet.";
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
                    LastLoadedBatch = nextPaging.Items?.ToList() ?? new List<SavedTrack>();

                    var fullList = Tracks.Items.ToList();
                    fullList.AddRange(nextPaging.Items);

                    Tracks = nextPaging;
                    Tracks.Items = fullList;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LikedSongsPageViewModel] ERROR during pagination: {ex.Message}");
            }
            finally
            {
                _isLoading = false;
            }
        }

        private static string BuildStatusMessage(CacheResponse<Paging<SavedTrack>> response)
        {
            if (response?.IsOfflineFallback == true)
                return "Offline. Liked Songs is being shown from cache.";

            if (!ConnectivityHelper.HasInternetAccess())
                return "Offline. Liked Songs is only available here because it was cached earlier.";

            if (response?.IsFromCache == true)
                return "Showing cached liked songs.";

            return null;
        }
    }
}
