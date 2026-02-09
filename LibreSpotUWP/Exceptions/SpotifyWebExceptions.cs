using System;

namespace LibreSpotUWP.Exceptions
{
    public sealed class SpotifyWebException : Exception
    {
        public SpotifyWebException(Exception inner)
            : base("Spotify Web API error.", inner) { }
    }

    public sealed class SpotifyRateLimitedException : Exception
    {
        public SpotifyRateLimitedException(Exception inner)
            : base("Spotify rate limit hit.", inner) { }
    }

    public sealed class SpotifyUnauthorizedException : Exception
    {
        public SpotifyUnauthorizedException(Exception inner)
            : base("Spotify authentication failed.", inner) { }
    }
}