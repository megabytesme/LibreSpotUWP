using LibreSpotUWP.Interfaces;
using System;
using System.Threading.Tasks;
using Windows.System;

namespace LibreSpotUWP.Helpers
{
    public class UwpSettingsNavigator : ISettingsNavigator
    {
        public async Task OpenBackgroundSettingsAsync()
        {
            if (OSHelper.IsWindows11)
            {
                string pfn = Windows.ApplicationModel.Package.Current.Id.FamilyName;
                var uri = new Uri($"ms-settings:appsfeatures-app?{pfn}");
                await Launcher.LaunchUriAsync(uri);
            }
            else
            {
                var uri = new Uri("ms-settings:privacy-backgroundapps");
                await Launcher.LaunchUriAsync(uri);
            }
        }
    }
}
