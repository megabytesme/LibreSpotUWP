using LibreSpotUWP.Exceptions;
using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace LibreSpotUWP.Services
{
    public sealed class SpotifyWebService : ISpotifyWebService
    {
        private readonly ISpotifyAuthService _auth;
        private readonly IMetadataCache _cache;
        private readonly ILibrespotService _librespot;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(4);
        private readonly SemaphoreSlim _clientUpdateGate = new SemaphoreSlim(1, 1);
        private readonly NetHttpClient _httpClient;
        private SpotifyClient _client;

        internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan TtlImmutable = TimeSpan.MaxValue;
        private static readonly TimeSpan TtlArtist = TimeSpan.FromDays(7);
        internal static readonly TimeSpan TtlSession = TimeSpan.FromMinutes(5);
        private const string UserContextCacheKey = "session/user_context";

        private string _userId;
        private string _userCountry;

        public SpotifyWebService(ISpotifyAuthService auth, IMetadataCache cache, ILibrespotService librespot)
        {
            _auth = auth;
            _cache = cache;
            _librespot = librespot;
            _httpClient = new NetHttpClient();
            _httpClient.SetRequestTimeout(RequestTimeout);

            _auth.AuthStateChanged += OnAuthStateChanged;

            if (_auth.Current != null && !_auth.Current.IsExpired)
                _client = CreateClient(_auth.Current.AccessToken);
        }

        private async Task<T> ExecuteAsync<T>(Func<SpotifyClient, Task<T>> action, CancellationToken ct)
        {
            if (!ConnectivityHelper.HasInternetAccess())
                throw new SpotifyWebException("Spotify request skipped while offline.");

            await EnsureClientReadyAsync(ct).ConfigureAwait(false);

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await ExecuteWithRetryAsync(action, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<T> ExecuteWithRetryAsync<T>(Func<SpotifyClient, Task<T>> action, CancellationToken ct)
        {
            const int maxAttempts = 3;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    return await action(_client).ConfigureAwait(false);
                }
                catch (APIUnauthorizedException) when (attempt < maxAttempts)
                {
                    await RecoverFromUnauthorizedAsync(ct).ConfigureAwait(false);
                }
                catch (APIException apiEx) when (IsRateLimited(apiEx) && attempt < maxAttempts)
                {
                    var wait = TimeSpan.FromSeconds(Math.Min(5, attempt * 2));
                    LogService.Warn(
                        $"Spotify API rate limited in {action.Method.Name}, retrying in {wait.TotalSeconds:0.#}s: {apiEx.Message}");
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
                catch (APIException apiEx)
                {
                    var method = action.Method.Name;
                    LogService.Warn(
                        $"Spotify API Error in {method}: {apiEx.Response?.StatusCode} - {apiEx.Message}");

                    throw new SpotifyWebException(
                        $"Spotify API Error: {apiEx.Response?.StatusCode}", apiEx);
                }
                catch (HttpRequestException httpEx)
                {
                    ReportInternetAccessFailureIfNetworkLooksOffline();

                    var method = action.Method.Name;
                    LogService.Warn(
                        $"Spotify HTTP Error in {method}: {httpEx.Message}");

                    throw new SpotifyWebException("Spotify request failed.", httpEx);
                }
                catch (TaskCanceledException canceledEx) when (!ct.IsCancellationRequested)
                {
                    ReportInternetAccessFailureIfNetworkLooksOffline();

                    var method = action.Method.Name;
                    LogService.Warn(
                        $"Spotify request timeout in {method}: {canceledEx.Message}");

                    throw new SpotifyWebException("Spotify request timed out.", canceledEx);
                }
            }

            throw new SpotifyRateLimitedException(new Exception("Spotify request was rate limited too many times."));
        }

        private void OnAuthStateChanged(object sender, AuthState state)
        {
            _userId = null;
            _userCountry = null;
            _client = state != null && !state.IsExpired
                ? CreateClient(state.AccessToken)
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

                _client = CreateClient(token);
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
            _client = CreateClient(token);
        }

        private SpotifyClient CreateClient(string accessToken)
        {
            var config = SpotifyClientConfig
                .CreateDefault(accessToken)
                .WithHTTPClient(_httpClient);
            return new SpotifyClient(config);
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

            if (!ConnectivityHelper.HasInternetAccess())
                throw new SpotifyWebException("Spotify user context is unavailable while offline.");

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
            if (!ConnectivityHelper.HasInternetAccess())
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

                throw new SpotifyWebException("Cached Spotify response is unavailable while offline.");
            }

            try
            {
                return await _cache.GetOrAddAsync(key, factory, ttl, forceRefresh);
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                if (LooksLikeConnectivityFailure(ex))
                    ReportInternetAccessFailureIfNetworkLooksOffline();

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

        private static bool LooksLikeConnectivityFailure(Exception ex)
        {
            var text = ex?.ToString() ?? string.Empty;
            return text.IndexOf("No such host", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("server name or address could not be resolved", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("Service unavailable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("Tried to acquire token without stored credentials", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ReportInternetAccessFailureIfNetworkLooksOffline()
        {
            if (!ConnectivityHelper.HasNetworkReportedInternetAccess())
                ConnectivityHelper.ReportInternetAccessFailure();
        }

        private static bool IsRateLimited(APIException apiEx)
        {
            return apiEx?.Response != null && apiEx.Response.StatusCode == (HttpStatusCode)429;
        }

        private static string ExtractSpotifyId(string idOrUri)
        {
            if (string.IsNullOrWhiteSpace(idOrUri))
                return null;

            var parts = idOrUri.Split(':');
            return parts.Length >= 3 ? parts[2] : idOrUri;
        }

        private static IEnumerable<List<string>> Batch(IEnumerable<string> values, int size)
        {
            var batch = new List<string>(size);
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                batch.Add(value);
                if (batch.Count < size)
                    continue;

                yield return batch;
                batch = new List<string>(size);
            }

            if (batch.Count > 0)
                yield return batch;
        }

        private Task InvalidatePlaylistAsync(string playlistId)
        {
            return Task.WhenAll(
                _cache.InvalidateAsync($"global/playlists/{playlistId}"),
                _cache.InvalidateAsync($"global/playlist_items/{playlistId}"),
                _cache.InvalidateAsync("users/current/playlists"));
        }

        private static FullTrack MapFullTrack(LibrespotTrackData payload)
        {
            if (payload == null)
                return null;

            return new FullTrack
            {
                Id = payload.Id,
                Uri = payload.Uri,
                Name = payload.Name,
                DurationMs = payload.DurationMs,
                DiscNumber = payload.DiscNumber,
                TrackNumber = payload.TrackNumber,
                Artists = payload.Artists?.Select(MapSimpleArtist).ToList() ?? new List<SimpleArtist>(),
                Album = MapSimpleAlbum(payload.Album)
            };
        }

        private static SimpleTrack MapSimpleTrack(LibrespotSimpleTrackData payload)
        {
            if (payload == null)
                return null;

            return new SimpleTrack
            {
                Id = payload.Id,
                Uri = payload.Uri,
                Name = payload.Name,
                DurationMs = payload.DurationMs,
                DiscNumber = payload.DiscNumber,
                TrackNumber = payload.TrackNumber,
                Artists = payload.Artists?.Select(MapSimpleArtist).ToList() ?? new List<SimpleArtist>()
            };
        }

        private static FullAlbum MapFullAlbum(LibrespotAlbumData payload)
        {
            if (payload == null)
                return null;

            return new FullAlbum
            {
                Id = payload.Id,
                Uri = payload.Uri,
                Name = payload.Name,
                AlbumType = payload.AlbumType,
                Images = EnsureImageList(payload.Images?.Select(MapImage).ToList()),
                Artists = payload.Artists?.Select(MapSimpleArtist).ToList() ?? new List<SimpleArtist>(),
                ReleaseDate = payload.ReleaseDate,
                TotalTracks = payload.TotalTracks
            };
        }

        private static SimpleAlbum MapSimpleAlbum(LibrespotAlbumSummaryData payload)
        {
            if (payload == null)
                return null;

            return new SimpleAlbum
            {
                Id = payload.Id,
                Uri = payload.Uri,
                Name = payload.Name,
                AlbumType = payload.AlbumType,
                Images = payload.Images?.Select(MapImage).ToList() ?? new List<Image>(),
                Artists = payload.Artists?.Select(MapSimpleArtist).ToList() ?? new List<SimpleArtist>()
            };
        }

        private static FullArtist MapFullArtist(LibrespotArtistData payload)
        {
            if (payload == null)
                return null;

            return new FullArtist
            {
                Id = payload.Id,
                Uri = payload.Uri,
                Name = payload.Name,
                Images = EnsureImageList(payload.Images?.Select(MapImage).ToList())
            };
        }

        private static FullPlaylist MapFullPlaylist(LibrespotPlaylistData payload)
        {
            if (payload == null)
                return null;

            return new FullPlaylist
            {
                Id = payload.Id,
                Uri = payload.Uri,
                Name = payload.Name,
                Images = EnsureImageList(payload.Images?.Select(MapImage).ToList()),
                Owner = payload.Owner == null
                    ? null
                    : new PublicUser
                    {
                        Id = payload.Owner.Id,
                        DisplayName = payload.Owner.DisplayName
                    }
            };
        }

        private static PlaylistTrack<IPlayableItem> MapPlaylistTrack(LibrespotTrackData payload)
        {
            var fullTrack = MapFullTrack(payload);
            return new PlaylistTrack<IPlayableItem>
            {
                Track = fullTrack
            };
        }

        private static SimpleArtist MapSimpleArtist(LibrespotArtistSummaryData payload)
        {
            if (payload == null)
                return null;

            return new SimpleArtist
            {
                Id = payload.Id,
                Uri = payload.Uri,
                Name = payload.Name
            };
        }

        private static Image MapImage(LibrespotImageData payload)
        {
            if (payload == null)
                return null;

            return new Image
            {
                Url = payload.Url,
                Width = payload.Width,
                Height = payload.Height
            };
        }

        private static PrivateUser MapPrivateUser(LibrespotUserProfileData payload)
        {
            if (payload == null)
                return null;

            return new PrivateUser
            {
                Id = payload.Id,
                Uri = payload.Uri,
                DisplayName = payload.DisplayName,
                Email = payload.Email,
                Country = payload.Country,
                Images = EnsureImageList(payload.Images?.Select(MapImage).ToList())
            };
        }

        private static AppImage MapAppImage(Image image)
        {
            if (image == null)
                return null;

            return new AppImage
            {
                Url = image.Url,
                Width = image.Width,
                Height = image.Height
            };
        }

        private static List<AppImage> EnsureAppImageList(List<AppImage> images)
        {
            if (images == null || images.Count == 0)
                return new List<AppImage>();

            return images.Where(image => image != null).ToList();
        }

        private static AppUserProfile MapAppUserProfile(PrivateUser user)
        {
            if (user == null)
                return null;

            return new AppUserProfile
            {
                Id = user.Id,
                Uri = user.Uri,
                DisplayName = user.DisplayName,
                Email = user.Email,
                Country = user.Country,
                Images = EnsureAppImageList(user.Images?.Select(MapAppImage).ToList())
            };
        }

        private static SavedTrack MapSavedTrack(LibrespotTrackData payload)
        {
            if (payload == null)
                return null;

            return new SavedTrack
            {
                AddedAt = DateTime.UtcNow,
                Track = MapFullTrack(payload)
            };
        }

        private static FullPlaylist MapPlaylistSummary(LibrespotPlaylistSummaryData payload)
        {
            if (payload == null)
                return null;

            return new FullPlaylist
            {
                Id = payload.Id,
                Uri = payload.Uri,
                Name = payload.Name,
                Images = EnsureImageList(payload.Images?.Select(MapImage).ToList())
            };
        }

        private static List<Image> EnsureImageList(List<Image> images)
        {
            if (images == null || images.Count == 0)
            {
                return new List<Image>
                {
                    new Image
                    {
                        Url = null,
                        Width = 0,
                        Height = 0
                    }
                };
            }

            return images;
        }

        private static SearchResponse MapSearchResponse(LibrespotSearchData payload)
        {
            payload = payload ?? new LibrespotSearchData();

            return new SearchResponse
            {
                Tracks = new Paging<FullTrack, SearchResponse>()
                {
                    Items = payload.Tracks?.Select(MapFullTrack).ToList() ?? new List<FullTrack>(),
                    Total = payload.Tracks?.Count ?? 0
                },
                Albums = new Paging<SimpleAlbum, SearchResponse>()
                {
                    Items = payload.Albums?.Select(MapSimpleAlbum).ToList() ?? new List<SimpleAlbum>(),
                    Total = payload.Albums?.Count ?? 0
                },
                Artists = new Paging<FullArtist, SearchResponse>()
                {
                    Items = payload.Artists?.Select(p => new FullArtist
                    {
                        Id = p.Id,
                        Uri = p.Uri,
                        Name = p.Name
                    }).ToList() ?? new List<FullArtist>(),
                    Total = payload.Artists?.Count ?? 0
                },
                Playlists = new Paging<FullPlaylist, SearchResponse>()
                {
                    Items = payload.Playlists?.Select(MapPlaylistSummary).ToList() ?? new List<FullPlaylist>(),
                    Total = payload.Playlists?.Count ?? 0
                }
            };
        }

        public async Task<CacheResponse<FullTrack>> GetTrackAsync(
            string trackId,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            var key = $"global/tracks/{trackId}";
            return await GetCachedResponseAsync(
                key,
                async () =>
                {
                    ct.ThrowIfCancellationRequested();
                    var payload = await _librespot.GetTrackAsync($"spotify:track:{trackId}").ConfigureAwait(false);
                    return MapFullTrack(payload);
                },
                TtlImmutable,
                forceRefresh);
        }

        public async Task<CacheResponse<FullAlbum>> GetAlbumAsync(
            string albumId,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            var key = $"global/albums/{albumId}";
            return await GetCachedResponseAsync(
                key,
                async () =>
                {
                    ct.ThrowIfCancellationRequested();
                    var payload = await _librespot.GetAlbumAsync($"spotify:album:{albumId}").ConfigureAwait(false);
                    return MapFullAlbum(payload);
                },
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
                async () =>
                {
                    ct.ThrowIfCancellationRequested();
                    var payload = await _librespot.GetAlbumAsync($"spotify:album:{albumId}").ConfigureAwait(false);

                    return new Paging<SimpleTrack>
                    {
                        Items = payload.Tracks?.Select(MapSimpleTrack).ToList() ?? new List<SimpleTrack>(),
                        Total = payload.TotalTracks
                    };
                },
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
                async () =>
                {
                    ct.ThrowIfCancellationRequested();
                    var payload = await _librespot.GetArtistAsync($"spotify:artist:{artistId}").ConfigureAwait(false);

                    var items = payload.Albums?.Select(MapSimpleAlbum).ToList() ?? new List<SimpleAlbum>();
                    return new Paging<SimpleAlbum>
                    {
                        Items = items,
                        Total = items.Count
                    };
                },
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
                async () =>
                {
                    ct.ThrowIfCancellationRequested();
                    var payload = await _librespot.GetArtistAsync($"spotify:artist:{artistId}").ConfigureAwait(false);
                    return MapFullArtist(payload) ?? new FullArtist
                    {
                        Id = artistId,
                        Uri = $"spotify:artist:{artistId}",
                        Name = string.Empty,
                        Images = EnsureImageList(null)
                    };
                },
                TtlArtist,
                forceRefresh);
        }

        public async Task<CacheResponse<PrivateUser>> GetCurrentUserAsync(
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            await EnsureUserContextAsync(ct).ConfigureAwait(false);

            var key = "users/current/profile";
            return await GetCachedResponseAsync(
                key,
                async () =>
                {
                    ct.ThrowIfCancellationRequested();
                    // Account identity belongs to the Web API session. Keep it available while
                    // playback is waiting for its separate one-time authorization.
                    var user = await ExecuteAsync(c => c.UserProfile.Current(ct), ct).ConfigureAwait(false);

                    _userId = user.Id;
                    _userCountry = user.Country;
                    var cached = await _cache.TryGetAsync<PrivateUser>($"users/{_userId}/profile").ConfigureAwait(false);
                    if ((user.Images == null || user.Images.Count == 0 || string.IsNullOrWhiteSpace(user.Images[0].Url)) &&
                        cached?.Value?.Images != null &&
                        cached.Value.Images.Count > 0)
                    {
                        user.Images = cached.Value.Images;
                    }

                    if (string.IsNullOrWhiteSpace(user.DisplayName) && cached?.Value != null)
                        user.DisplayName = cached.Value.DisplayName;

                    if (string.IsNullOrWhiteSpace(user.Email) && cached?.Value != null)
                        user.Email = cached.Value.Email;

                    if (string.IsNullOrWhiteSpace(user.Country) && cached?.Value != null)
                        user.Country = cached.Value.Country;

                    return user;
                },
                TtlSession,
                forceRefresh);
        }

        public async Task<CacheResponse<AppUserProfile>> GetCurrentUserProfileAsync(
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            var response = await GetCurrentUserAsync(forceRefresh, ct).ConfigureAwait(false);
            return new CacheResponse<AppUserProfile>(
                MapAppUserProfile(response.Value),
                response.Timestamp,
                response.IsFromCache,
                response.IsStale,
                response.IsOfflineFallback);
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

        public Task<DeviceResponse> GetAvailableDevicesAsync(CancellationToken ct = new CancellationToken())
        {
            return ExecuteAsync(c => c.Player.GetAvailableDevices(ct), ct);
        }

        public Task<CurrentlyPlayingContext> GetCurrentPlaybackAsync(CancellationToken ct = new CancellationToken())
        {
            return ExecuteAsync(c => c.Player.GetCurrentPlayback(
                new PlayerCurrentPlaybackRequest(
                    PlayerCurrentPlaybackRequest.AdditionalTypes.Track |
                    PlayerCurrentPlaybackRequest.AdditionalTypes.Episode),
                ct), ct);
        }

        public Task<bool> TransferPlaybackAsync(string deviceId, bool play, CancellationToken ct = new CancellationToken())
        {
            return ExecuteAsync(c => c.Player.TransferPlayback(
                new PlayerTransferPlaybackRequest(new[] { deviceId })
                {
                    Play = play
                },
                ct), ct);
        }

        public Task<bool> ResumePlaybackAsync(
            string deviceId,
            string contextUri = null,
            string startUri = null,
            int? positionMs = null,
            CancellationToken ct = new CancellationToken())
        {
            var request = new PlayerResumePlaybackRequest
            {
                DeviceId = deviceId,
                PositionMs = positionMs
            };

            if (!string.IsNullOrWhiteSpace(contextUri) &&
                contextUri.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase))
            {
                request.Uris = new[] { contextUri };
            }
            else if (!string.IsNullOrWhiteSpace(contextUri))
            {
                request.ContextUri = contextUri;
                if (!string.IsNullOrWhiteSpace(startUri))
                {
                    request.OffsetParam = new PlayerResumePlaybackRequest.Offset
                    {
                        Uri = startUri
                    };
                }
            }
            else if (!string.IsNullOrWhiteSpace(startUri))
            {
                request.Uris = new[] { startUri };
            }

            return ExecuteAsync(c => c.Player.ResumePlayback(request, ct), ct);
        }

        public Task<bool> PausePlaybackAsync(string deviceId, CancellationToken ct = new CancellationToken())
        {
            return ExecuteAsync(c => c.Player.PausePlayback(new PlayerPausePlaybackRequest { DeviceId = deviceId }, ct), ct);
        }

        public Task<bool> SkipNextAsync(string deviceId, CancellationToken ct = new CancellationToken())
        {
            return ExecuteAsync(c => c.Player.SkipNext(new PlayerSkipNextRequest { DeviceId = deviceId }, ct), ct);
        }

        public Task<bool> SkipPreviousAsync(string deviceId, CancellationToken ct = new CancellationToken())
        {
            return ExecuteAsync(c => c.Player.SkipPrevious(new PlayerSkipPreviousRequest { DeviceId = deviceId }, ct), ct);
        }

        public Task<bool> SeekToAsync(string deviceId, long positionMs, CancellationToken ct = new CancellationToken())
        {
            return ExecuteAsync(c => c.Player.SeekTo(new PlayerSeekToRequest(positionMs) { DeviceId = deviceId }, ct), ct);
        }

        public Task<bool> SetVolumeAsync(string deviceId, int volumePercent, CancellationToken ct = new CancellationToken())
        {
            volumePercent = Math.Max(0, Math.Min(100, volumePercent));
            return ExecuteAsync(c => c.Player.SetVolume(new PlayerVolumeRequest(volumePercent) { DeviceId = deviceId }, ct), ct);
        }

        public Task<bool> SetShuffleAsync(string deviceId, bool enabled, CancellationToken ct = new CancellationToken())
        {
            return ExecuteAsync(c => c.Player.SetShuffle(new PlayerShuffleRequest(enabled) { DeviceId = deviceId }, ct), ct);
        }

        public Task<bool> SetRepeatAsync(string deviceId, int mode, CancellationToken ct = new CancellationToken())
        {
            var state = PlayerSetRepeatRequest.State.Off;
            if (mode == 1)
                state = PlayerSetRepeatRequest.State.Context;
            else if (mode == 2)
                state = PlayerSetRepeatRequest.State.Track;

            return ExecuteAsync(c => c.Player.SetRepeat(new PlayerSetRepeatRequest(state) { DeviceId = deviceId }, ct), ct);
        }

        public Task<bool> AddToQueueAsync(string deviceId, string uri, CancellationToken ct = new CancellationToken())
        {
            return ExecuteAsync(c => c.Player.AddToQueue(new PlayerAddToQueueRequest(uri) { DeviceId = deviceId }, ct), ct);
        }

        public async Task<CacheResponse<Paging<FullPlaylist>>> GetCurrentUserPlaylistsAsync(
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            await EnsureUserContextAsync(ct).ConfigureAwait(false);

            var key = "users/current/playlists";
            return await GetCachedResponseAsync(
                key,
                async () =>
                {
                    ct.ThrowIfCancellationRequested();
                    var payload = await _librespot.GetUserPlaylistsAsync(_userId).ConfigureAwait(false);

                    var items = payload.Items?.Select(MapPlaylistSummary).ToList() ?? new List<FullPlaylist>();
                    return new Paging<FullPlaylist>
                    {
                        Items = items,
                        Total = items.Count
                    };
                },
                TtlSession,
                forceRefresh);
        }

        public async Task<CacheResponse<Paging<FullPlaylist>>> GetCurrentUserPlaylistsPageAsync(
            int limit,
            int offset,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            await EnsureUserContextAsync(ct).ConfigureAwait(false);

            limit = Math.Max(1, Math.Min(50, limit));
            offset = Math.Max(0, offset);

            var key = $"users/current/playlists_page/{limit}_{offset}";
            return await GetCachedResponseAsync(
                key,
                () => ExecuteAsync(
                    c => c.Playlists.CurrentUsers(
                        new PlaylistCurrentUsersRequest
                        {
                            Limit = limit,
                            Offset = offset
                        },
                        ct),
                    ct),
                TtlSession,
                forceRefresh);
        }

        public async Task<IReadOnlyDictionary<string, bool>> CheckTracksSavedAsync(
            IEnumerable<string> trackIds,
            CancellationToken ct = new CancellationToken())
        {
            var ids = (trackIds ?? Enumerable.Empty<string>())
                .Select(ExtractSpotifyId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var result = ids.ToDictionary(id => id, id => false, StringComparer.OrdinalIgnoreCase);
            if (result.Count == 0 || !ConnectivityHelper.HasInternetAccess())
                return result;

            foreach (var batch in Batch(ids, 50))
            {
                var checks = await ExecuteAsync(
                    c => c.Library.CheckTracks(new LibraryCheckTracksRequest(batch), ct),
                    ct).ConfigureAwait(false);

                for (int i = 0; i < batch.Count && i < checks.Count; i++)
                    result[batch[i]] = checks[i];
            }

            return result;
        }

        public async Task SetTracksSavedAsync(
            IEnumerable<string> trackIds,
            bool saved,
            CancellationToken ct = new CancellationToken())
        {
            if (!ConnectivityHelper.HasInternetAccess())
                throw new SpotifyWebException("Spotify library changes are unavailable while offline.");

            var ids = (trackIds ?? Enumerable.Empty<string>())
                .Select(ExtractSpotifyId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ids.Count == 0)
                return;

            foreach (var batch in Batch(ids, 50))
            {
                if (saved)
                {
                    await ExecuteAsync(
                        c => c.Library.SaveTracks(new LibrarySaveTracksRequest(batch), ct),
                        ct).ConfigureAwait(false);
                }
                else
                {
                    await ExecuteAsync(
                        c => c.Library.RemoveTracks(new LibraryRemoveTracksRequest(batch), ct),
                        ct).ConfigureAwait(false);
                }
            }

            await _cache.InvalidateAsync("users/current/saved_tracks").ConfigureAwait(false);
        }

        public async Task<bool> CheckAlbumSavedAsync(
            string albumId,
            CancellationToken ct = new CancellationToken())
        {
            if (!ConnectivityHelper.HasInternetAccess())
                return false;

            albumId = ExtractSpotifyId(albumId);
            if (string.IsNullOrWhiteSpace(albumId))
                return false;

            var checks = await ExecuteAsync(
                c => c.Library.CheckAlbums(new LibraryCheckAlbumsRequest(new List<string> { albumId }), ct),
                ct).ConfigureAwait(false);

            return checks.Count > 0 && checks[0];
        }

        public async Task SetAlbumSavedAsync(
            string albumId,
            bool saved,
            CancellationToken ct = new CancellationToken())
        {
            if (!ConnectivityHelper.HasInternetAccess())
                throw new SpotifyWebException("Spotify library changes are unavailable while offline.");

            await EnsureUserContextAsync(ct).ConfigureAwait(false);

            albumId = ExtractSpotifyId(albumId);
            if (string.IsNullOrWhiteSpace(albumId))
                return;

            if (saved)
            {
                await ExecuteAsync(
                    c => c.Library.SaveAlbums(new LibrarySaveAlbumsRequest(new List<string> { albumId }), ct),
                    ct).ConfigureAwait(false);
            }
            else
            {
                await ExecuteAsync(
                    c => c.Library.RemoveAlbums(new LibraryRemoveAlbumsRequest(new List<string> { albumId }), ct),
                    ct).ConfigureAwait(false);
            }

            await _cache.InvalidateAsync($"users/{_userId}/saved_albums").ConfigureAwait(false);
        }

        public async Task<bool> CheckPlaylistFollowedAsync(
            string playlistId,
            CancellationToken ct = new CancellationToken())
        {
            if (!ConnectivityHelper.HasInternetAccess())
                return false;

            await EnsureUserContextAsync(ct).ConfigureAwait(false);

            playlistId = ExtractSpotifyId(playlistId);
            if (string.IsNullOrWhiteSpace(playlistId) || string.IsNullOrWhiteSpace(_userId))
                return false;

            var checks = await ExecuteAsync(
                c => c.Follow.CheckPlaylist(
                    playlistId,
                    new FollowCheckPlaylistRequest(new List<string> { _userId }),
                    ct),
                ct).ConfigureAwait(false);

            return checks.Count > 0 && checks[0];
        }

        public async Task SetPlaylistFollowedAsync(
            string playlistId,
            bool followed,
            CancellationToken ct = new CancellationToken())
        {
            if (!ConnectivityHelper.HasInternetAccess())
                throw new SpotifyWebException("Spotify playlist changes are unavailable while offline.");

            playlistId = ExtractSpotifyId(playlistId);
            if (string.IsNullOrWhiteSpace(playlistId))
                return;

            if (followed)
                await ExecuteAsync(c => c.Follow.FollowPlaylist(playlistId, ct), ct).ConfigureAwait(false);
            else
                await ExecuteAsync(c => c.Follow.UnfollowPlaylist(playlistId, ct), ct).ConfigureAwait(false);

            await _cache.InvalidateAsync("users/current/playlists").ConfigureAwait(false);
        }

        public async Task<bool> PlaylistContainsTrackAsync(
            string playlistId,
            string trackUri,
            CancellationToken ct = new CancellationToken())
        {
            if (!ConnectivityHelper.HasInternetAccess())
                return false;

            await EnsureUserContextAsync(ct).ConfigureAwait(false);

            playlistId = ExtractSpotifyId(playlistId);
            if (string.IsNullOrWhiteSpace(playlistId) || string.IsNullOrWhiteSpace(trackUri))
                return false;

            var trackId = ExtractSpotifyId(trackUri);
            if (string.IsNullOrWhiteSpace(trackId))
                return false;

            var offset = 0;
            const int limit = 100;
            while (true)
            {
                var request = new PlaylistGetItemsRequest
                {
                    Limit = limit,
                    Offset = offset
                };
                request.Fields.Add("items(track(type,id,uri)),next,total");

                var page = await ExecuteAsync(
                    c => c.Playlists.GetItems(
                        playlistId,
                        request,
                        ct),
                    ct).ConfigureAwait(false);

                var items = page?.Items ?? new List<PlaylistTrack<IPlayableItem>>();
                if (items.Any(item =>
                    item?.Track is FullTrack track &&
                    (string.Equals(track.Uri, trackUri, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(track.Id, trackId, StringComparison.OrdinalIgnoreCase))))
                {
                    return true;
                }

                if (items.Count == 0 || string.IsNullOrWhiteSpace(page?.Next))
                    return false;

                offset += items.Count;
            }
        }

        public async Task AddTrackToPlaylistAsync(
            string playlistId,
            string trackUri,
            CancellationToken ct = new CancellationToken())
        {
            if (!ConnectivityHelper.HasInternetAccess())
                throw new SpotifyWebException("Spotify playlist changes are unavailable while offline.");

            playlistId = ExtractSpotifyId(playlistId);
            if (string.IsNullOrWhiteSpace(playlistId) || string.IsNullOrWhiteSpace(trackUri))
                return;

            await ExecuteAsync(
                c => c.Playlists.AddItems(
                    playlistId,
                    new PlaylistAddItemsRequest(new List<string> { trackUri }),
                    ct),
                ct).ConfigureAwait(false);

            await InvalidatePlaylistAsync(playlistId).ConfigureAwait(false);
        }

        public async Task RemoveTrackFromPlaylistAsync(
            string playlistId,
            string trackUri,
            CancellationToken ct = new CancellationToken())
        {
            if (!ConnectivityHelper.HasInternetAccess())
                throw new SpotifyWebException("Spotify playlist changes are unavailable while offline.");

            playlistId = ExtractSpotifyId(playlistId);
            if (string.IsNullOrWhiteSpace(playlistId) || string.IsNullOrWhiteSpace(trackUri))
                return;

            var request = new PlaylistRemoveItemsRequest
            {
                Tracks = new List<PlaylistRemoveItemsRequest.Item>
                {
                    new PlaylistRemoveItemsRequest.Item { Uri = trackUri }
                }
            };

            await ExecuteAsync(
                c => c.Playlists.RemoveItems(playlistId, request, ct),
                ct).ConfigureAwait(false);

            await InvalidatePlaylistAsync(playlistId).ConfigureAwait(false);
        }

        public async Task ReorderPlaylistItemsAsync(
            string playlistId,
            int rangeStart,
            int insertBefore,
            int rangeLength = 1,
            CancellationToken ct = new CancellationToken())
        {
            if (!ConnectivityHelper.HasInternetAccess())
                throw new SpotifyWebException("Spotify playlist changes are unavailable while offline.");

            playlistId = ExtractSpotifyId(playlistId);
            if (string.IsNullOrWhiteSpace(playlistId))
                return;

            var request = new PlaylistReorderItemsRequest(rangeStart, insertBefore)
            {
                RangeLength = Math.Max(1, rangeLength)
            };

            await ExecuteAsync(
                c => c.Playlists.ReorderItems(playlistId, request, ct),
                ct).ConfigureAwait(false);

            await InvalidatePlaylistAsync(playlistId).ConfigureAwait(false);
        }

        public async Task<CacheResponse<Paging<SavedTrack>>> GetSavedTracksAsync(
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            await EnsureUserContextAsync(ct).ConfigureAwait(false);

            var key = "users/current/saved_tracks";
            return await GetCachedResponseAsync(
                key,
                async () =>
                {
                    ct.ThrowIfCancellationRequested();
                    return await ExecuteAsync(c => c.Library.GetTracks(ct), ct).ConfigureAwait(false);
                },
                TtlSession,
                forceRefresh);
        }

        public async Task<CacheResponse<Paging<SavedTrack>>> GetSavedTracksPageAsync(
            int limit,
            int offset,
            bool forceRefresh = false,
            CancellationToken ct = new CancellationToken())
        {
            await EnsureUserContextAsync(ct).ConfigureAwait(false);

            limit = Math.Max(1, Math.Min(50, limit));
            offset = Math.Max(0, offset);

            var key = $"users/current/saved_tracks_page/{limit}_{offset}";
            return await GetCachedResponseAsync(
                key,
                () => ExecuteAsync(
                    c => c.Library.GetTracks(
                        new LibraryTracksRequest
                        {
                            Limit = limit,
                            Offset = offset
                        },
                        ct),
                    ct),
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
            await EnsureUserContextAsync(ct).ConfigureAwait(false);

            var key = "users/current/followed_artists";
            return await GetCachedResponseAsync(
                key,
                async () =>
                {
                    ct.ThrowIfCancellationRequested();
                    var payload = await _librespot.GetFollowedArtistsAsync(_userId).ConfigureAwait(false);

                    var artists = payload.Items?.Select(p => new FullArtist
                    {
                        Id = p.Id,
                        Uri = p.Uri,
                        Name = p.Name
                    }).ToList() ?? new List<FullArtist>();

                    return new FollowedArtistsResponse
                    {
                        Artists = new CursorPaging<FullArtist, FollowedArtistsResponse>()
                        {
                            Items = artists,
                            Total = artists.Count
                        }
                    };
                },
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
                async () =>
                {
                    ct.ThrowIfCancellationRequested();
                    var payload = await _librespot.GetPlaylistAsync($"spotify:playlist:{playlistId}").ConfigureAwait(false);
                    return MapFullPlaylist(payload) ?? new FullPlaylist
                    {
                        Id = playlistId,
                        Uri = $"spotify:playlist:{playlistId}",
                        Name = string.Empty,
                        Images = EnsureImageList(null)
                    };
                },
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
                async () =>
                {
                    ct.ThrowIfCancellationRequested();
                    var payload = await _librespot.GetPlaylistAsync($"spotify:playlist:{playlistId}").ConfigureAwait(false);

                    var items = payload.Tracks?.Select(MapPlaylistTrack).ToList()
                        ?? new List<PlaylistTrack<IPlayableItem>>();

                    return new Paging<PlaylistTrack<IPlayableItem>>
                    {
                        Items = items,
                        Total = items.Count
                    };
                },
                TtlImmutable,
                forceRefresh);
        }

        public Task<CacheResponse<Paging<T>>> GetNextPageAsync<T>(
            Paging<T> currentPaging,
            CancellationToken ct = new CancellationToken())
        {
            if (currentPaging == null || currentPaging.Next == null)
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
                async () =>
                {
                    ct.ThrowIfCancellationRequested();
                    await EnsureUserContextAsync(ct).ConfigureAwait(false);

                    var request = new SearchRequest(type, query)
                    {
                        Limit = 20,
                        Market = _userCountry
                    };

                    return await ExecuteAsync(c => c.Search.Item(request, ct), ct).ConfigureAwait(false);
                },
                TtlSession,
                forceRefresh);
        }

        private sealed class ImagePayload
        {
            public string Url { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
        }

        private sealed class ArtistSummaryPayload
        {
            public string Id { get; set; }
            public string Uri { get; set; }
            public string Name { get; set; }
        }

        private class AlbumSummaryPayload
        {
            public string Id { get; set; }
            public string Uri { get; set; }
            public string Name { get; set; }
            public string AlbumType { get; set; }
            public List<ImagePayload> Images { get; set; }
            public List<ArtistSummaryPayload> Artists { get; set; }
        }

        private class SimpleTrackPayload
        {
            public string Id { get; set; }
            public string Uri { get; set; }
            public string Name { get; set; }
            public int DurationMs { get; set; }
            public int DiscNumber { get; set; }
            public int TrackNumber { get; set; }
            public List<ArtistSummaryPayload> Artists { get; set; }
        }

        private sealed class TrackPayload : SimpleTrackPayload
        {
            public AlbumSummaryPayload Album { get; set; }
        }

        private sealed class AlbumPayload : AlbumSummaryPayload
        {
            public string ReleaseDate { get; set; }
            public int TotalTracks { get; set; }
            public List<SimpleTrackPayload> Tracks { get; set; }
        }

        private sealed class ArtistPayload
        {
            public string Id { get; set; }
            public string Uri { get; set; }
            public string Name { get; set; }
            public List<ImagePayload> Images { get; set; }
            public List<AlbumSummaryPayload> Albums { get; set; }
        }

        private sealed class OwnerPayload
        {
            public string Id { get; set; }
            public string DisplayName { get; set; }
        }

        private sealed class PlaylistTrackPayload
        {
            public TrackPayload Track { get; set; }
        }

        private sealed class PlaylistPayload
        {
            public string Id { get; set; }
            public string Uri { get; set; }
            public string Name { get; set; }
            public List<ImagePayload> Images { get; set; }
            public OwnerPayload Owner { get; set; }
            public List<PlaylistTrackPayload> Tracks { get; set; }
        }

        private sealed class UserProfilePayload
        {
            public string Id { get; set; }
            public string Uri { get; set; }
            public string DisplayName { get; set; }
            public string Email { get; set; }
            public string Country { get; set; }
            public List<ImagePayload> Images { get; set; }
        }

        private sealed class PlaylistSummaryPayload
        {
            public string Id { get; set; }
            public string Uri { get; set; }
            public string Name { get; set; }
            public List<ImagePayload> Images { get; set; }
        }

        private sealed class PlaylistListPayload
        {
            public List<PlaylistSummaryPayload> Items { get; set; }
        }

        private sealed class TrackListPayload
        {
            public List<TrackPayload> Items { get; set; }
        }

        private sealed class ArtistListPayload
        {
            public List<ArtistSummaryPayload> Items { get; set; }
        }

        private sealed class SearchPayload
        {
            public List<TrackPayload> Tracks { get; set; }
            public List<AlbumSummaryPayload> Albums { get; set; }
            public List<ArtistSummaryPayload> Artists { get; set; }
            public List<PlaylistSummaryPayload> Playlists { get; set; }
        }
    }
}
