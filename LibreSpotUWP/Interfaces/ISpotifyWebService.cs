using SpotifyAPI.Web;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LibreSpotUWP.Interfaces
{
    public interface ISpotifyWebService
    {
        Task<FullTrack> GetTrackAsync(string trackId, CancellationToken ct = default(CancellationToken));
        Task<FullAlbum> GetAlbumAsync(string albumId, CancellationToken ct = default(CancellationToken));
        Task<FullPlaylist> GetPlaylistAsync(string playlistId, CancellationToken ct = default(CancellationToken));

        Task<SearchResponse> SearchAsync(
            string query,
            SearchRequest.Types type,
            CancellationToken ct = default(CancellationToken));
    }
}