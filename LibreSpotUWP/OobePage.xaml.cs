using LibreSpotUWP.Controls;
using LibreSpotUWP.Helpers;
using LibreSpotUWP.Models;
using LibreSpotUWP.Services;
using System;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LibreSpotUWP
{
    public sealed partial class OobePage : Page
    {
        private static readonly Uri LoginHelperProjectUri =
            new Uri("https://github.com/megabytesme/LibreSpotUWPLoginHelper/releases/latest");

        private bool _checkingAuthState;
        private bool _listeningForAuthState;

        public OobePage()
        {
            InitializeComponent();

            Loaded += OobePage_Loaded;
            Unloaded += OobePage_Unloaded;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            await QrLoginHelper.TryConsumePendingScanAsync(App.SpotifyAuth, SetBusy);
            await RefreshSignedInStateAsync();
        }

        private async void OobePage_Loaded(object sender, RoutedEventArgs e)
        {
            if (App.SpotifyAuth != null && !_listeningForAuthState)
            {
                App.SpotifyAuth.AuthStateChanged += SpotifyAuth_AuthStateChanged;
                _listeningForAuthState = true;
            }

            UpdateDirectSignInUi();
            await RefreshSignedInStateAsync();
        }

        private void OobePage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (App.SpotifyAuth != null && _listeningForAuthState)
            {
                App.SpotifyAuth.AuthStateChanged -= SpotifyAuth_AuthStateChanged;
                _listeningForAuthState = false;
            }
        }

        private async void SpotifyAuth_AuthStateChanged(object sender, AuthState e)
        {
            await RefreshSignedInStateAsync();
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            _ = MoveToNextOobePageAsync();
        }

        private async void BtnOpenHelper_Click(object sender, RoutedEventArgs e)
        {
            await Launcher.LaunchUriAsync(LoginHelperProjectUri);
        }

        private void BtnScanQr_Click(object sender, RoutedEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                Frame?.Navigate(typeof(ScannerPage));
            });
        }

        private async void BtnPasteDetails_Click(object sender, RoutedEventArgs e)
        {
            await PasteSignInDetailsAsync();
        }

        private void BtnSaveClientId_Click(object sender, RoutedEventArgs e)
        {
            UserSettings.SpotifyCustomClientId = ClientIdTextBox.Text;
            UpdateDirectSignInUi();
        }

        private async void BtnSpotifySignIn_Click(object sender, RoutedEventArgs e)
        {
            TxtAuthStatus.Text = "Waiting for Spotify to return to LibreSpotUWP...";
            await App.SpotifyAuth.BeginPkceLoginAsync();
        }

        private void BtnLaunch_Click(object sender, RoutedEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                Frame?.BackStack.Clear();
                Frame?.Navigate(NavigationHelper.GetPageType("Shell"));
            });
        }

        private async Task PasteSignInDetailsAsync()
        {
            var textBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 220,
                PlaceholderText = "Paste sign-in details from LibreSpotUWP Login Helper"
            };

            var container = new StackPanel();
            container.Children.Add(new TextBlock
            {
                Text = "Paste the full sign-in details text. It contains the same session data as the QR code.",
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
            if (result != ContentDialogResult.Primary)
                return;

            await QrLoginHelper.ImportQrLoginAsync(textBox.Text, App.SpotifyAuth, SetBusy);
            await RefreshSignedInStateAsync();
        }

        private async Task RefreshSignedInStateAsync()
        {
            if (_checkingAuthState || App.SpotifyAuth == null)
                return;

            _checkingAuthState = true;

            try
            {
                var token = await App.SpotifyAuth.GetAccessToken();
                var isSignedIn = !string.IsNullOrEmpty(token);

                BtnReadyNext.IsEnabled = isSignedIn;
                BtnLaunch.IsEnabled = isSignedIn;

                if (!isSignedIn)
                {
                    TxtAuthStatus.Text = "No Spotify account is connected yet.";
                    TxtReadyAccount.Text = "Finish sign-in first, then LibreSpotUWP will open.";
                    return;
                }

                TxtAuthStatus.Text = "Spotify account connected. You can continue.";
                TxtReadyAccount.Text = "Your Spotify account is connected.";

                await TryLoadCurrentUserAsync();

                if (OobeFlipView.SelectedIndex == 2)
                    await MoveToOobePageAsync(3);
            }
            finally
            {
                _checkingAuthState = false;
            }
        }

        private async Task MoveToNextOobePageAsync()
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                var nextIndex = OobeFlipView.SelectedIndex + 1;
                if (nextIndex < OobeFlipView.Items.Count)
                    OobeFlipView.SelectedIndex = nextIndex;
            });
        }

        private async Task MoveToOobePageAsync(int pageIndex)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (pageIndex >= 0 && pageIndex < OobeFlipView.Items.Count)
                    OobeFlipView.SelectedIndex = pageIndex;
            });
        }

        private async Task TryLoadCurrentUserAsync()
        {
            if (App.SpotifyWeb == null)
                return;

            try
            {
                var profile = await App.SpotifyWeb.GetCurrentUserProfileAsync(forceRefresh: false);
                var user = profile?.Value;
                SpotifyAccountManager.Instance.SetUser(user);

                var name = !string.IsNullOrWhiteSpace(user?.DisplayName)
                    ? user.DisplayName
                    : user?.Id;

                if (!string.IsNullOrWhiteSpace(name))
                    TxtReadyAccount.Text = "Signed in as " + name + ".";
            }
            catch (Exception ex)
            {
                LogService.Warn($"Unable to load current user during OOBE: {ex.Message}");
            }
        }

        private void UpdateDirectSignInUi()
        {
            DirectSignInPanel.Visibility = OSHelper.SupportsBrowserSpotifyLogin
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (!OSHelper.SupportsBrowserSpotifyLogin)
                return;

            ClientIdTextBox.Text = UserSettings.SpotifyCustomClientId;

            var hasClientId = UserSettings.HasSpotifyCustomClientId;
            BtnSpotifySignIn.Visibility = hasClientId ? Visibility.Visible : Visibility.Collapsed;
            DirectSignInStatusText.Text = hasClientId
                ? "Direct browser sign-in is enabled for this device."
                : "Direct browser sign-in is optional and needs a Spotify client ID. Leave this empty and use the Login Helper if you are not sure.";
        }

        private void SetBusy(bool isBusy)
        {
            LoginProgressRing.IsActive = isBusy;
            LoginProgressRing.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;

            BtnOpenHelper.IsEnabled = !isBusy;
            BtnScanQr.IsEnabled = !isBusy;
            BtnPasteDetails.IsEnabled = !isBusy;
            BtnSaveClientId.IsEnabled = !isBusy;
            BtnSpotifySignIn.IsEnabled = !isBusy;
        }
    }
}
