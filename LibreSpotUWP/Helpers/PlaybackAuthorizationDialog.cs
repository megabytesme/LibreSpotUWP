using LibreSpotUWP.Models;
using LibreSpotUWP.Controls;
using System;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LibreSpotUWP.Helpers
{
    public static class PlaybackAuthorizationDialog
    {
        private static readonly Uri LoginHelperUri =
            new Uri("https://github.com/megabytesme/LibreSpotUWPLoginHelper/releases/latest");
        private static bool _dialogOpen;
        private static bool _shownForCurrentProblem;

        public static async Task ShowIfNeededAsync(bool force = false)
        {
            var auth = App.SpotifyPlaybackAuth;
            var web = App.SpotifyAuth?.Current;
            if (auth == null || web == null || string.IsNullOrWhiteSpace(web.AccessToken))
                return;

            var state = auth.Current;
            var needsAuthorization = state == null ||
                state.Status == PlaybackAuthorizationStatus.Missing ||
                state.Status == PlaybackAuthorizationStatus.Rejected;
            if (!needsAuthorization)
            {
                _shownForCurrentProblem = false;
                return;
            }

            if (_dialogOpen || (!force && _shownForCurrentProblem))
                return;

            _dialogOpen = true;
            _shownForCurrentProblem = true;
            try
            {
                var content = new StackPanel();
                content.Children.Add(new TextBlock
                {
                    Text = state?.Status == PlaybackAuthorizationStatus.Rejected
                        ? "Spotify rejected the saved playback authorization. Your library and playlists are still signed in, but playback must be authorized again using the same Spotify Premium account."
                        : "Spotify now authorizes music playback separately from library access. Your library and playlists remain signed in, but this device needs a one-time playback authorization.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 12)
                });
                content.Children.Add(new TextBlock
                {
                    Text = "Use the updated Login Helper on a modern Windows PC, then scan its QR code or paste the exported sign-in details here.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8)
                });

                var helperLink = new HyperlinkButton
                {
                    Content = "Download the updated Login Helper",
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                helperLink.Click += async (sender, args) => await Launcher.LaunchUriAsync(LoginHelperUri);
                content.Children.Add(helperLink);

                var useBrowser = false;
                Button browserButton = null;
                if (OSHelper.SupportsBrowserSpotifyLogin)
                {
                    browserButton = new Button
                    {
                        Content = "Authorize playback in this browser",
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Margin = new Thickness(0, 4, 0, 8)
                    };
                    content.Children.Add(browserButton);
                }

                var dialog = new ContentDialog
                {
                    Title = "Spotify playback authorization required",
                    Content = content,
                    PrimaryButtonText = "Scan QR",
                    SecondaryButtonText = "Paste Details",
                    CloseButtonText = "Later",
                    DefaultButton = ContentDialogButton.Primary
                };

                if (browserButton != null)
                {
                    browserButton.Click += (sender, args) =>
                    {
                        useBrowser = true;
                        dialog.Hide();
                    };
                }

                var result = await dialog.ShowAsync();
                if (useBrowser)
                {
                    await PlaybackBrowserAuthorizationHelper.ShowAsync(
                        state?.AccountId ?? SpotifyAccountManager.Instance.User?.Id);
                }
                else if (result == ContentDialogResult.Primary)
                {
                    var frame = Window.Current.Content as Frame;
                    frame?.Navigate(typeof(ScannerPage));
                }
                else if (result == ContentDialogResult.Secondary)
                {
                    await QrLoginHelper.ShowPasteSignInDetailsAsync(App.SpotifyAuth);
                }
            }
            catch (Exception ex)
            {
                _shownForCurrentProblem = false;
                Services.LogService.Warn(
                    $"[PlaybackAuthorizationDialog.ShowIfNeededAsync] Unable to show playback recovery UI: {ex.Message}");
            }
            finally
            {
                _dialogOpen = false;
            }
        }
    }
}
