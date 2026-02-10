using SpotifyAPI.Web;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LibreSpotUWP.Interfaces
{
    public interface ISpotifyWebService
    {
        Task<CursorPaging<PlayHistoryItem>> GetRecentlyPlayedAsync(int limit = 20, CancellationToken ct = default);
        Task<FullTrack> GetTrackAsync(string trackId, CancellationToken ct = default);
        Task<FullAlbum> GetAlbumAsync(string albumId, CancellationToken ct = default);
        Task<FullArtist> GetArtistAsync(string artistId, CancellationToken ct = default);
        Task<FullPlaylist> GetPlaylistAsync(string playlistId, CancellationToken ct = default);
        Task<Paging<SimpleTrack>> GetAlbumTracksAsync(string albumId, CancellationToken ct = default);
        Task<Paging<SimpleAlbum>> GetArtistAlbumsAsync(string artistId, CancellationToken ct = default);
        Task<Paging<PlaylistTrack<IPlayableItem>>> GetPlaylistItemsAsync(string playlistId, CancellationToken ct = default);
        Task<SearchResponse> SearchAsync(string query, SearchRequest.Types type, CancellationToken ct = default);
        Task<PrivateUser> GetCurrentUserAsync(CancellationToken ct = default);
        Task<Paging<FullTrack>> GetUserTopTracksAsync(int limit = 20, CancellationToken ct = default);
        Task<Paging<FullArtist>> GetUserTopArtistsAsync(int limit = 20, CancellationToken ct = default);
        Task<Paging<SavedTrack>> GetSavedTracksAsync(CancellationToken ct = default);
        Task<Paging<SavedAlbum>> GetSavedAlbumsAsync(CancellationToken ct = default);
        Task<Paging<FullPlaylist>> GetCurrentUserPlaylistsAsync(CancellationToken ct = default);
        Task<FollowedArtistsResponse> GetFollowedArtistsAsync(CancellationToken ct = default);
    }
}