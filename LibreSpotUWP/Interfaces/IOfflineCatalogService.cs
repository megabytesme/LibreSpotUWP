using LibreSpotUWP.Models;
using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LibreSpotUWP.Interfaces
{
    public interface IOfflineCatalogService
    {
        Task InitializeAsync();
        bool IsTrackPersisted(string trackUri);
        bool IsAlbumPersisted(string albumId);
        bool IsPlaylistPersisted(string playlistId);
        Task SetTrackPersistedAsync(FullTrack track, bool persisted);
        Task SetAlbumPersistedAsync(FullAlbum album, IEnumerable<SimpleTrack> tracks, bool persisted);
        Task SetPlaylistPersistedAsync(FullPlaylist playlist, IEnumerable<FullTrack> tracks, bool persisted);
        Task<IReadOnlyList<OfflineTrackEntry>> GetDownloadedTracksAsync();
        Task<IReadOnlyList<OfflineAlbumEntry>> GetDownloadedAlbumsAsync();
        Task<IReadOnlyList<OfflinePlaylistEntry>> GetDownloadedPlaylistsAsync();
        Task<IReadOnlyList<string>> GetTrackUrisForContextAsync(string contextUri);
        Task<OfflineTrackEntry> GetDownloadedTrackAsync(string trackUri);
        Task RenewPersistedTrackLeasesAsync(DateTimeOffset expiresAtUtc);
        Task RemoveExpiredPersistedTracksAsync();
    }
}
