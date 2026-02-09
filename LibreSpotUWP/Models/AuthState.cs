using System;

namespace LibreSpotUWP.Models
{
    public sealed class AuthState
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    }
}