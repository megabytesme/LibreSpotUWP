using LibreSpotUWP.Models;
using LibreSpotUWP.Services;
using LibreSpotUWP.Views.Win10_1507;
using System;

namespace LibreSpotUWP.Helpers
{
    public static class NavigationHelper
    {
        public static Type GetPageType(string pageKey)
        {
            var mode = AppearanceService.Current;

            //if (pageKey == "OOBE") // todo - add OOBE
            //{
                //if (mode == AppearanceMode.Win11) return typeof(MainPage);
                //if (mode == AppearanceMode.Win10_1709) return typeof(MainPage);
                //return typeof(MainPage);
            //}

            if (pageKey == "Shell")
            {
                //if (mode == AppearanceMode.Win11) return typeof(ShellPage);
                if (mode == AppearanceMode.Win10_1709) return typeof(MainPage);
                return typeof(MainPage);
            }

            if (pageKey == "Home")
            {
                //if (mode == AppearanceMode.Win11) return typeof(HomePage_Win11);
                //if (mode == AppearanceMode.Win10_1709) return typeof(HomePage_Win10_1709);
                return typeof(HomePage_Win10_1507);
            }

            if (pageKey == "Settings") // todo - add settings
            {
                //if (mode == AppearanceMode.Win11) return typeof(SettingsPage_Win11);
                //if (mode == AppearanceMode.Win10_1709) return typeof(SettingsPage_Win10_1709);
                return typeof(SettingsPage_Win10_1507);
            }

            if (pageKey == "Player")
            {
                //if (mode == AppearanceMode.Win11) return typeof(PlayerPage_Win11);
                //if (mode == AppearanceMode.Win10_1709) return typeof(PlayerPage_Win10_1709);
                return typeof(PlayerPage_Win10_1507);
            }

            throw new ArgumentException($"Unknown page key: {pageKey}");
        }
    }
}