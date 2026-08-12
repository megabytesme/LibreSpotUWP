using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using LibreSpotUWP.Exceptions;
using LibreSpotUWP.Services;
using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LibreSpotUWP.Helpers
{
    public static class QrLoginHelper
    {
        public static async Task ShowPasteSignInDetailsAsync(
            ISpotifyAuthService auth,
            Action<bool> setBusy = null)
        {
            var textBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 220,
                PlaceholderText = "Paste sign-in details from another LibreSpotUWP client or the Login Helper app"
            };

            var container = new StackPanel();
            container.Children.Add(new TextBlock
            {
                Text = "Paste the full sign-in details text. It uses the same data as the QR code.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });
            container.Children.Add(textBox);

            var dialog = new ContentDialog
            {
                Title = "Paste Sign-in Details",
                Content = container,
                PrimaryButtonText = "Import",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
                await ImportQrLoginAsync(textBox.Text, auth, setBusy);
        }

        public static async Task TryConsumePendingScanAsync(
            ISpotifyAuthService auth,
            Action<bool> setBusy = null)
        {
            if (ScannerPage.LastScanResult == null)
                return;

            var rawData = ScannerPage.LastScanResult.Text;
            ScannerPage.LastScanResult = null;

            await ImportQrLoginAsync(rawData, auth, setBusy);
        }

        public static async Task ImportQrLoginAsync(
            string json,
            ISpotifyAuthService auth,
            Action<bool> setBusy = null)
        {
            try
            {
                AuthState importedState = null;
                LoginPackage loginPackage = null;
                await Task.Run(() =>
                {
                    UiResponsivenessTelemetry.VerifyBackgroundThread("QR auth JSON parsing");
                    var root = JObject.Parse(json);
                    if (string.Equals((string)root["Format"], LoginPackage.CurrentFormat, StringComparison.Ordinal) ||
                        string.Equals((string)root["format"], LoginPackage.CurrentFormat, StringComparison.Ordinal))
                    {
                        loginPackage = root.ToObject<LoginPackage>();
                        if (loginPackage == null || loginPackage.Version != LoginPackage.CurrentVersion ||
                            loginPackage.Web == null || loginPackage.Playback == null)
                        {
                            throw new InvalidOperationException("The sign-in package version is not supported.");
                        }
                        importedState = loginPackage.Web;
                    }
                    else
                    {
                        importedState = root.ToObject<AuthState>();
                    }
                });
                if (importedState == null)
                    throw new InvalidOperationException("The sign-in details did not contain a valid session.");
                if (loginPackage != null)
                    await App.SpotifyPlaybackAuth.ValidateImportAsync(loginPackage.Playback);

                var stackPanel = new StackPanel();
                stackPanel.Children.Add(new TextBlock
                {
                    Text = loginPackage == null
                        ? "A legacy Spotify Web session was found. It can restore browsing, but playback will still need the updated Login Helper. Would you like to import it?"
                        : "A Spotify Web and playback session was found. Would you like to import it and sign in?",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 12)
                });

                var dialog = new ContentDialog
                {
                    Title = "Import Session",
                    Content = stackPanel,
                    PrimaryButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary
                };

                var btnImport = new Button
                {
                    Content = "Confirm Import",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Style = (Style)Application.Current.Resources["AccentButtonStyle"]
                };

                var userConfirmed = false;
                btnImport.Click += (s, args) =>
                {
                    userConfirmed = true;
                    dialog.Hide();
                };
                stackPanel.Children.Add(btnImport);

                await dialog.ShowAsync();
                if (!userConfirmed)
                    return;

                setBusy?.Invoke(true);
                await auth.ImportAuthStateAsync(importedState);
                if (loginPackage != null)
                    await App.SpotifyPlaybackAuth.ImportAsync(loginPackage.Playback, loginPackage.AccountId);
                else
                    await App.SpotifyPlaybackAuth.ResetAsync();

                var successDialog = new ContentDialog
                {
                    Title = "Success",
                    Content = new TextBlock
                    {
                        Text = loginPackage == null
                            ? "Web session imported. Use the updated Login Helper to authorize playback."
                            : "Web and playback authorization imported successfully."
                    },
                    PrimaryButtonText = "OK"
                };
                await successDialog.ShowAsync();
                await AudioKeyCompatibilityWarning.ShowIfNeededAsync();
            }
            catch (SpotifyPremiumRequiredException ex)
            {
                await PremiumRequiredDialog.ShowAsync(ex);
            }
            catch (Exception ex)
            {
                LogService.Warn($"[QrLoginHelper.ImportQrLoginAsync] Import failed: {ex.Message}");
                var errorDialog = new ContentDialog
                {
                    Title = "Import Failed",
                    Content = new TextBlock { Text = "Failed to read sign-in details. They may be corrupted or in an invalid format." },
                    PrimaryButtonText = "Close"
                };
                await errorDialog.ShowAsync();
            }
            finally
            {
                setBusy?.Invoke(false);
            }
        }
    }
}
