using System;

namespace LibreSpotUWP.Services
{
    public static class PlaybackAccountIdentityValidator
    {
        public static void EnsureConsistent(
            string expectedAccountId,
            string credentialUser,
            string sessionUser)
        {
            if (string.IsNullOrWhiteSpace(expectedAccountId))
                throw new InvalidOperationException("The playback authorization is not linked to a Spotify account.");
            if (string.IsNullOrWhiteSpace(credentialUser))
                throw new InvalidOperationException("Spotify did not return a playback account identity.");

            if (!string.IsNullOrWhiteSpace(sessionUser) &&
                !string.Equals(sessionUser, credentialUser, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Spotify returned inconsistent playback account identities.");
            }

            if (!string.Equals(expectedAccountId, credentialUser, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The playback authorization belongs to a different Spotify account.");
            }
        }
    }
}
