using System;
using System.Threading.Tasks;
using LibreSpotUWP.Services;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LibreSpotUWP.Helpers
{
    public static class PlaybackBrowserAuthorizationHelper
    {
        public static async Task ShowAsync(string accountId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(accountId))
                {
                    var profile = await App.SpotifyWeb.GetCurrentUserProfileAsync(forceRefresh: false);
                    accountId = profile?.Value?.Id;
                }
                if (string.IsNullOrWhiteSpace(accountId))
                    throw new InvalidOperationException("The signed-in Spotify account could not be identified. Please try again.");

                var authorizeUri = await App.SpotifyPlaybackAuth.BeginBrowserAuthorizationAsync();
                if (!await Launcher.LaunchUriAsync(authorizeUri))
                    throw new InvalidOperationException("Windows could not open Spotify authorization in the browser.");

                var callbackText = new TextBox
                {
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    Height = 120,
                    PlaceholderText = "http://127.0.0.1:5588/login?code=...&state=..."
                };
                var content = new StackPanel();
                content.Children.Add(new TextBlock
                {
                    Text = "Approve playback in Spotify. The browser will finish on a local page that may say it cannot connect; that is expected. Copy the full address from the browser, return here, and paste it below.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 12)
                });
                content.Children.Add(callbackText);

                var dialog = new ContentDialog
                {
                    Title = "Complete Spotify Playback Authorization",
                    Content = content,
                    PrimaryButtonText = "Complete",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    return;

                await App.SpotifyPlaybackAuth.CompleteBrowserAuthorizationAsync(
                    callbackText.Text,
                    accountId);

                await new ContentDialog
                {
                    Title = "Playback Authorization Received",
                    Content = new TextBlock
                    {
                        Text = "LibreSpotUWP is finishing the one-time playback connection. Future launches will use the reusable credential saved securely on this device.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    PrimaryButtonText = "OK"
                }.ShowAsync();
            }
            catch (Exception ex)
            {
                LogService.Warn($"[PlaybackBrowserAuthorizationHelper.ShowAsync] Authorization failed: {ex.Message}");
                await new ContentDialog
                {
                    Title = "Playback Authorization Failed",
                    Content = new TextBlock
                    {
                        Text = ex.Message,
                        TextWrapping = TextWrapping.Wrap
                    },
                    PrimaryButtonText = "Close"
                }.ShowAsync();
            }
        }
    }
}
