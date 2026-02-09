using LibreSpotUWP.Constants;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using SpotifyAPI.Web;
using System;
using System.Threading.Tasks;

namespace LibreSpotUWP.Services
{
    public class SpotifyAuthService : ISpotifyAuthService
    {
        private readonly ISecureStorage _storage;
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
                    Scopes.Streaming
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

            await SaveStateAsync();
            await App.Librespot.ConnectWithAccessTokenAsync(Current.AccessToken);
            AuthStateChanged?.Invoke(this, Current);
        }

        private bool _isRefreshing = false;

        public async Task RefreshAsync()
        {
            if (_isRefreshing)
                return;

            if (Current == null || string.IsNullOrEmpty(Current.RefreshToken))
                return;

            if (Current.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
                return;

            _isRefreshing = true;

            try
            {
                var refresh = new PKCETokenRefreshRequest(
                    SpotifyConfig.ClientId,
                    Current.RefreshToken);

                var oauth = new OAuthClient();
                var response = await oauth.RequestToken(refresh);

                Current.AccessToken = response.AccessToken;
                Current.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn);

                await SaveStateAsync();
                await App.Librespot.ConnectWithAccessTokenAsync(Current.AccessToken);
                AuthStateChanged?.Invoke(this, Current);
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        public async Task<string> GetAccessToken()
        {
            if (Current != null &&
                !Current.IsExpired &&
                !string.IsNullOrEmpty(Current.AccessToken))
            {
                return Current.AccessToken;
            }

            var json = await _storage.LoadAsync(StorageKey);
            if (string.IsNullOrEmpty(json))
                return null;

            var loaded = Newtonsoft.Json.JsonConvert.DeserializeObject<AuthState>(json);
            if (loaded == null ||
                loaded.IsExpired ||
                string.IsNullOrEmpty(loaded.AccessToken))
            {
                return null;
            }

            Current = loaded;

            return loaded.AccessToken;
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
                    _ = App.AuthToken = Current.AccessToken;
                }
            }
            catch
            {
                await _storage.DeleteAsync(StorageKey);
            }
        }
    }
}