using LibreSpotUWP.Models;
using LibreSpotUWP.Services;
using LibreSpotUWP.Views;
using LibreSpotUWP.Views.Win10_1507;
using System;

namespace LibreSpotUWP.Helpers
{
    public static class NavigationHelper
    {
        public static Type GetPageType(string pageKey)
        {
            var mode = AppearanceService.Current;

            if (pageKey == "Shell")
            {
                if (mode == AppearanceMode.Win11) return typeof(LibreSpotUWP.Views.Win11.ShellPage);
                return typeof(MainPage);
            }

            if (pageKey == "Home")
            {
                if (mode == AppearanceMode.Win11) return typeof(LibreSpotUWP.Views.Win11.HomePage_Win11);
                if (mode == AppearanceMode.Win10_1709) return typeof(LibreSpotUWP.Views.Win10_1709.HomePage_Win10_1709);
                return typeof(HomePage_Win10_1507);
            }

            if (pageKey == "Settings")
            {
                if (mode == AppearanceMode.Win11) return typeof(LibreSpotUWP.Views.Win11.SettingsPage_Win11);
                if (mode == AppearanceMode.Win10_1709) return typeof(LibreSpotUWP.Views.Win10_1709.SettingsPage_Win10_1709);
                return typeof(SettingsPage_Win10_1507);
            }

            if (pageKey == "Player")
            {
                if (mode == AppearanceMode.Win11) return typeof(LibreSpotUWP.Views.Win11.PlayerPage_Win11);
                if (mode == AppearanceMode.Win10_1709) return typeof(LibreSpotUWP.Views.Win10_1709.PlayerPage_Win10_1709);
                return typeof(PlayerPage_Win10_1507);
            }

            if (pageKey == "Lyrics")
            {
                if (mode == AppearanceMode.Win11) return typeof(LibreSpotUWP.Views.Win11.LyricsPage_Win11);
                if (mode == AppearanceMode.Win10_1709) return typeof(LibreSpotUWP.Views.Win10_1709.LyricsPage_Win10_1709);
                return typeof(LyricsPage_Win10_1507);
            }

            if (pageKey == "LikedSongs")
            {
                if (mode == AppearanceMode.Win11) return typeof(LibreSpotUWP.Views.Win11.LikedSongsPage);
                if (mode == AppearanceMode.Win10_1709) return typeof(LibreSpotUWP.Views.Win10_1709.LikedSongsPage);
                return typeof(LibreSpotUWP.Views.LikedSongsPage);
            }

            if (pageKey.StartsWith("Album:", StringComparison.OrdinalIgnoreCase))
            {
                if (mode == AppearanceMode.Win11) return typeof(LibreSpotUWP.Views.Win11.AlbumPage);
                if (mode == AppearanceMode.Win10_1709) return typeof(LibreSpotUWP.Views.Win10_1709.AlbumPage);
                return typeof(LibreSpotUWP.Views.AlbumPage);
            }

            if (pageKey.StartsWith("Artist:", StringComparison.OrdinalIgnoreCase))
            {
                if (mode == AppearanceMode.Win11) return typeof(LibreSpotUWP.Views.Win11.ArtistPage);
                if (mode == AppearanceMode.Win10_1709) return typeof(LibreSpotUWP.Views.Win10_1709.ArtistPage);
                return typeof(LibreSpotUWP.Views.ArtistPage);
            }

            if (pageKey.StartsWith("Playlist:", StringComparison.OrdinalIgnoreCase))
            {
                if (mode == AppearanceMode.Win11) return typeof(LibreSpotUWP.Views.Win11.PlaylistPage);
                if (mode == AppearanceMode.Win10_1709) return typeof(LibreSpotUWP.Views.Win10_1709.PlaylistPage);
                return typeof(LibreSpotUWP.Views.PlaylistPage);
            }

            if (pageKey.StartsWith("User:", StringComparison.OrdinalIgnoreCase))
            {
                if (mode == AppearanceMode.Win11) return typeof(LibreSpotUWP.Views.Win11.UserProfilePage);
                if (mode == AppearanceMode.Win10_1709) return typeof(LibreSpotUWP.Views.Win10_1709.UserProfilePage);
                return typeof(LibreSpotUWP.Views.UserProfilePage);
            }

            if (pageKey.StartsWith("Search:", StringComparison.OrdinalIgnoreCase))
            {
                if (mode == AppearanceMode.Win11) return typeof(LibreSpotUWP.Views.Win11.SearchPage);
                if (mode == AppearanceMode.Win10_1709) return typeof(LibreSpotUWP.Views.Win10_1709.SearchPage);
                return typeof(LibreSpotUWP.Views.SearchPage);
            }

            throw new ArgumentException($"Unknown page key: {pageKey}");
        }
    }
}
