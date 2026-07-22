using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using LibreSpotUWP.Exceptions;
using LibreSpotUWP.Services;
using System;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LibreSpotUWP.Helpers
{
    public static class QrLoginHelper
    {
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
                var importedState = await Task.Run(() =>
                {
                    UiResponsivenessTelemetry.VerifyBackgroundThread("QR auth JSON parsing");
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<AuthState>(json);
                });
                if (importedState == null)
                    throw new InvalidOperationException("The sign-in details did not contain a valid session.");

                var stackPanel = new StackPanel();
                stackPanel.Children.Add(new TextBlock
                {
                    Text = "A Spotify session was found. Would you like to import it and sign in?",
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

                var successDialog = new ContentDialog
                {
                    Title = "Success",
                    Content = new TextBlock { Text = "Session imported successfully." },
                    PrimaryButtonText = "OK"
                };
                await successDialog.ShowAsync();
            }
            catch (SpotifyPremiumRequiredException ex)
            {
                await PremiumRequiredDialog.ShowAsync(ex);
            }
            catch
            {
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
