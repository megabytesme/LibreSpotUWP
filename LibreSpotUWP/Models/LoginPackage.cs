using System;

namespace LibreSpotUWP.Models
{
    public sealed class LoginPackage
    {
        public const string CurrentFormat = "LibreSpotUWP.Login";
        public const int CurrentVersion = 2;

        public string Format { get; set; }
        public int Version { get; set; }
        public string MinimumAppVersion { get; set; }
        public string AccountId { get; set; }
        public AuthState Web { get; set; }
        public PlaybackAuthorizationPackage Playback { get; set; }
    }

    public sealed class PlaybackAuthorizationPackage
    {
        public int AuthVersion { get; set; }
        public string Kind { get; set; }
        public string AccessToken { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public string StoredCredentials { get; set; }
    }
}
