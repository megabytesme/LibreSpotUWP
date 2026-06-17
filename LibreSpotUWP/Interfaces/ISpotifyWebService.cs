using LibreSpotUWP.Models;
using SpotifyAPI.Web;
using System.Threading;
using System.Threading.Tasks;

namespace LibreSpotUWP.Interfaces
{
    public interface ISpotifyWebService
    {
        Task<CacheResponse<CursorPaging<PlayHistoryItem>>> GetRecentlyPlayedAsync(
            int limit = 20,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken());

        Task<CacheResponse<FullTrack>> GetTrackAsync(
            string trackId,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken());

        Task<CacheResponse<FullAlbum>> GetAlbumAsync(
            string albumId,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken());

        Task<CacheResponse<FullArtist>> GetArtistAsync(
            string artistId,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken());

        Task<CacheResponse<FullPlaylist>> GetPlaylistAsync(
            string playlistId,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken());

        Task<CacheResponse<Paging<SimpleTrack>>> GetAlbumTracksAsync(
            string albumId,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken());

        Task<CacheResponse<Paging<SimpleAlbum>>> GetArtistAlbumsAsync(
            string artistId,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken());

        Task<CacheResponse<Paging<PlaylistTrack<IPlayableItem>>>> GetPlaylistItemsAsync(
            string playlistId,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken());

        Task<CacheResponse<Paging<T>>> GetNextPageAsync<T>(
            Paging<T> currentPaging,
            CancellationToken ct = new CancellationToken());

        Task<CacheResponse<SearchResponse>> SearchAsync(
            string query,
            SearchRequest.Types type,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken());

        Task<CacheResponse<PrivateUser>> GetCurrentUserAsync(
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken());

        Task<CacheResponse<AppUserProfile>> GetCurrentUserProfileAsync(
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken());

        Task<CacheResponse<Paging<FullTrack>>> GetUserTopTracksAsync(
            int limit = 20,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken());

        Task<CacheResponse<Paging<FullArtist>>> GetUserTopArtistsAsync(
            int limit = 20,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken());

        Task<CacheResponse<Paging<SavedTrack>>> GetSavedTracksAsync(
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken());

        Task<CacheResponse<Paging<SavedAlbum>>> GetSavedAlbumsAsync(
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken());

        Task<CacheResponse<Paging<FullPlaylist>>> GetCurrentUserPlaylistsAsync(
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken());

        Task<CacheResponse<FollowedArtistsResponse>> GetFollowedArtistsAsync(
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken());

        Task<DeviceResponse> GetAvailableDevicesAsync(CancellationToken ct = new CancellationToken());
        Task<CurrentlyPlayingContext> GetCurrentPlaybackAsync(CancellationToken ct = new CancellationToken());
        Task<bool> TransferPlaybackAsync(string deviceId, bool play, CancellationToken ct = new CancellationToken());
        Task<bool> ResumePlaybackAsync(
            string deviceId,
            string contextUri = null,
            string startUri = null,
            int? positionMs = null,
            CancellationToken ct = new CancellationToken());
        Task<bool> PausePlaybackAsync(string deviceId, CancellationToken ct = new CancellationToken());
        Task<bool> SkipNextAsync(string deviceId, CancellationToken ct = new CancellationToken());
        Task<bool> SkipPreviousAsync(string deviceId, CancellationToken ct = new CancellationToken());
        Task<bool> SeekToAsync(string deviceId, long positionMs, CancellationToken ct = new CancellationToken());
        Task<bool> SetVolumeAsync(string deviceId, int volumePercent, CancellationToken ct = new CancellationToken());
        Task<bool> SetShuffleAsync(string deviceId, bool enabled, CancellationToken ct = new CancellationToken());
        Task<bool> SetRepeatAsync(string deviceId, int mode, CancellationToken ct = new CancellationToken());
        Task<bool> AddToQueueAsync(string deviceId, string uri, CancellationToken ct = new CancellationToken());
    }
}
