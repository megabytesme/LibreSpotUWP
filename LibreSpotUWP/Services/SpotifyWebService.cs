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

        private SpotifyClient _client;

        public SpotifyWebService(
            ISpotifyAuthService auth,
            IMetadataCache cache)
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

        public Task<FullTrack> GetTrackAsync(string trackId, CancellationToken ct = default(CancellationToken))
        {
            return _cache.GetOrAddAsync(
                "track:" + trackId,
                delegate { return ExecuteAsync(c => c.Tracks.Get(trackId), ct); },
                TimeSpan.FromHours(12));
        }

        public Task<FullAlbum> GetAlbumAsync(string albumId, CancellationToken ct = default(CancellationToken))
        {
            return _cache.GetOrAddAsync(
                "album:" + albumId,
                delegate { return ExecuteAsync(c => c.Albums.Get(albumId), ct); },
                TimeSpan.FromHours(24));
        }

        public Task<FullPlaylist> GetPlaylistAsync(string playlistId, CancellationToken ct = default(CancellationToken))
        {
            return _cache.GetOrAddAsync(
                "playlist:" + playlistId,
                delegate { return ExecuteAsync(c => c.Playlists.Get(playlistId), ct); },
                TimeSpan.FromHours(6));
        }

        public Task<SearchResponse> SearchAsync(
            string query,
            SearchRequest.Types type,
            CancellationToken ct = default(CancellationToken))
        {
            return _cache.GetOrAddAsync(
                "search:" + query + ":" + type,
                delegate
                {
                    return ExecuteAsync(
                        c => c.Search.Item(new SearchRequest(type, query)),
                        ct);
                },
                TimeSpan.FromMinutes(10));
        }

        private readonly SemaphoreSlim _gate = new SemaphoreSlim(4);

        private async Task<T> ExecuteAsync<T>(
            Func<SpotifyClient, Task<T>> action,
            CancellationToken ct)
        {
            if (_client == null)
                throw new InvalidOperationException("SpotifyWebService has no authenticated client.");

            await _gate.WaitAsync(ct);
            try
            {
                return await action(_client);
            }
            catch (APIException apiEx)
                when (apiEx.Response != null &&
                      (int)apiEx.Response.StatusCode == 429)
            {
                throw new SpotifyRateLimitedException(apiEx);
            }

            catch (APIException apiEx)
                when (apiEx.Response != null &&
                      (int)apiEx.Response.StatusCode == 401)
            {
                throw new SpotifyUnauthorizedException(apiEx);
            }
            catch (Exception ex)
            {
                throw new SpotifyWebException(ex);
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}