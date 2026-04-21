using LibreSpotUWP.Constants;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using SpotifyAPI.Web;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LibreSpotUWP.Services
{
    public class SpotifyAuthService : ISpotifyAuthService
    {
        private readonly ISecureStorage _storage;
        private readonly SemaphoreSlim _authGate = new SemaphoreSlim(1, 1);
        private string _codeVerifier;

        private const string StorageKey = "spotify_auth_state";

        public AuthState Current { get; private set; }
        public event EventHandler<AuthState> AuthStateChanged;

        public SpotifyAuthService(ISecureStorage storage)
        {
            _storage = storage;
            _ = LoadStateAsync();
        }

        public async Task BeginPkceLoginAsync()
        {
            var (verifier, challenge) = PKCEUtil.GenerateCodes();
            _codeVerifier = verifier;

            var redirect = new Uri("librespotuwp://callback/");

            var login = new LoginRequest(
                redirect,
                SpotifyConfig.ClientId,
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
                    Scopes.UserReadPlaybackState,
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

            var redirect = new Uri("librespotuwp://callback/");

            var request = new PKCETokenRequest(
                SpotifyConfig.ClientId,
                code,
                redirect,
                _codeVerifier);

            var oauth = new OAuthClient();
            var response = await oauth.RequestToken(request);

            Current = new AuthState
            {
                AccessToken = response.AccessToken,
                RefreshToken = response.RefreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn)
            };

            await PersistStateAndNotifyAsync(Current, reconnectLibrespot: true);
            _codeVerifier = null;
        }

        public async Task RefreshAsync()
        {
            await _authGate.WaitAsync().ConfigureAwait(false);

            try
            {
                if (Current == null || string.IsNullOrEmpty(Current.RefreshToken))
                    return;

                if (!NeedsRefresh(Current))
                    return;

                var refresh = new PKCETokenRefreshRequest(
                    SpotifyConfig.ClientId,
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

                Current.AccessToken = response.AccessToken;
                if (!string.IsNullOrEmpty(response.RefreshToken))
                    Current.RefreshToken = response.RefreshToken;
                Current.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn);

                await PersistStateAndNotifyAsync(Current, reconnectLibrespot: true).ConfigureAwait(false);
            }
            finally
            {
                _authGate.Release();
            }
        }

        public async Task ResetAuthStateAsync()
        {
            Current = null;
            await _storage.DeleteAsync(StorageKey);

            App.AuthToken = null;

            AuthStateChanged?.Invoke(this, null);
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
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(Current);
            await _storage.SaveAsync(StorageKey, json);
        }

        private async Task LoadStateAsync()
        {
            var json = await _storage.LoadAsync(StorageKey);
            if (string.IsNullOrEmpty(json))
                return;

            try
            {
                Current = Newtonsoft.Json.JsonConvert.DeserializeObject<AuthState>(json);

                if (Current != null && !string.IsNullOrEmpty(Current.AccessToken))
                {
                    App.AuthToken = Current.AccessToken;
                }
            }
            catch
            {
                await _storage.DeleteAsync(StorageKey);
            }
        }

        public async Task ImportAuthStateAsync(AuthState state)
        {
            if (state == null || string.IsNullOrEmpty(state.AccessToken))
                throw new ArgumentException("Invalid AuthState imported.");

            Current = state;

            await PersistStateAndNotifyAsync(Current, reconnectLibrespot: true);
        }

        private static bool NeedsRefresh(AuthState state)
        {
            return state == null ||
                string.IsNullOrEmpty(state.AccessToken) ||
                state.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1);
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
            if (Current != null && !string.IsNullOrEmpty(Current.AccessToken))
                return Current;

            var json = await _storage.LoadAsync(StorageKey).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json))
                return null;

            try
            {
                var loaded = Newtonsoft.Json.JsonConvert.DeserializeObject<AuthState>(json);
                if (loaded == null || string.IsNullOrEmpty(loaded.AccessToken))
                    return null;

                Current = loaded;
                App.AuthToken = loaded.AccessToken;
                return loaded;
            }
            catch
            {
                await _storage.DeleteAsync(StorageKey).ConfigureAwait(false);
                return null;
            }
        }

        private async Task PersistStateAndNotifyAsync(AuthState state, bool reconnectLibrespot)
        {
            Current = state;
            App.AuthToken = state?.AccessToken;

            if (state == null)
            {
                AuthStateChanged?.Invoke(this, null);
                return;
            }

            await SaveStateAsync().ConfigureAwait(false);

            if (reconnectLibrespot && !string.IsNullOrEmpty(state.AccessToken))
                await App.Librespot.ConnectWithAccessTokenAsync(state.AccessToken).ConfigureAwait(false);

            AuthStateChanged?.Invoke(this, state);
        }
    }
}
