using SpotifyAPI.Web;
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;

namespace LibreSpotUWP.Helpers
{
    internal static class SpotifyDjSupportHelper
    {
        private static int _dialogVisible;

        public static bool IsSpotifyDjPlaylist(FullPlaylist playlist)
        {
            return playlist != null &&
                string.Equals(playlist.Name?.Trim(), "DJ", StringComparison.OrdinalIgnoreCase);
        }

        public static async Task<bool> ShowIfUnsupportedAsync(FullPlaylist playlist)
        {
            if (!IsSpotifyDjPlaylist(playlist))
                return false;

            if (Interlocked.Exchange(ref _dialogVisible, 1) != 0)
                return true;

            try
            {
                var dialog = new ContentDialog
                {
                    Title = "Spotify DJ",
                    Content = "DJ is not supported yet.",
                    CloseButtonText = "OK"
                };
                await dialog.ShowAsync();
            }
            finally
            {
                Volatile.Write(ref _dialogVisible, 0);
            }

            return true;
        }
    }
}
