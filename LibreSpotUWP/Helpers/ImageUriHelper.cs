using System;
using Windows.UI.Xaml.Media.Imaging;

namespace LibreSpotUWP.Helpers
{
    public static class ImageUriHelper
    {
        private const string FallbackLogoUri = "ms-appx:///Assets/StoreLogo.scale-400.png";
        private const string SpotifyImagePrefix = "spotify:image:";
        private const string SpotifyCdnPrefix = "https://i.scdn.co/image/";

        public static string NormalizeImageUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            value = value.Trim();

            if (value.StartsWith(SpotifyImagePrefix, StringComparison.OrdinalIgnoreCase))
                return SpotifyCdnPrefix + value.Substring(SpotifyImagePrefix.Length);

            if (LooksLikeSpotifyImageId(value))
                return SpotifyCdnPrefix + value;

            return value;
        }

        public static Uri GetFallbackLogoUri()
        {
            return new Uri(FallbackLogoUri);
        }

        public static bool TryCreateImageUri(string value, out Uri uri)
        {
            uri = null;
            var normalized = NormalizeImageUrl(value);
            return !string.IsNullOrWhiteSpace(normalized) &&
                   Uri.TryCreate(normalized, UriKind.Absolute, out uri);
        }

        public static Uri GetImageUriOrFallback(string value)
        {
            if (TryCreateImageUri(value, out var uri))
                return uri;

            return GetFallbackLogoUri();
        }

        public static BitmapImage CreateBitmapImage(string value, bool useFallback = true)
        {
            if (TryCreateImageUri(value, out var uri))
                return new BitmapImage(uri);

            return useFallback ? new BitmapImage(GetFallbackLogoUri()) : null;
        }

        private static bool LooksLikeSpotifyImageId(string value)
        {
            if (value.Length != 40)
                return false;

            foreach (var c in value)
            {
                if (!Uri.IsHexDigit(c))
                    return false;
            }

            return true;
        }
    }
}
