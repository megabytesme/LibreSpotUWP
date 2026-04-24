using LibreSpotUWP.Models;
using Windows.Storage;

namespace LibreSpotUWP.Helpers
{
    public static class UserSettings
    {
        private const string HomeOrganizationModeKey = "HomeOrganizationMode";
        private const string AbsoluteVolumeKey = "AbsoluteVolumeControlEnabled";
        private const string LyricsAutoScrollKey = "LyricsAutoScrollEnabled";
        private const string AudioEffectsKey = "AudioEffectsPreset";

        public static HomeOrganizationMode HomeOrganizationMode
        {
            get => (HomeOrganizationMode)(ApplicationData.Current.LocalSettings.Values.TryGetValue(HomeOrganizationModeKey, out object value) ? (int)value : 0);
            set => ApplicationData.Current.LocalSettings.Values[HomeOrganizationModeKey] = (int)value;
        }

        public static bool AbsoluteVolumeControlEnabled
        {
            get => ApplicationData.Current.LocalSettings.Values.TryGetValue(AbsoluteVolumeKey, out object value) && value is bool enabled && enabled;
            set => ApplicationData.Current.LocalSettings.Values[AbsoluteVolumeKey] = value;
        }

        public static bool LyricsAutoScrollEnabled
        {
            get => !ApplicationData.Current.LocalSettings.Values.TryGetValue(LyricsAutoScrollKey, out object value) || !(value is bool enabled) || enabled;
            set => ApplicationData.Current.LocalSettings.Values[LyricsAutoScrollKey] = value;
        }

        public static string AudioEffectsPreset
        {
            get => ApplicationData.Current.LocalSettings.Values.TryGetValue(AudioEffectsKey, out object value) ? value as string : "None";
            set => ApplicationData.Current.LocalSettings.Values[AudioEffectsKey] = value ?? "None";
        }
    }
}
