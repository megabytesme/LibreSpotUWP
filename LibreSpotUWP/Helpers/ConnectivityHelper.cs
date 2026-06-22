using System;
using Windows.Networking.Connectivity;
using Windows.Storage;

namespace LibreSpotUWP.Helpers
{
    public static class ConnectivityHelper
    {
        private const string ManualOfflineModeKey = "ManualOfflineModeEnabled";
        private static readonly TimeSpan InternetFailureBackoff = TimeSpan.FromSeconds(30);
        private static readonly object Gate = new object();
        private static DateTimeOffset _internetFailureBackoffUntil = DateTimeOffset.MinValue;

        public static event EventHandler InternetAccessFailureReported;
        public static event EventHandler ConnectivityStatusChanged;

        public static bool HasInternetAccess()
        {
            if (IsManualOfflineModeEnabled())
                return false;

            lock (Gate)
            {
                if (_internetFailureBackoffUntil > DateTimeOffset.UtcNow)
                    return false;
            }

            var profile = NetworkInformation.GetInternetConnectionProfile();
            return profile?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
        }

        public static bool HasNetworkReportedInternetAccess()
        {
            if (IsManualOfflineModeEnabled())
                return false;

            var profile = NetworkInformation.GetInternetConnectionProfile();
            return profile?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
        }

        public static TimeSpan GetInternetAccessFailureBackoffRemaining()
        {
            lock (Gate)
            {
                var remaining = _internetFailureBackoffUntil - DateTimeOffset.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        public static void ReportInternetAccessFailure(TimeSpan? backoff = null)
        {
            var until = DateTimeOffset.UtcNow.Add(backoff ?? InternetFailureBackoff);
            var shouldRaise = false;
            lock (Gate)
            {
                if (until > _internetFailureBackoffUntil)
                {
                    _internetFailureBackoffUntil = until;
                    shouldRaise = true;
                }
            }

            if (shouldRaise)
            {
                InternetAccessFailureReported?.Invoke(null, EventArgs.Empty);
                ConnectivityStatusChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        public static bool ClearInternetAccessFailure(bool force = false)
        {
            var shouldRaise = false;
            lock (Gate)
            {
                if (!force && _internetFailureBackoffUntil > DateTimeOffset.UtcNow)
                    return false;

                shouldRaise = _internetFailureBackoffUntil != DateTimeOffset.MinValue;
                _internetFailureBackoffUntil = DateTimeOffset.MinValue;
            }

            if (shouldRaise)
                ConnectivityStatusChanged?.Invoke(null, EventArgs.Empty);

            return shouldRaise;
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
            var wasEnabled = IsManualOfflineModeEnabled();
            ApplicationData.Current.LocalSettings.Values[ManualOfflineModeKey] = enabled;

            if (!enabled)
                ClearInternetAccessFailure(force: true);

            if (wasEnabled != enabled)
                ConnectivityStatusChanged?.Invoke(null, EventArgs.Empty);
        }
    }
}
