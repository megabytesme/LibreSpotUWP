using LibreSpotUWP.Exceptions;
using System;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LibreSpotUWP.Helpers
{
    public static class PremiumRequiredDialog
    {
        public static async Task ShowAsync(SpotifyPremiumRequiredException exception)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = exception?.Message ?? "LibreSpotUWP requires a Spotify Premium account. Free Spotify accounts are not supported.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

            var linkButton = new HyperlinkButton
            {
                Content = "View Spotify Premium",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 4)
            };
            linkButton.Click += async (sender, args) =>
                await Launcher.LaunchUriAsync(new Uri(SpotifyPremiumRequiredException.PremiumUrl));
            content.Children.Add(linkButton);

            var dialog = new ContentDialog
            {
                Title = "Spotify Premium required",
                Content = content,
                PrimaryButtonText = "Close"
            };

            await dialog.ShowAsync();
        }
    }
}
