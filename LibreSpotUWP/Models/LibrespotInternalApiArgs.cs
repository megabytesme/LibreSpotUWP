using Newtonsoft.Json;
using System.Collections.Generic;

namespace LibreSpotUWP.Models
{
    public static class LibrespotInternalApiArgs
    {
        public static string TrackUri(string trackIdOrUri) => NormalizeSpotifyUri("track", trackIdOrUri);

        public static string EpisodeUri(string episodeIdOrUri) => NormalizeSpotifyUri("episode", episodeIdOrUri);

        public static string ShowUri(string showIdOrUri) => NormalizeSpotifyUri("show", showIdOrUri);

        public static string PlaylistUri(string playlistIdOrUri) => NormalizeSpotifyUri("playlist", playlistIdOrUri);

        public static string ArtistUri(string artistIdOrUri) => NormalizeSpotifyUri("artist", artistIdOrUri);

        public static string AlbumUri(string albumIdOrUri) => NormalizeSpotifyUri("album", albumIdOrUri);

        public static string LyricsForImage(string trackIdOrUri, string imageIdHex)
        {
            return JsonConvert.SerializeObject(new
            {
                trackUri = TrackUri(trackIdOrUri),
                imageIdHex
            });
        }

        public static string ApolloStation(
            string scope,
            string contextUri,
            int? count = null,
            IEnumerable<string> previousTrackIdsOrUris = null,
            bool autoplay = false)
        {
            var previous = new List<string>();
            if (previousTrackIdsOrUris != null)
            {
                foreach (var track in previousTrackIdsOrUris)
                    previous.Add(TrackUri(track));
            }

            return JsonConvert.SerializeObject(new
            {
                scope,
                contextUri,
                count,
                previousTrackUris = previous,
                autoplay
            });
        }

        public static string AutoplayContext(
            string contextUri,
            IEnumerable<string> recentTrackIdsOrUris = null,
            bool? isVideo = null)
        {
            var recent = new List<string>();
            if (recentTrackIdsOrUris != null)
            {
                foreach (var track in recentTrackIdsOrUris)
                    recent.Add(TrackUri(track));
            }

            return JsonConvert.SerializeObject(new
            {
                contextUri,
                recentTrackUris = recent,
                isVideo
            });
        }

        public static string Rootlist(int from = 0, int? length = null)
        {
            return JsonConvert.SerializeObject(new
            {
                from,
                length
            });
        }

        private static string NormalizeSpotifyUri(string entityType, string idOrUri)
        {
            if (string.IsNullOrWhiteSpace(idOrUri))
                return string.Empty;

            return idOrUri.StartsWith("spotify:")
                ? idOrUri
                : $"spotify:{entityType}:{idOrUri}";
        }
    }
}
