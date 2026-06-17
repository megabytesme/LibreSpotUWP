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
        private const int PageSize = 50;
        private bool _isLoading;
        private bool _sortDescending = true;
        private int _nextOffset;
        private int _nextLimit = PageSize;

        public Paging<SavedTrack> Tracks { get; private set; }
        public string StatusMessage { get; private set; }
        public DateTimeOffset? CachedAt { get; private set; }
        public List<SavedTrack> LastLoadedBatch { get; private set; } = new List<SavedTrack>();
        public bool SortDescending
        {
            get => _sortDescending;
            set => _sortDescending = value;
        }
        public bool HasMoreTracks => Tracks != null && _nextOffset >= 0 && _nextLimit > 0 && _nextOffset < (Tracks.Total ?? 0);
        public int TotalTracksLoaded => Tracks?.Items?.Count ?? 0;
        public int SongCount => Tracks?.Total ?? Tracks?.Items?.Count ?? 0;
        public TimeSpan LoadedDuration => TimeSpan.FromMilliseconds(
            (Tracks?.Items?.AsEnumerable() ?? Enumerable.Empty<SavedTrack>()).Sum(item => (long)(item?.Track?.DurationMs ?? 0)));

        public IEnumerable<SavedTrack> GetOrderedTracks()
        {
            return Tracks?.Items?.AsEnumerable() ?? Enumerable.Empty<SavedTrack>();
        }

        public async Task LoadAsync(bool forceRefresh = false)
        {
            try
            {
                CacheResponse<Paging<SavedTrack>> response;
                if (_sortDescending)
                {
                    response = await _web.GetSavedTracksPageAsync(PageSize, 0, forceRefresh);
                }
                else
                {
                    var firstPage = await _web.GetSavedTracksPageAsync(1, 0, forceRefresh);
                    var total = firstPage?.Value?.Total ?? 0;
                    var offset = Math.Max(0, total - PageSize);
                    response = await _web.GetSavedTracksPageAsync(PageSize, offset, forceRefresh);
                }

                Tracks = response.Value ?? new Paging<SavedTrack> { Items = new List<SavedTrack>() };
                Tracks.Items = OrderBatch(Tracks.Items).ToList();
                LastLoadedBatch = Tracks.Items?.ToList() ?? new List<SavedTrack>();
                UpdateNextPageState(Tracks);
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
            if (!HasMoreTracks || _isLoading)
                return;

            try
            {
                _isLoading = true;
                LastLoadedBatch = new List<SavedTrack>();
                var result = await _web.GetSavedTracksPageAsync(_nextLimit, _nextOffset);
                if (result?.Value != null)
                {
                    var nextPaging = result.Value;
                    LastLoadedBatch = OrderBatch(nextPaging.Items).ToList();

                    var fullList = Tracks.Items.ToList();
                    fullList.AddRange(LastLoadedBatch);

                    Tracks = nextPaging;
                    Tracks.Items = fullList;
                    UpdateNextPageState(nextPaging);
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

        private IEnumerable<SavedTrack> OrderBatch(IEnumerable<SavedTrack> items)
        {
            items = items ?? Enumerable.Empty<SavedTrack>();
            return _sortDescending
                ? items.OrderByDescending(item => item?.AddedAt ?? DateTime.MinValue)
                : items.OrderBy(item => item?.AddedAt ?? DateTime.MinValue);
        }

        private void UpdateNextPageState(Paging<SavedTrack> paging)
        {
            var total = paging?.Total ?? 0;
            var loaded = paging?.Items?.Count ?? 0;
            if (loaded >= total)
            {
                _nextOffset = -1;
                _nextLimit = 0;
                return;
            }

            if (_sortDescending)
            {
                _nextOffset = loaded;
                _nextLimit = PageSize;
                return;
            }

            var currentOffset = paging?.Offset ?? 0;
            if (currentOffset <= 0)
            {
                _nextOffset = -1;
                _nextLimit = 0;
                return;
            }

            _nextLimit = Math.Min(PageSize, currentOffset);
            _nextOffset = currentOffset - _nextLimit;
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
