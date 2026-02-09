using Windows.ApplicationModel;
using Windows.Foundation.Metadata;

namespace LibreSpotUWP.Helpers
{
    public static class OSHelper
    {
        public static bool IsWindows11 { get; private set; } = false;
        public static bool IsWindows10_1709OrGreater { get; private set; } = false;

        static OSHelper()
        {
            IsWindows11 =
                ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 13);

            IsWindows10_1709OrGreater =
                ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 5);
        }

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
