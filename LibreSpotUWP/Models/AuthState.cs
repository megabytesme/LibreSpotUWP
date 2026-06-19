using System;

namespace LibreSpotUWP.Models
{
    public sealed class AuthState
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public string ClientId { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public DateTimeOffset? LastTokenRefreshAt { get; set; }
        public DateTimeOffset? RefreshTokenExpiresAt { get; set; }
        public int ScopeVersion { get; set; }
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    }
}
