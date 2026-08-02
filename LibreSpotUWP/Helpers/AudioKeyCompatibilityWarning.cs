using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LibreSpotUWP.Helpers
{
    public static class AudioKeyCompatibilityWarning
    {
        public const string IssueUrl = "https://github.com/librespot-org/librespot/issues/1649";
        private static int _dialogOpen;

        public static async Task<bool> ShowIfNeededAsync(bool allowCancel = false)
        {
            if (UserSettings.HideAudioKeyCompatibilityWarning)
                return true;

            if (Interlocked.CompareExchange(ref _dialogOpen, 1, 0) != 0)
                return true;

            try
            {
                var content = new StackPanel();
                content.Children.Add(new TextBlock
                {
                    Text =
                        "Some Spotify accounts created after 2024 may sign in successfully but still be unable to play music. " +
                        "Spotify appears to require a newer audio-key protection/DRM flow for some accounts, and that flow " +
                        "has not yet been reverse-engineered or implemented in librespot.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 12)
                });
                content.Children.Add(new TextBlock
                {
                    Text =
                        "Spotify does not expose an account creation date to LibreSpotUWP, so the app cannot determine in " +
                        "advance whether this account is affected. You can continue, but playback is not guaranteed.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 12)
                });

                var issueLink = new HyperlinkButton
                {
                    Content = "Open librespot audio-key issue #1649",
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 12)
                };
                issueLink.Click += async (sender, args) =>
                    await Launcher.LaunchUriAsync(new Uri(IssueUrl));
                content.Children.Add(issueLink);

                var dontShowAgain = new CheckBox
                {
                    Name = "DontShowAudioKeyCompatibilityWarningCheckBox",
                    Content = "Don't show this warning again"
                };
                content.Children.Add(dontShowAgain);

                var dialog = new ContentDialog
                {
                    Title = "Spotify account compatibility warning",
                    Content = content,
                    PrimaryButtonText = "Continue",
                    DefaultButton = ContentDialogButton.Primary
                };
                if (allowCancel)
                    dialog.CloseButtonText = "Cancel";

                var result = await dialog.ShowAsync();
                var continued = !allowCancel || result == ContentDialogResult.Primary;
                if (continued && dontShowAgain.IsChecked == true)
                    UserSettings.HideAudioKeyCompatibilityWarning = true;

                return continued;
            }
            finally
            {
                Interlocked.Exchange(ref _dialogOpen, 0);
            }
        }
    }
}
