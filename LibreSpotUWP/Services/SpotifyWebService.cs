using LibreSpotUWP.Exceptions;
using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using SpotifyAPI.Web;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LibreSpotUWP.Services
{
    public sealed class SpotifyWebService : ISpotifyWebService
    {
        private readonly ISpotifyAuthService _auth;
        private readonly IMetadataCache _cache;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(4);
        private readonly SemaphoreSlim _clientUpdateGate = new SemaphoreSlim(1, 1);
        private SpotifyClient _client;

        private static readonly TimeSpan TtlImmutable = TimeSpan.MaxValue;
        private static readonly TimeSpan TtlArtist = TimeSpan.FromDays(7);
        private static readonly TimeSpan TtlSession = TimeSpan.Zero;
        private const string UserContextCacheKey = "session/user_context";

        private string _userId;
        private string _userCountry;

        public SpotifyWebService(ISpotifyAuthService auth, IMetadataCache cache)
        {
            _auth = auth;
            _cache = cache;

            _auth.AuthStateChanged += OnAuthStateChanged;

            if (_auth.Current != null && !_auth.Current.IsExpired)
                _client = new SpotifyClient(_auth.Current.AccessToken);
        }

        private async Task<T> ExecuteAsync<T>(Func<SpotifyClient, Task<T>> action, CancellationToken ct)
        {
            await EnsureClientReadyAsync(ct).ConfigureAwait(false);

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await action(_client).ConfigureAwait(false);
            }
            catch (APIUnauthorizedException)
            {
                await RecoverFromUnauthorizedAsync(ct).ConfigureAwait(false);
                return await action(_client).ConfigureAwait(false);
            }
            catch (APIException apiEx)
            {
                var method = action.Method.Name;
                System.Diagnostics.Debug.WriteLine(
                    $"Spotify API Error in {method}: {apiEx.Response?.StatusCode} - {apiEx.Message}");

                throw new SpotifyWebException(
                    $"Spotify API Error: {apiEx.Response?.StatusCode}", apiEx);
            }
            catch (HttpRequestException httpEx)
            {
                var method = action.Method.Name;
                System.Diagnostics.Debug.WriteLine(
                    $"Spotify HTTP Error in {method}: {httpEx.Message}");

                throw new SpotifyWebException("Spotify request failed.", httpEx);
            }
            catch (TaskCanceledException canceledEx) when (!ct.IsCancellationRequested)
            {
                var method = action.Method.Name;
                System.Diagnostics.Debug.WriteLine(
                    $"Spotify request timeout in {method}: {canceledEx.Message}");

                throw new SpotifyWebException("Spotify request timed out.", canceledEx);
            }
            finally
            {
                _gate.Release();
            }
        }

        private void OnAuthStateChanged(object sender, AuthState state)
        {
            _userId = null;
            _userCountry = null;
            _client = state != null && !state.IsExpired
                ? new SpotifyClient(state.AccessToken)
                : null;
        }

        private async Task EnsureClientReadyAsync(CancellationToken ct)
        {
            if (_client != null)
                return;

            await _clientUpdateGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_client != null)
                    return;

                var token = await _auth.EnsureValidAccessTokenAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(token))
                    throw new InvalidOperationException("Spotify client is not authenticated.");

                _client = new SpotifyClient(token);
            }
            finally
            {
                _clientUpdateGate.Release();
            }
        }

        private async Task RecoverFromUnauthorizedAsync(CancellationToken ct)
        {
            _client = null;
            _userId = null;
            _userCountry = null;

            var token = await _auth.EnsureValidAccessTokenAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(token))
                throw new SpotifyUnauthorizedException(new InvalidOperationException("Unable to refresh Spotify access token."));

            ct.ThrowIfCancellationRequested();
            _client = new SpotifyClient(token);
        }

        private async Task EnsureUserContextAsync(CancellationToken ct)
        {
            if (_userId != null && _userCountry != null)
                return;

            var cached = await _cache.TryGetAsync<PrivateUser>(UserContextCacheKey);
            if (cached?.Value != null &&
                !string.IsNullOrEmpty(cached.Value.Id) &&
                !string.IsNullOrEmpty(cached.Value.Country))
            {
                _userId = cached.Value.Id;
                _userCountry = cached.Value.Country;

                if (!ConnectivityHelper.HasInternetAccess())
                    return;
            }

            var me = await ExecuteAsync(c => c.UserProfile.Current(ct), ct);

            _userId = me.Id;
            _userCountry = me.Country;

            await _cache.GetOrAddAsync(
                $"users/{_userId}/profile",
                () => Task.FromResult(me),
                TtlSession,
                true);

            await _cache.GetOrAddAsync(
                UserContextCacheKey,
                () => Task.FromResult(me),
                TtlSession,
                true);
        }

        private async Task<CacheResponse<T>> GetCachedResponseAsync<T>(
            string key,
            Func<Task<T>> factory,
            TimeSpan ttl,
            bool forceRefresh = false)
        {
            if (!forceRefresh && !ConnectivityHelper.HasInternetAccess())
            {
                var cached = await _cache.TryGetAsync<T>(key);
                if (cached != null)
                {
                    return new CacheResponse<T>(
                        cached.Value,
                        cached.Timestamp,
                        true,
                        cached.IsStale || ttl != TimeSpan.MaxValue,
                        true);
                }
            }

            try
            {
                return await _cache.GetOrAddAsync(key, factory, ttl, forceRefresh);
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                var cached = await _cache.TryGetAsync<T>(key);
                if (cached != null)
                {
                    return new CacheResponse<T>(
                        cached.Value,
                        cached.Timestamp,
                        true,
                        cached.IsStale || ttl != TimeSpan.MaxValue,
                        true);
                }

                throw;
            }
        }

        private static bool IsRecoverable(Exception ex)
        {
            return ex is APIException ||
                ex is SpotifyWebException ||
                ex is SpotifyUnauthorizedException ||
                ex is HttpRequestException ||
                ex is TaskCanceledException ||
                ex is InvalidOperationException;
        }

        public async Task<CacheResponse<FullTrack>> GetTrackAsync(
            string trackId,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            await EnsureUserContextAsync(ct);

            var key = $"global/tracks/{trackId}_{_userCountry}";
            return await GetCachedResponseAsync(
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
            return await GetCachedResponseAsync(
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
            return GetCachedResponseAsync(
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
            return GetCachedResponseAsync(
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
            return GetCachedResponseAsync(
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
            return await GetCachedResponseAsync(
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
            return await GetCachedResponseAsync(
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
            return await GetCachedResponseAsync(
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
            return await GetCachedResponseAsync(
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
            return await GetCachedResponseAsync(
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
            return await GetCachedResponseAsync(
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
            return await GetCachedResponseAsync(
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
            return await GetCachedResponseAsync(
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
            return GetCachedResponseAsync(
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
            return GetCachedResponseAsync(
                key,
                () => ExecuteAsync(c => c.Playlists.GetItems(playlistId, ct), ct),
                TtlImmutable,
                forceRefresh);
        }

        public Task<CacheResponse<Paging<T>>> GetNextPageAsync<T>(
            Paging<T> currentPaging,
            CancellationToken ct = new CancellationToken())
        {
            if (currentPaging?.Next == null)
                return null;

            var key = $"global/paging_next/{currentPaging.Next.GetHashCode()}";

            return GetCachedResponseAsync(
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
            return GetCachedResponseAsync(
                key,
                () => ExecuteAsync(c => c.Search.Item(new SearchRequest(type, query), ct), ct),
                TtlSession,
                forceRefresh);
        }
    }
}
