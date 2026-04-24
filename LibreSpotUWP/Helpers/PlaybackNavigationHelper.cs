using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LibreSpotUWP.Helpers
{
    public static class PlaybackNavigationHelper
    {
        public static MainPage FindMainPage(DependencyObject start)
        {
            DependencyObject current = start;
            while (current != null)
            {
                if (current is MainPage mainPage)
                    return mainPage;

                current = Windows.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
            }

            return (Window.Current.Content as Frame)?.Content as MainPage;
        }

        public static void NavigateToSpotifyUri(DependencyObject start, string spotifyUri)
        {
            if (string.IsNullOrWhiteSpace(spotifyUri))
                return;

            var mainPage = FindMainPage(start);
            if (mainPage == null)
                return;

            if (spotifyUri.StartsWith("spotify:artist:", StringComparison.OrdinalIgnoreCase))
            {
                mainPage.NavigateToArtist(spotifyUri.Substring("spotify:artist:".Length));
                return;
            }

            if (spotifyUri.StartsWith("spotify:album:", StringComparison.OrdinalIgnoreCase))
            {
                mainPage.NavigateToAlbum(spotifyUri.Substring("spotify:album:".Length));
                return;
            }

            if (spotifyUri.StartsWith("spotify:playlist:", StringComparison.OrdinalIgnoreCase))
            {
                mainPage.NavigateToPlaylist(spotifyUri.Substring("spotify:playlist:".Length));
                return;
            }

            if (spotifyUri.StartsWith("spotify:user:", StringComparison.OrdinalIgnoreCase))
            {
                var userId = spotifyUri.Substring("spotify:user:".Length);
                mainPage.NavigateTo("User:" + userId);
            }
        }
    }
}
