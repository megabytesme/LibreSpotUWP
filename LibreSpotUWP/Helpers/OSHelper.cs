using Windows.ApplicationModel;
using Windows.Foundation.Metadata;
using Windows.System.Profile;

namespace LibreSpotUWP.Helpers
{
    public static class OSHelper
    {
        public static bool IsWindows11 { get; } =
            ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 13);

        public static bool IsWindows10_1709OrGreater { get; } =
            ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 5);

        public static string DeviceFamily { get; } = AnalyticsInfo.VersionInfo.DeviceFamily ?? string.Empty;

        public static bool IsWindowsMobile =>
            string.Equals(DeviceFamily, "Windows.Mobile", System.StringComparison.OrdinalIgnoreCase);

        public static bool IsDesktopFamily =>
            string.Equals(DeviceFamily, "Windows.Desktop", System.StringComparison.OrdinalIgnoreCase);

        public static bool IsXboxFamily =>
            string.Equals(DeviceFamily, "Windows.Xbox", System.StringComparison.OrdinalIgnoreCase);

        public static bool IsHolographicFamily =>
            string.Equals(DeviceFamily, "Windows.Holographic", System.StringComparison.OrdinalIgnoreCase);

        public static bool SupportsBrowserSpotifyLogin =>
            !IsWindowsMobile && (IsDesktopFamily || IsXboxFamily || IsHolographicFamily);

        public static bool SupportsWin10_1507Appearance => true;
        public static bool SupportsWin10_1709Appearance => IsWindows10_1709OrGreater;
        public static bool SupportsWin11Appearance => IsWindows11;

        public static string PlatformName
        {
            get
            {
                return IsWindows11 ? "Windows 11 (UWP)" : "Windows 10 (UWP)";
            }
        }

        public static string OsFamily
        {
            get
            {
                return "Windows";
            }
        }

        public static string AppVersion
        {
            get
            {
                var v = Package.Current.Id.Version;
                return $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        public static string PlatformFamily
        {
            get
            {
                return "UWP";
            }
        }

        public static string Architecture
        {
            get
            {
                return Package.Current.Id.Architecture.ToString().ToLower();
            }
        }

        public static string GetOsDescriptor
        {
            get
            {
                return $"{OsFamily} v{AppVersion} ({PlatformFamily} {Architecture})";
            }
        }
    }
}
