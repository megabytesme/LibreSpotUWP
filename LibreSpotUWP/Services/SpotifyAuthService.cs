using LibreSpotUWP.Constants;
using LibreSpotUWP.Exceptions;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Helpers;
using LibreSpotUWP.Models;
using Newtonsoft.Json.Linq;
using SpotifyAPI.Web;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.UI.Core;

namespace LibreSpotUWP.Services
{
    public class SpotifyAuthService : ISpotifyAuthService
    {
        private readonly ISecureStorage _storage;
        private readonly SemaphoreSlim _authGate = new SemaphoreSlim(1, 1);
        private string _codeVerifier;
        private string _pendingClientId;

        private const string StorageKey = "spotify_auth_state";
        private const int RequiredScopeVersion = 4;
        private const int RequiredAuthVersion = 1;
        private const string SpotifyMeEndpoint = "https://api.spotify.com/v1/me";
        private static readonly TimeSpan OfflinePersistenceLeaseDuration = TimeSpan.FromDays(30);
        private static readonly HttpClient AccountHttpClient = new HttpClient();

        public AuthState Current { get; private set; }
        public event EventHandler<AuthState> AuthStateChanged;

        public SpotifyAuthService(ISecureStorage storage)
        {
            _storage = storage;
            _ = LoadStateAsync();
        }

        public async Task BeginPkceLoginAsync()
        {
            var clientId = UserSettings.SpotifyCustomClientId;
            if (string.IsNullOrWhiteSpace(clientId))
                return;

            var (verifier, challenge) = PKCEUtil.GenerateCodes();
            _codeVerifier = verifier;
            _pendingClientId = clientId;

            var redirect = new Uri(SpotifyConfig.AppRedirectUri);

            var login = new LoginRequest(
                redirect,
                clientId,
                LoginRequest.ResponseType.Code)
            {
                CodeChallenge = challenge,
                CodeChallengeMethod = "S256",
                Scope = new[]
                {
                    Scopes.UserReadEmail,
                    Scopes.UserReadPrivate,
                    Scopes.PlaylistReadPrivate,
                    Scopes.PlaylistReadCollaborative,
                    Scopes.Streaming,
                    Scopes.UserReadRecentlyPlayed,
                    Scopes.UserTopRead,
                    Scopes.UserLibraryRead,
                    Scopes.UserLibraryModify,
                    Scopes.PlaylistModifyPrivate,
                    Scopes.PlaylistModifyPublic,
                    Scopes.UserReadPlaybackState,
                    Scopes.UserModifyPlaybackState,
                    Scopes.UserReadCurrentlyPlaying,
                    Scopes.UserFollowRead
                }
            };

            await Windows.System.Launcher.LaunchUriAsync(login.ToUri());
        }

        public async Task ExchangePkceCodeAsync(string code)
        {
            if (string.IsNullOrEmpty(_codeVerifier))
                return;

            LogService.Info("[SpotifyAuthService.ExchangePkceCodeAsync] Exchanging PKCE code for token.");
            var clientId = string.IsNullOrWhiteSpace(_pendingClientId)
                ? UserSettings.SpotifyCustomClientId
                : _pendingClientId;
            if (string.IsNullOrWhiteSpace(clientId))
                return;

            var redirect = new Uri(SpotifyConfig.AppRedirectUri);

            var request = new PKCETokenRequest(
                clientId,
                code,
                redirect,
                _codeVerifier);

            var oauth = new OAuthClient();
            var response = await oauth.RequestToken(request);
            LogService.Info("[SpotifyAuthService.ExchangePkceCodeAsync] Token exchange completed.");

            await EnsurePremiumAccountAsync(response.AccessToken).ConfigureAwait(false);

            Current = new AuthState
            {
                AccessToken = response.AccessToken,
                RefreshToken = response.RefreshToken,
                ClientId = clientId,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn),
                LastTokenRefreshAt = DateTimeOffset.UtcNow,
                RefreshTokenExpiresAt = TryGetRefreshTokenExpiresAt(response),
                ScopeVersion = RequiredScopeVersion,
                AuthVersion = RequiredAuthVersion
            };

            await PersistStateAndNotifyAsync(Current, reconnectLibrespot: true);
            LogService.Info("[SpotifyAuthService.ExchangePkceCodeAsync] PKCE auth state persisted.");
            _codeVerifier = null;
            _pendingClientId = null;
        }

        public async Task RefreshAsync()
        {
            await _authGate.WaitAsync().ConfigureAwait(false);

            try
            {
                if (Current == null || string.IsNullOrEmpty(Current.RefreshToken))
                    return;

                if (!HasRequiredAuthSchema(Current))
                {
                    await ResetAuthStateAsync().ConfigureAwait(false);
                    return;
                }

                if (!NeedsRefresh(Current))
                    return;

                var refresh = new PKCETokenRefreshRequest(
                    ResolveStateClientId(Current),
                    Current.RefreshToken);

                var oauth = new OAuthClient();

                PKCETokenResponse response;

                try
                {
                    response = await oauth.RequestToken(refresh);
                }
                catch (APIException apiEx) when (apiEx.Message.Contains("invalid_grant"))
                {
                    await ResetAuthStateAsync();
                    return;
                }

                try
                {
                    await EnsurePremiumAccountAsync(response.AccessToken).ConfigureAwait(false);
                }
                catch (SpotifyPremiumRequiredException)
                {
                    await ResetAuthStateAsync().ConfigureAwait(false);
                    throw;
                }

                Current.AccessToken = response.AccessToken;
                if (!string.IsNullOrEmpty(response.RefreshToken))
                    Current.RefreshToken = response.RefreshToken;
                Current.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn);
                Current.LastTokenRefreshAt = DateTimeOffset.UtcNow;
                Current.RefreshTokenExpiresAt = TryGetRefreshTokenExpiresAt(response);
                Current.ScopeVersion = RequiredScopeVersion;
                Current.AuthVersion = RequiredAuthVersion;

                await RenewOfflinePersistenceLeaseAsync().ConfigureAwait(false);
                await PersistStateAndNotifyAsync(Current, reconnectLibrespot: true).ConfigureAwait(false);
            }
            finally
            {
                _authGate.Release();
            }
        }

        public async Task ResetAuthStateAsync()
        {
            await ClearStoredAuthStateAsync().ConfigureAwait(false);
            RaiseAuthStateChanged(null);
        }

        public async Task EnsureCurrentAccountIsPremiumAsync()
        {
            await _authGate.WaitAsync().ConfigureAwait(false);

            try
            {
                var state = await GetOrLoadCurrentStateCoreAsync().ConfigureAwait(false);
                if (state == null || string.IsNullOrEmpty(state.AccessToken))
                    return;

                try
                {
                    await EnsurePremiumAccountAsync(state.AccessToken).ConfigureAwait(false);
                }
                catch (SpotifyPremiumRequiredException)
                {
                    await ResetAuthStateAsync().ConfigureAwait(false);
                    throw;
                }
            }
            finally
            {
                _authGate.Release();
            }
        }

        public async Task<string> GetAccessToken()
        {
            var state = await GetOrLoadCurrentStateAsync().ConfigureAwait(false);
            return state != null && !NeedsRefresh(state)
                ? state.AccessToken
                : null;
        }

        public async Task<string> EnsureValidAccessTokenAsync(bool interactive = false)
        {
            await _authGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var state = await GetOrLoadCurrentStateCoreAsync().ConfigureAwait(false);
                if (state == null)
                {
                    if (interactive)
                        await BeginPkceLoginAsync();

                    return null;
                }

                if (!NeedsRefresh(state))
                    return state.AccessToken;

                if (string.IsNullOrEmpty(state.RefreshToken))
                {
                    if (interactive)
                        await BeginPkceLoginAsync();

                    return null;
                }
            }
            finally
            {
                _authGate.Release();
            }

            await RefreshAsync().ConfigureAwait(false);

            var refreshed = await GetOrLoadCurrentStateAsync().ConfigureAwait(false);
            if (refreshed != null && NeedsRefresh(refreshed))
                refreshed = null;

            if (refreshed == null && interactive)
                await BeginPkceLoginAsync();

            return refreshed?.AccessToken;
        }

        private async Task SaveStateAsync()
        {
            if (Current != null)
                StampCurrentAuthSchema(Current);

            var json = await Task.Run(() =>
            {
                UiResponsivenessTelemetry.VerifyBackgroundThread("auth JSON serialization");
                return Newtonsoft.Json.JsonConvert.SerializeObject(Current);
            }).ConfigureAwait(false);
            await _storage.SaveAsync(StorageKey, json);
        }

        private async Task LoadStateAsync()
        {
            var json = await _storage.LoadAsync(StorageKey);
            if (string.IsNullOrEmpty(json))
                return;

            try
            {
                Current = await Task.Run(() =>
                {
                    UiResponsivenessTelemetry.VerifyBackgroundThread("auth JSON parsing");
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<AuthState>(json);
                }).ConfigureAwait(true);

                if (Current == null ||
                    string.IsNullOrEmpty(Current.AccessToken) ||
                    !HasRequiredAuthSchema(Current))
                {
                    await ClearStoredAuthStateAsync().ConfigureAwait(false);
                    return;
                }

                if (Current != null && !string.IsNullOrEmpty(Current.AccessToken))
                {
                    App.AuthToken = Current.AccessToken;
                }
            }
            catch
            {
                await ClearStoredAuthStateAsync().ConfigureAwait(false);
            }
        }

        public async Task ImportAuthStateAsync(AuthState state)
        {
            if (state == null || string.IsNullOrEmpty(state.AccessToken) || !HasRequiredAuthSchema(state))
                throw new ArgumentException("Invalid AuthState imported.");

            await EnsurePremiumAccountAsync(state.AccessToken).ConfigureAwait(false);

            Current = state;
            Current.ClientId = ResolveStateClientId(Current);
            StampCurrentAuthSchema(Current);
            if (!Current.LastTokenRefreshAt.HasValue)
                Current.LastTokenRefreshAt = DateTimeOffset.UtcNow;

            await PersistStateAndNotifyAsync(Current, reconnectLibrespot: true);
        }

        private static bool NeedsRefresh(AuthState state)
        {
            return state == null ||
                string.IsNullOrEmpty(state.AccessToken) ||
                state.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1);
        }

        private static bool HasRequiredAuthSchema(AuthState state)
        {
            return state != null &&
                state.AuthVersion >= RequiredAuthVersion &&
                state.ScopeVersion >= RequiredScopeVersion &&
                !string.IsNullOrWhiteSpace(state.ClientId);
        }

        private static void StampCurrentAuthSchema(AuthState state)
        {
            if (state == null)
                return;

            state.AuthVersion = RequiredAuthVersion;
            state.ScopeVersion = RequiredScopeVersion;
            state.ClientId = ResolveStateClientId(state);
        }

        private static string ResolveStateClientId(AuthState state)
        {
            return string.IsNullOrWhiteSpace(state?.ClientId)
                ? SpotifyConfig.DefaultClientId
                : state.ClientId;
        }

        private static DateTimeOffset? TryGetRefreshTokenExpiresAt(object response)
        {
            if (response == null)
                return null;

            var property = response.GetType().GetRuntimeProperty("RefreshTokenExpiresIn");
            if (property == null)
                return null;

            var value = property.GetValue(response);
            if (value == null)
                return null;

            if (value is int secondsInt && secondsInt > 0)
                return DateTimeOffset.UtcNow.AddSeconds(secondsInt);

            if (value is long secondsLong && secondsLong > 0)
                return DateTimeOffset.UtcNow.AddSeconds(secondsLong);

            if (value is double secondsDouble && secondsDouble > 0)
                return DateTimeOffset.UtcNow.AddSeconds(secondsDouble);

            return null;
        }

        private static async Task EnsurePremiumAccountAsync(string accessToken)
        {
            if (string.IsNullOrWhiteSpace(accessToken) || !ConnectivityHelper.HasInternetAccess())
                return;

            using (var request = new HttpRequestMessage(HttpMethod.Get, SpotifyMeEndpoint))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                using (var response = await AccountHttpClient.SendAsync(request).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();

                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var product = (string)JObject.Parse(json)["product"];

                    if (!string.Equals(product, "premium", StringComparison.OrdinalIgnoreCase))
                    {
                        LogService.Warn($"[SpotifyAuthService.EnsurePremiumAccountAsync] Rejected Spotify account product={product ?? "(null)"}.");
                        throw new SpotifyPremiumRequiredException(product);
                    }
                }
            }
        }

        private static async Task RenewOfflinePersistenceLeaseAsync()
        {
            if (App.OfflineCatalog == null)
                return;

            try
            {
                await App.OfflineCatalog
                    .RenewPersistedTrackLeasesAsync(DateTimeOffset.UtcNow.Add(OfflinePersistenceLeaseDuration))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogService.Error(ex, "[SpotifyAuthService.RenewOfflinePersistenceLeaseAsync] Failed to renew offline persistence lease.");
            }
        }

        private async Task<AuthState> GetOrLoadCurrentStateAsync()
        {
            await _authGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await GetOrLoadCurrentStateCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _authGate.Release();
            }
        }

        private async Task<AuthState> GetOrLoadCurrentStateCoreAsync()
        {
            if (Current != null && !HasRequiredAuthSchema(Current))
            {
                await ClearStoredAuthStateAsync().ConfigureAwait(false);
                return null;
            }

            if (Current != null && !string.IsNullOrEmpty(Current.AccessToken))
                return Current;

            var json = await _storage.LoadAsync(StorageKey).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json))
                return null;

            try
            {
                var loaded = await Task.Run(() =>
                {
                    UiResponsivenessTelemetry.VerifyBackgroundThread("auth state JSON parsing");
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<AuthState>(json);
                }).ConfigureAwait(false);
                if (loaded == null || string.IsNullOrEmpty(loaded.AccessToken))
                {
                    await ClearStoredAuthStateAsync().ConfigureAwait(false);
                    return null;
                }

                if (!HasRequiredAuthSchema(loaded))
                {
                    await ClearStoredAuthStateAsync().ConfigureAwait(false);
                    return null;
                }

                Current = loaded;
                App.AuthToken = loaded.AccessToken;
                return loaded;
            }
            catch
            {
                await ClearStoredAuthStateAsync().ConfigureAwait(false);
                return null;
            }
        }

        private async Task ClearStoredAuthStateAsync()
        {
            Current = null;
            App.AuthToken = null;
            await _storage.DeleteAsync(StorageKey).ConfigureAwait(false);
        }

        private async Task PersistStateAndNotifyAsync(AuthState state, bool reconnectLibrespot)
        {
            Current = state;
            App.AuthToken = state?.AccessToken;

            if (state == null)
            {
                RaiseAuthStateChanged(null);
                return;
            }

            LogService.Info("[SpotifyAuthService.PersistStateAndNotifyAsync] Saving auth state.");
            await SaveStateAsync().ConfigureAwait(false);
            LogService.Info("[SpotifyAuthService.PersistStateAndNotifyAsync] Auth state saved.");

            if (reconnectLibrespot && !string.IsNullOrEmpty(state.AccessToken))
            {
                LogService.Info("[SpotifyAuthService.PersistStateAndNotifyAsync] Auth token updated; media service will connect librespot if needed.");
            }

            RaiseAuthStateChanged(state);
            LogService.Info("[SpotifyAuthService.PersistStateAndNotifyAsync] Auth state notification sent.");
        }

        private void RaiseAuthStateChanged(AuthState state)
        {
            try
            {
                var dispatcher = CoreApplication.MainView?.CoreWindow?.Dispatcher;
                if (dispatcher != null && !dispatcher.HasThreadAccess)
                {
                    var ignored = dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        try
                        {
                            AuthStateChanged?.Invoke(this, state);
                        }
                        catch (Exception ex)
                        {
                            LogService.Error(ex, "[SpotifyAuthService.RaiseAuthStateChanged] Handler failed");
                        }
                    });
                    return;
                }

                AuthStateChanged?.Invoke(this, state);
            }
            catch (Exception ex)
            {
                LogService.Error(ex, "[SpotifyAuthService.RaiseAuthStateChanged] Dispatch failed");
            }
        }
    }
}
