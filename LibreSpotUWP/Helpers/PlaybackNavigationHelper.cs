using System;
using LibreSpotUWP.Interfaces;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LibreSpotUWP.Helpers
{
    public static class PlaybackNavigationHelper
    {
        public static IAppShell FindShell(DependencyObject start)
        {
            DependencyObject current = start;
            while (current != null)
            {
                if (current is IAppShell shell)
                    return shell;

                current = Windows.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
            }

            return (Window.Current.Content as Frame)?.Content as IAppShell;
        }

        public static void NavigateToSpotifyUri(DependencyObject start, string spotifyUri)
        {
            if (string.IsNullOrWhiteSpace(spotifyUri))
                return;

            var shell = FindShell(start);
            if (shell == null)
                return;

            if (spotifyUri.StartsWith("spotify:artist:", StringComparison.OrdinalIgnoreCase))
            {
                shell.NavigateToArtist(spotifyUri.Substring("spotify:artist:".Length));
                return;
            }

            if (spotifyUri.StartsWith("spotify:album:", StringComparison.OrdinalIgnoreCase))
            {
                shell.NavigateToAlbum(spotifyUri.Substring("spotify:album:".Length));
                return;
            }

            if (spotifyUri.StartsWith("spotify:playlist:", StringComparison.OrdinalIgnoreCase))
            {
                shell.NavigateToPlaylist(spotifyUri.Substring("spotify:playlist:".Length));
                return;
            }

            if (spotifyUri.StartsWith("spotify:user:", StringComparison.OrdinalIgnoreCase))
            {
                var userId = spotifyUri.Substring("spotify:user:".Length);
                shell.NavigateTo("User:" + userId);
            }
        }
    }
}
