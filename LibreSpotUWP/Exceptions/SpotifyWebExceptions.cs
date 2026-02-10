using System;
using SpotifyAPI.Web;

namespace LibreSpotUWP.Exceptions
{
    public class SpotifyWebException : Exception
    {
        public int? StatusCode { get; }

        public SpotifyWebException(string message) : base(message) { }

        public SpotifyWebException(Exception inner)
            : base("Spotify Web API error.", inner)
        {
            if (inner is APIException apiEx)
                StatusCode = (int?)apiEx.Response?.StatusCode;
        }

        public SpotifyWebException(string message, Exception inner) : base(message, inner)
        {
            if (inner is APIException apiEx)
                StatusCode = (int?)apiEx.Response?.StatusCode;
        }
    }

    public sealed class SpotifyRateLimitedException : SpotifyWebException
    {
        public TimeSpan? RetryAfter { get; }

        public SpotifyRateLimitedException(APITooManyRequestsException inner)
            : base("Spotify rate limit hit. Slow down.", inner)
        {
            RetryAfter = inner.RetryAfter;
        }

        public SpotifyRateLimitedException(Exception inner) : base(inner) { }
    }

    public sealed class SpotifyUnauthorizedException : SpotifyWebException
    {
        public bool IsForbidden { get; }

        public SpotifyUnauthorizedException(APIUnauthorizedException inner)
            : base("Spotify authentication failed or token expired.", inner) { }

        public SpotifyUnauthorizedException(Exception inner) : base(inner)
        {
            if (inner is APIException apiEx && apiEx.Response?.StatusCode == System.Net.HttpStatusCode.Forbidden)
                IsForbidden = true;
        }
    }
}