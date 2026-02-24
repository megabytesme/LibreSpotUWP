using LibreSpotUWP.Exceptions;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
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

        private static readonly TimeSpan TtlImmutable = TimeSpan.MaxValue;
        private static readonly TimeSpan TtlArtist = TimeSpan.FromDays(7);
        private static readonly TimeSpan TtlSession = TimeSpan.Zero;

        private string _userId;
        private string _userCountry;

        public SpotifyWebService(ISpotifyAuthService auth, IMetadataCache cache)
        {
            _auth = auth;
            _cache = cache;

            _auth.AuthStateChanged += (_, state) =>
            {
                if (state != null && !state.IsExpired)
                    _client = new SpotifyClient(state.AccessToken);
                else
                    _client = null;
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
                var method = action.Method.Name;
                System.Diagnostics.Debug.WriteLine(
                    $"Spotify API Error in {method}: {apiEx.Response?.StatusCode} - {apiEx.Message}");

                throw new SpotifyWebException(
                    $"Spotify API Error: {apiEx.Response?.StatusCode}", apiEx);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task EnsureUserContextAsync(CancellationToken ct)
        {
            if (_userId != null && _userCountry != null)
                return;

            var me = await ExecuteAsync(c => c.UserProfile.Current(ct), ct);

            _userId = me.Id;
            _userCountry = me.Country;

            await _cache.GetOrAddAsync(
                $"users/{_userId}/profile",
                () => Task.FromResult(me),
                TtlSession,
                false);
        }

        public async Task<CacheResponse<FullTrack>> GetTrackAsync(
            string trackId,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            await EnsureUserContextAsync(ct);

            var key = $"global/tracks/{trackId}_{_userCountry}";
            return await _cache.GetOrAddAsync(
                key,
                () => ExecuteAsync(c =>
                    c.Tracks.Get(trackId, new TrackRequest { Market = _userCountry }, ct),
                    ct),
                TtlImmutable,
                forceRefresh);
        }

        public async Task<CacheResponse<FullAlbum>> GetAlbumAsync(
            string albumId,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            await EnsureUserContextAsync(ct);

            var key = $"global/albums/{albumId}_{_userCountry}";
            return await _cache.GetOrAddAsync(
                key,
                () => ExecuteAsync(c =>
                    c.Albums.Get(albumId, new AlbumRequest { Market = _userCountry }, ct),
                    ct),
                TtlImmutable,
                forceRefresh);
        }

        public Task<CacheResponse<Paging<SimpleTrack>>> GetAlbumTracksAsync(
            string albumId,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            var key = $"global/album_tracks/{albumId}";
            return _cache.GetOrAddAsync(
                key,
                () => ExecuteAsync(c => c.Albums.GetTracks(albumId, ct), ct),
                TtlImmutable,
                forceRefresh);
        }

        public Task<CacheResponse<Paging<SimpleAlbum>>> GetArtistAlbumsAsync(
            string artistId,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            var key = $"global/artist_albums/{artistId}";
            return _cache.GetOrAddAsync(
                key,
                () => ExecuteAsync(c => c.Artists.GetAlbums(artistId, ct), ct),
                TtlImmutable,
                forceRefresh);
        }

        public Task<CacheResponse<FullArtist>> GetArtistAsync(
            string artistId,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            var key = $"global/artists/{artistId}";
            return _cache.GetOrAddAsync(
                key,
                () => ExecuteAsync(c => c.Artists.Get(artistId, ct), ct),
                TtlArtist,
                forceRefresh);
        }

        public async Task<CacheResponse<PrivateUser>> GetCurrentUserAsync(
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            await EnsureUserContextAsync(ct);

            var key = $"users/{_userId}/profile";
            return await _cache.GetOrAddAsync(
                key,
                () => ExecuteAsync(c => c.UserProfile.Current(ct), ct),
                TtlSession,
                forceRefresh);
        }

        public async Task<CacheResponse<Paging<FullTrack>>> GetUserTopTracksAsync(
            int limit = 20,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            await EnsureUserContextAsync(ct);

            var key = $"users/{_userId}/top_tracks_{limit}";
            return await _cache.GetOrAddAsync(
                key,
                () => ExecuteAsync(c =>
                    c.Personalization.GetTopTracks(new PersonalizationTopRequest { Limit = limit }, ct),
                    ct),
                TtlSession,
                forceRefresh);
        }

        public async Task<CacheResponse<Paging<FullArtist>>> GetUserTopArtistsAsync(
            int limit = 20,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            await EnsureUserContextAsync(ct);

            var key = $"users/{_userId}/top_artists_{limit}";
            return await _cache.GetOrAddAsync(
                key,
                () => ExecuteAsync(c =>
                    c.Personalization.GetTopArtists(new PersonalizationTopRequest { Limit = limit }, ct),
                    ct),
                TtlSession,
                forceRefresh);
        }

        public async Task<CacheResponse<CursorPaging<PlayHistoryItem>>> GetRecentlyPlayedAsync(
            int limit = 20,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            await EnsureUserContextAsync(ct);

            var key = $"users/{_userId}/recently_played_{limit}";
            return await _cache.GetOrAddAsync(
                key,
                () => ExecuteAsync(c =>
                    c.Player.GetRecentlyPlayed(new PlayerRecentlyPlayedRequest { Limit = limit }, ct),
                    ct),
                TtlSession,
                forceRefresh);
        }

        public async Task<CacheResponse<Paging<FullPlaylist>>> GetCurrentUserPlaylistsAsync(
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            await EnsureUserContextAsync(ct);

            var key = $"users/{_userId}/playlists";
            return await _cache.GetOrAddAsync(
                key,
                () => ExecuteAsync(c => c.Playlists.CurrentUsers(ct), ct),
                TtlSession,
                forceRefresh);
        }

        public async Task<CacheResponse<Paging<SavedTrack>>> GetSavedTracksAsync(
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            await EnsureUserContextAsync(ct);

            var key = $"users/{_userId}/saved_tracks";
            return await _cache.GetOrAddAsync(
                key,
                () => ExecuteAsync(c => c.Library.GetTracks(ct), ct),
                TtlSession,
                forceRefresh);
        }

        public async Task<CacheResponse<Paging<SavedAlbum>>> GetSavedAlbumsAsync(
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            await EnsureUserContextAsync(ct);

            var key = $"users/{_userId}/saved_albums";
            return await _cache.GetOrAddAsync(
                key,
                () => ExecuteAsync(c => c.Library.GetAlbums(ct), ct),
                TtlSession,
                forceRefresh);
        }

        public async Task<CacheResponse<FollowedArtistsResponse>> GetFollowedArtistsAsync(
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            await EnsureUserContextAsync(ct);

            var key = $"users/{_userId}/followed_artists";
            return await _cache.GetOrAddAsync(
                key,
                () => ExecuteAsync(c => c.Follow.OfCurrentUser(new FollowOfCurrentUserRequest(), ct), ct),
                TtlSession,
                forceRefresh);
        }

        public Task<CacheResponse<FullPlaylist>> GetPlaylistAsync(
            string playlistId,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            var key = $"global/playlists/{playlistId}";
            return _cache.GetOrAddAsync(
                key,
                () => ExecuteAsync(c => c.Playlists.Get(playlistId, ct), ct),
                TtlImmutable,
                forceRefresh);
        }

        public Task<CacheResponse<Paging<PlaylistTrack<IPlayableItem>>>> GetPlaylistItemsAsync(
            string playlistId,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            var key = $"global/playlist_items/{playlistId}";
            return _cache.GetOrAddAsync(
                key,
                () => ExecuteAsync(c => c.Playlists.GetItems(playlistId, ct), ct),
                TtlImmutable,
                forceRefresh);
        }

        public Task<CacheResponse<Paging<T>>> GetNextPageAsync<T>(
            Paging<T> currentPaging,
            CancellationToken ct = default)
        {
            if (currentPaging?.Next == null) return null;

            var key = $"global/paging_next/{currentPaging.Next.GetHashCode()}";

            return _cache.GetOrAddAsync(
                key,
                () => ExecuteAsync(c => c.NextPage(currentPaging), ct),
                TtlImmutable,
                false);
        }

        public Task<CacheResponse<SearchResponse>> SearchAsync(
            string query,
            SearchRequest.Types type,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            var key = $"global/search/{type}_{query}";
            return _cache.GetOrAddAsync(
                key,
                () => ExecuteAsync(c => c.Search.Item(new SearchRequest(type, query), ct), ct),
                TtlSession,
                forceRefresh);
        }
    }
}