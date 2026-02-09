using LibreSpotUWP.Helpers;
using LibreSpotUWP.Models;
using System;
using Windows.Storage;

namespace LibreSpotUWP.Services
{
    public static class AppearanceService
    {
        private const string Key = "AppearanceMode";

        public static AppearanceMode Current
        {
            get
            {
                var settings = ApplicationData.Current.LocalSettings;

                if (settings.Values.TryGetValue(Key, out object value) &&
                    value is int i &&
                    Enum.IsDefined(typeof(AppearanceMode), i))
                {
                    return (AppearanceMode)i;
                }

                if (OSHelper.IsWindows11)
                    return AppearanceMode.Win11;

                if (OSHelper.IsWindows10_1709OrGreater)
                    return AppearanceMode.Win10_1709;

                return AppearanceMode.Win10_1507;
            }
        }

        public static void Set(AppearanceMode mode)
        {
            ApplicationData.Current.LocalSettings.Values[Key] = (int)mode;
        }
    }
}