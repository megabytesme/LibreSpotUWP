using LibreSpotUWP.Exceptions;
using LibreSpotUWP.Interfaces;
using SpotifyAPI.Web;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LibreSpotUWP.Services
{
    public sealed class SpotifyWebService : ISpotifyWebService
    {
        private readonly ISpotifyAuthService _auth;
        private readonly IMetadataCache _cache;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(4);
        private SpotifyClient _client;

        private static readonly TimeSpan MetadataTtl = TimeSpan.FromHours(24);
        private static readonly TimeSpan PersonalDataTtl = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan LibraryTtl = TimeSpan.FromMinutes(30);

        public SpotifyWebService(ISpotifyAuthService auth, IMetadataCache cache)
        {
            _auth = auth;
            _cache = cache;

            _auth.AuthStateChanged += (_, state) =>
            {
                if (!state.IsExpired)
                    _client = new SpotifyClient(state.AccessToken);
            };

            if (_auth.Current != null && !_auth.Current.IsExpired)
                _client = new SpotifyClient(_auth.Current.AccessToken);
        }

        private async Task<T> ExecuteAsync<T>(Func<SpotifyClient, Task<T>> action, CancellationToken ct)
        {
            if (_client == null)
                throw new InvalidOperationException("Spotify client is not authenticated.");

            await _gate.WaitAsync(ct);
            try
            {
                return await action(_client);
            }
            catch (APIException apiEx)
            {
                throw new SpotifyWebException($"Spotify API Error: {apiEx.Response?.StatusCode}", apiEx);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<FullTrack> GetTrackAsync(string trackId, CancellationToken ct = default)
        {
            var user = await GetCurrentUserAsync(ct);
            return await _cache.GetOrAddAsync($"track_{trackId}_{user.Country}",
                () => ExecuteAsync(c => c.Tracks.Get(trackId, new TrackRequest { Market = user.Country }, ct), ct),
                TimeSpan.FromHours(24));
        }

        public async Task<FullAlbum> GetAlbumAsync(string albumId, CancellationToken ct = default)
        {
            var user = await GetCurrentUserAsync(ct);
            return await _cache.GetOrAddAsync($"album_{albumId}_{user.Country}",
                () => ExecuteAsync(c => c.Albums.Get(albumId, new AlbumRequest { Market = user.Country }, ct), ct),
                TimeSpan.FromHours(24));
        }

        public async Task<FullArtist> GetArtistAsync(string artistId, CancellationToken ct = default)
        {
            return await _cache.GetOrAddAsync($"artist_{artistId}",
                () => ExecuteAsync(c => c.Artists.Get(artistId, ct), ct),
                TimeSpan.FromHours(24));
        }

        public Task<FullPlaylist> GetPlaylistAsync(string playlistId, CancellationToken ct = default)
            => _cache.GetOrAddAsync($"playlist_{playlistId}", () => ExecuteAsync(c => c.Playlists.Get(playlistId, ct), ct), LibraryTtl);

        public Task<Paging<SimpleTrack>> GetAlbumTracksAsync(string albumId, CancellationToken ct = default)
            => _cache.GetOrAddAsync($"album_tracks_{albumId}", () => ExecuteAsync(c => c.Albums.GetTracks(albumId, ct), ct), MetadataTtl);

        public Task<Paging<SimpleAlbum>> GetArtistAlbumsAsync(string artistId, CancellationToken ct = default)
            => _cache.GetOrAddAsync($"artist_albums_{artistId}", () => ExecuteAsync(c => c.Artists.GetAlbums(artistId, ct), ct), MetadataTtl);

        public Task<Paging<PlaylistTrack<IPlayableItem>>> GetPlaylistItemsAsync(string playlistId, CancellationToken ct = default)
            => _cache.GetOrAddAsync($"playlist_items_{playlistId}", () => ExecuteAsync(c => c.Playlists.GetItems(playlistId, ct), ct), LibraryTtl);

        public Task<SearchResponse> SearchAsync(string query, SearchRequest.Types type, CancellationToken ct = default)
            => _cache.GetOrAddAsync($"search_{type}_{query}", () => ExecuteAsync(c => c.Search.Item(new SearchRequest(type, query), ct), ct), PersonalDataTtl);

        public Task<PrivateUser> GetCurrentUserAsync(CancellationToken ct = default)
            => _cache.GetOrAddAsync("me_profile", () => ExecuteAsync(c => c.UserProfile.Current(ct), ct), MetadataTtl);

        public Task<Paging<FullTrack>> GetUserTopTracksAsync(int limit = 20, CancellationToken ct = default)
            => _cache.GetOrAddAsync($"top_tracks_{limit}", () => ExecuteAsync(c => c.Personalization.GetTopTracks(new PersonalizationTopRequest { Limit = limit }, ct), ct), PersonalDataTtl);

        public Task<Paging<FullArtist>> GetUserTopArtistsAsync(int limit = 20, CancellationToken ct = default)
            => _cache.GetOrAddAsync($"top_artists_{limit}", () => ExecuteAsync(c => c.Personalization.GetTopArtists(new PersonalizationTopRequest { Limit = limit }, ct), ct), PersonalDataTtl);

        public Task<CursorPaging<PlayHistoryItem>> GetRecentlyPlayedAsync(int limit = 20, CancellationToken ct = default)
            => _cache.GetOrAddAsync($"recently_played_{limit}", () => ExecuteAsync(c => c.Player.GetRecentlyPlayed(new PlayerRecentlyPlayedRequest { Limit = limit }, ct), ct), PersonalDataTtl);

        public Task<Paging<SavedTrack>> GetSavedTracksAsync(CancellationToken ct = default)
            => _cache.GetOrAddAsync("saved_tracks", () => ExecuteAsync(c => c.Library.GetTracks(ct), ct), LibraryTtl);

        public Task<Paging<SavedAlbum>> GetSavedAlbumsAsync(CancellationToken ct = default)
            => _cache.GetOrAddAsync("saved_albums", () => ExecuteAsync(c => c.Library.GetAlbums(ct), ct), LibraryTtl);

        public Task<Paging<FullPlaylist>> GetCurrentUserPlaylistsAsync(CancellationToken ct = default)
            => _cache.GetOrAddAsync("me_playlists", () => ExecuteAsync(c => c.Playlists.CurrentUsers(ct), ct), LibraryTtl);

        public Task<FollowedArtistsResponse> GetFollowedArtistsAsync(CancellationToken ct = default)
            => _cache.GetOrAddAsync("followed_artists", () => ExecuteAsync(c => c.Follow.OfCurrentUser(new FollowOfCurrentUserRequest(), ct), ct), LibraryTtl);
    }
}