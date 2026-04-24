using Windows.Networking.Connectivity;
using Windows.Storage;

namespace LibreSpotUWP.Helpers
{
    public static class ConnectivityHelper
    {
        private const string ManualOfflineModeKey = "ManualOfflineModeEnabled";

        public static bool HasInternetAccess()
        {
            if (IsManualOfflineModeEnabled())
                return false;

            var profile = NetworkInformation.GetInternetConnectionProfile();
            return profile?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
        }

        public static bool IsManualOfflineModeEnabled()
        {
            var settings = ApplicationData.Current.LocalSettings;
            return settings.Values.TryGetValue(ManualOfflineModeKey, out var value) &&
                value is bool enabled &&
                enabled;
        }

        public static void SetManualOfflineModeEnabled(bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values[ManualOfflineModeKey] = enabled;
        }
    }
}
