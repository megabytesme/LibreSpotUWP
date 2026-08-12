using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using LibreSpotUWP.Constants;
using LibreSpotUWP.Exceptions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using SpotifyAPI.Web;

namespace LibreSpotUWP.Services
{
    public sealed class SpotifyPlaybackAuthService : ISpotifyPlaybackAuthService
    {
        private const string StorageKey = "spotify_playback_auth_state";
        private const int RequiredAuthVersion = 2;
        private const string SpotifyMeEndpoint = "https://api.spotify.com/v1/me";
        private readonly ISecureStorage _storage;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private static readonly HttpClient TokenHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        private Task _loadTask;
        private string _browserCodeVerifier;
        private string _browserState;

        public SpotifyPlaybackAuthService(ISecureStorage storage)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        }

        public PlaybackAuthState Current { get; private set; }
        public event EventHandler<PlaybackAuthState> PlaybackAuthStateChanged;

        public Task InitializeAsync()
        {
            return _loadTask ?? (_loadTask = LoadAsync());
        }

        public Task<Uri> BeginBrowserAuthorizationAsync()
        {
            var codes = PKCEUtil.GenerateCodes();
            _browserCodeVerifier = codes.Item1;
            _browserState = Guid.NewGuid().ToString("N");

            var uri = new Uri(
                "https://accounts.spotify.com/authorize" +
                "?response_type=code" +
                "&client_id=" + Uri.EscapeDataString(SpotifyConfig.PlaybackClientId) +
                "&scope=" + Uri.EscapeDataString("streaming user-read-private") +
                "&code_challenge_method=S256" +
                "&code_challenge=" + Uri.EscapeDataString(codes.Item2) +
                "&redirect_uri=" + Uri.EscapeDataString(SpotifyConfig.PlaybackRedirectUri) +
                "&state=" + Uri.EscapeDataString(_browserState) +
                "&show_dialog=true");
            return Task.FromResult(uri);
        }

        public async Task CompleteBrowserAuthorizationAsync(string callbackUri, string accountId)
        {
            if (string.IsNullOrWhiteSpace(_browserCodeVerifier) || string.IsNullOrWhiteSpace(_browserState))
                throw new InvalidOperationException("Start playback authorization before pasting the callback address.");

            if (!Uri.TryCreate(callbackUri?.Trim(), UriKind.Absolute, out var callback) ||
                !string.Equals(callback.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(callback.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                callback.Port != 5588 ||
                !string.Equals(callback.AbsolutePath.TrimEnd('/'), "/login", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Paste the full 127.0.0.1:5588/login address from Spotify.");
            }

            var query = HttpUtility.ParseQueryString(callback.Query);
            var error = query["error"];
            if (!string.IsNullOrWhiteSpace(error))
                throw new InvalidOperationException("Spotify did not authorize playback: " + error);

            if (!string.Equals(query["state"], _browserState, StringComparison.Ordinal))
                throw new InvalidOperationException("The Spotify callback does not belong to this authorization attempt.");

            var code = query["code"];
            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("The Spotify callback did not contain an authorization code.");

            var verifier = _browserCodeVerifier;
            _browserCodeVerifier = null;
            _browserState = null;

            using (var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = SpotifyConfig.PlaybackRedirectUri,
                ["client_id"] = SpotifyConfig.PlaybackClientId,
                ["code_verifier"] = verifier
            }))
            using (var response = await TokenHttpClient
                .PostAsync("https://accounts.spotify.com/api/token", content)
                .ConfigureAwait(false))
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("Spotify refused the playback authorization. Please start again.");

                var token = JObject.Parse(body);
                var accessToken = (string)token["access_token"];
                if (string.IsNullOrWhiteSpace(accessToken))
                    throw new InvalidOperationException("Spotify did not return a playback access token.");

                var expiresIn = (int?)token["expires_in"] ?? 3600;
                await ImportAsync(new PlaybackAuthorizationPackage
                {
                    AuthVersion = RequiredAuthVersion,
                    Kind = "bootstrapToken",
                    AccessToken = accessToken,
                    ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn)
                }, accountId).ConfigureAwait(false);
            }
        }

        public async Task ValidateImportAsync(PlaybackAuthorizationPackage package, string accountId)
        {
            await ValidatePackageForAccountAsync(package, accountId).ConfigureAwait(false);
        }

        public async Task<PlaybackConnectionMaterial> GetConnectionMaterialAsync()
        {
            await InitializeAsync().ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Current == null || Current.Status == PlaybackAuthorizationStatus.Rejected)
                    return null;

                if (!string.IsNullOrWhiteSpace(Current.StoredCredentials))
                {
                    return new PlaybackConnectionMaterial
                    {
                        StoredCredentials = Current.StoredCredentials
                    };
                }

                if (!string.IsNullOrWhiteSpace(Current.BootstrapAccessToken) &&
                    Current.BootstrapExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
                {
                    return new PlaybackConnectionMaterial
                    {
                        BootstrapAccessToken = Current.BootstrapAccessToken
                    };
                }

                if (Current.Status == PlaybackAuthorizationStatus.BootstrapPending)
                {
                    Current.BootstrapAccessToken = null;
                    Current.BootstrapExpiresAt = null;
                    Current.Status = PlaybackAuthorizationStatus.Missing;
                    await SaveCoreAsync().ConfigureAwait(false);
                }

                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task ImportAsync(PlaybackAuthorizationPackage package, string accountId)
        {
            await ValidatePackageForAccountAsync(package, accountId).ConfigureAwait(false);

            var hasStoredCredentials = !string.IsNullOrWhiteSpace(package.StoredCredentials);
            var hasBootstrapToken = !string.IsNullOrWhiteSpace(package.AccessToken) &&
                package.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1);

            await InitializeAsync().ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                Current = new PlaybackAuthState
                {
                    AuthVersion = RequiredAuthVersion,
                    AccountId = accountId,
                    StoredCredentials = hasStoredCredentials ? package.StoredCredentials : null,
                    BootstrapAccessToken = hasBootstrapToken ? package.AccessToken : null,
                    BootstrapExpiresAt = hasBootstrapToken ? package.ExpiresAt : null,
                    AuthorizedAt = hasStoredCredentials ? DateTimeOffset.UtcNow : (DateTimeOffset?)null,
                    Status = hasStoredCredentials
                        ? PlaybackAuthorizationStatus.Ready
                        : PlaybackAuthorizationStatus.BootstrapPending
                };
                await SaveCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            RaiseChanged(Current);
        }

        public async Task SaveReusableCredentialsAsync(string credentialsJson, string sessionUser)
        {
            var credentialUser = ValidateStoredCredentials(credentialsJson);
            if (!string.IsNullOrWhiteSpace(sessionUser) &&
                !string.Equals(sessionUser, credentialUser, StringComparison.OrdinalIgnoreCase))
            {
                await MarkRejectedAsync().ConfigureAwait(false);
                throw new InvalidOperationException("Spotify returned inconsistent playback account identities.");
            }
            await InitializeAsync().ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var state = Current ?? new PlaybackAuthState { AuthVersion = RequiredAuthVersion };
                if (string.IsNullOrWhiteSpace(state.AccountId))
                    throw new InvalidOperationException("The playback authorization is not linked to a Spotify account.");
                if (!string.Equals(state.AccountId, credentialUser, StringComparison.OrdinalIgnoreCase))
                {
                    state.StoredCredentials = null;
                    state.BootstrapAccessToken = null;
                    state.BootstrapExpiresAt = null;
                    state.Status = PlaybackAuthorizationStatus.Rejected;
                    Current = state;
                    await SaveCoreAsync().ConfigureAwait(false);
                    throw new InvalidOperationException("The playback authorization belongs to a different Spotify account.");
                }

                state.StoredCredentials = credentialsJson;
                state.SessionUser = credentialUser;
                state.BootstrapAccessToken = null;
                state.BootstrapExpiresAt = null;
                state.AuthorizedAt = DateTimeOffset.UtcNow;
                state.Status = PlaybackAuthorizationStatus.Ready;
                Current = state;
                await SaveCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            RaiseChanged(Current);
        }

        public async Task MarkRejectedAsync()
        {
            await InitializeAsync().ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                Current = Current ?? new PlaybackAuthState { AuthVersion = RequiredAuthVersion };
                Current.StoredCredentials = null;
                Current.BootstrapAccessToken = null;
                Current.BootstrapExpiresAt = null;
                Current.Status = PlaybackAuthorizationStatus.Rejected;
                await SaveCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            RaiseChanged(Current);
        }

        public async Task ResetAsync()
        {
            await InitializeAsync().ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                Current = null;
                await _storage.DeleteAsync(StorageKey).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            RaiseChanged(null);
        }

        public async Task<PlaybackAuthorizationPackage> ExportAsync()
        {
            await InitializeAsync().ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Current == null || Current.Status != PlaybackAuthorizationStatus.Ready ||
                    string.IsNullOrWhiteSpace(Current.StoredCredentials))
                {
                    return null;
                }

                return new PlaybackAuthorizationPackage
                {
                    AuthVersion = RequiredAuthVersion,
                    Kind = "storedCredentials",
                    StoredCredentials = Current.StoredCredentials
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task LoadAsync()
        {
            var json = await _storage.LoadAsync(StorageKey).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
                return;

            try
            {
                var state = JsonConvert.DeserializeObject<PlaybackAuthState>(json);
                if (state == null || state.AuthVersion < RequiredAuthVersion)
                    throw new InvalidOperationException("Unsupported playback authorization state.");
                if (state.Status == PlaybackAuthorizationStatus.Rejected)
                {
                    state.StoredCredentials = null;
                    state.BootstrapAccessToken = null;
                    state.BootstrapExpiresAt = null;
                }
                else if (!string.IsNullOrWhiteSpace(state.StoredCredentials))
                {
                    ValidateStoredCredentials(state.StoredCredentials);
                    state.BootstrapAccessToken = null;
                    state.BootstrapExpiresAt = null;
                    state.Status = PlaybackAuthorizationStatus.Ready;
                }
                else if (!string.IsNullOrWhiteSpace(state.BootstrapAccessToken) &&
                    state.BootstrapExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
                {
                    state.Status = PlaybackAuthorizationStatus.BootstrapPending;
                }
                else
                {
                    state.BootstrapAccessToken = null;
                    state.BootstrapExpiresAt = null;
                    state.Status = PlaybackAuthorizationStatus.Missing;
                }
                Current = state;
            }
            catch (Exception ex)
            {
                LogService.Warn($"[SpotifyPlaybackAuthService.LoadAsync] Clearing invalid playback authorization: {ex.Message}");
                Current = null;
                await _storage.DeleteAsync(StorageKey).ConfigureAwait(false);
            }
        }

        private async Task SaveCoreAsync()
        {
            if (Current == null)
            {
                await _storage.DeleteAsync(StorageKey).ConfigureAwait(false);
                return;
            }

            await _storage.SaveAsync(StorageKey, JsonConvert.SerializeObject(Current)).ConfigureAwait(false);
        }

        private static string ValidateStoredCredentials(string credentialsJson)
        {
            var json = JObject.Parse(credentialsJson);
            var username = (string)json["username"];
            if (string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrWhiteSpace((string)json["auth_data"]) ||
                json["auth_type"] == null)
            {
                throw new ArgumentException("The reusable playback credential is incomplete.");
            }

            return username;
        }

        private static void ValidatePackage(PlaybackAuthorizationPackage package)
        {
            if (package == null || package.AuthVersion < RequiredAuthVersion)
                throw new ArgumentException("The sign-in package does not contain supported playback authorization.");

            var hasStoredCredentials = !string.IsNullOrWhiteSpace(package.StoredCredentials);
            var hasBootstrapToken = !string.IsNullOrWhiteSpace(package.AccessToken) &&
                package.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1);
            if (!hasStoredCredentials && !hasBootstrapToken)
                throw new ArgumentException("The playback authorization is missing or expired.");

            if (hasStoredCredentials)
                ValidateStoredCredentials(package.StoredCredentials);
        }

        private static async Task ValidatePackageForAccountAsync(
            PlaybackAuthorizationPackage package,
            string accountId)
        {
            ValidatePackage(package);
            if (string.IsNullOrWhiteSpace(accountId))
                throw new ArgumentException(
                    "The sign-in package did not identify its Spotify account.",
                    nameof(accountId));

            if (!string.IsNullOrWhiteSpace(package.StoredCredentials))
            {
                var credentialUser = ValidateStoredCredentials(package.StoredCredentials);
                if (!string.Equals(accountId, credentialUser, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The reusable playback authorization belongs to a different Spotify account.");
                }
                return;
            }

            await ValidateBootstrapAccountAsync(package.AccessToken, accountId).ConfigureAwait(false);
        }

        private static async Task ValidateBootstrapAccountAsync(
            string accessToken,
            string expectedAccountId)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, SpotifyMeEndpoint))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using (var response = await TokenHttpClient.SendAsync(request).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            "Spotify could not verify the playback account. Start playback authorization again and approve the requested account access.");
                    }

                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var profile = JObject.Parse(body);
                    var accountId = (string)profile["id"];
                    var product = (string)profile["product"];

                    if (!string.Equals(product, "premium", StringComparison.OrdinalIgnoreCase))
                        throw new SpotifyPremiumRequiredException(product);
                    if (string.IsNullOrWhiteSpace(accountId))
                        throw new InvalidOperationException("Spotify did not return a playback account identifier.");
                    if (!string.Equals(expectedAccountId, accountId, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "Playback was authorized with a different Spotify account. Start again and select the same account used for your library.");
                    }
                }
            }
        }

        private void RaiseChanged(PlaybackAuthState state)
        {
            try
            {
                PlaybackAuthStateChanged?.Invoke(this, state);
            }
            catch (Exception ex)
            {
                LogService.Error(ex, "[SpotifyPlaybackAuthService.RaiseChanged] Handler failed");
            }
        }
    }
}
