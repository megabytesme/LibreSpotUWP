using System;

namespace LibreSpotUWP.Models
{
    public enum PlaybackAuthorizationStatus
    {
        Missing = 0,
        BootstrapPending = 1,
        Ready = 2,
        Rejected = 3
    }

    public sealed class PlaybackAuthState
    {
        public int AuthVersion { get; set; }
        public string AccountId { get; set; }
        public string BootstrapAccessToken { get; set; }
        public DateTimeOffset? BootstrapExpiresAt { get; set; }
        public string StoredCredentials { get; set; }
        public string SessionUser { get; set; }
        public DateTimeOffset? AuthorizedAt { get; set; }
        public PlaybackAuthorizationStatus Status { get; set; }
    }

    public sealed class PlaybackConnectionMaterial
    {
        public string StoredCredentials { get; set; }
        public string BootstrapAccessToken { get; set; }

        public bool IsEmpty => string.IsNullOrWhiteSpace(StoredCredentials) &&
            string.IsNullOrWhiteSpace(BootstrapAccessToken);

        public string Identity => !string.IsNullOrWhiteSpace(StoredCredentials)
            ? "stored:" + StoredCredentials
            : "bootstrap:" + BootstrapAccessToken;
    }

    public sealed class PlaybackCredentialsEventArgs : EventArgs
    {
        public string CredentialsJson { get; set; }
        public string SessionUser { get; set; }
    }
}
