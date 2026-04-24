using System;
using System.Numerics;

namespace LibreSpotUWP.Helpers
{
    public static class SpotifyIdHelper
    {
        private const string Base62Digits = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

        public static string TrackUriToId(string trackUri)
        {
            if (string.IsNullOrWhiteSpace(trackUri) || !trackUri.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase))
                return null;

            return trackUri.Substring("spotify:track:".Length);
        }

        public static string TrackUriToHexId(string trackUri)
        {
            var base62 = TrackUriToId(trackUri);
            return Base62ToHex(base62);
        }

        public static string Base62ToHex(string base62)
        {
            if (string.IsNullOrWhiteSpace(base62) || base62.Length != 22)
                return null;

            BigInteger value = BigInteger.Zero;
            foreach (char c in base62)
            {
                int digit = Base62Digits.IndexOf(c);
                if (digit < 0)
                    return null;

                value = (value * 62) + digit;
            }

            var hex = value.ToString("x");
            return hex.PadLeft(32, '0');
        }
    }
}
