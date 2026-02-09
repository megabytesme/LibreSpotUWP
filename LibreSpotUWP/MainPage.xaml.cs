using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using LibreSpotUWP.Services;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace LibreSpotUWP
{
    public sealed partial class MainPage : Page
    {
        private IMediaService _media;
        private ISpotifyAuthService _auth;
        private bool _isDraggingSlider = false;

        public MainPage()
        {
            InitializeComponent();
            Loaded += MainPage_Loaded;
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            _media = App.Media;
            _auth = App.SpotifyAuth;

            DataContext = _media.Current;

            _media.MediaStateChanged += (s, state) =>
            {
                RunOnUI(() =>
                {
                    DataContext = state;

                    if (state.Metadata?.Album?.Images != null &&
                        state.Metadata.Album.Images.Count > 0)
                    {
                        AlbumArtImage.Source =
                            new Windows.UI.Xaml.Media.Imaging.BitmapImage(
                                new Uri(state.Metadata.Album.Images[0].Url));
                    }
                });
            };

            if (_auth != null)
                _auth.AuthStateChanged += (s, state) => RunOnUI(() => UpdateSpotifyApiStatus(state));

            UpdateSpotifyApiStatus(_auth?.Current);
        }

        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(TrackUriTextBox.Text))
                await _media.PlayTrackAsync(TrackUriTextBox.Text);
        }

        private async void PauseButton_Click(object sender, RoutedEventArgs e)
            => await _media.PauseAsync();

        private async void ResumeButton_Click(object sender, RoutedEventArgs e)
            => await _media.ResumeAsync();

        private async void StopButton_Click(object sender, RoutedEventArgs e)
            => await _media.StopAsync();

        private void NextButton_Click(object sender, RoutedEventArgs e)
            => _media.Next();

        private void PrevButton_Click(object sender, RoutedEventArgs e)
            => _media.Previous();

        private void PositionSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
            => _isDraggingSlider = true;

        private void PositionSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _media.Seek((uint)PositionSlider.Value);
            _isDraggingSlider = false;
        }

        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            _media.SetVolumeDebounced(e.NewValue);
        }

        private void RunOnUI(Action action)
            => _ = Dispatcher.RunAsync(
                Windows.UI.Core.CoreDispatcherPriority.Normal,
                () => action());

        private void UpdateLibrespotStatus(LibrespotSessionState state)
        {
            if (state == null) LibrespotStatusText.Text = "Not Initialized";
            else LibrespotStatusText.Text = state.IsConnected ? $"Connected as {state.UserName}" : "Disconnected";
        }

        private void UpdateSpotifyApiStatus(AuthState state)
        {
            SpotifyApiStatusText.Text = (state == null || state.IsExpired) ? "Web API: Logged Out" : "Web API: Authenticated";
        }

        private async void AccountButton_Click(object sender, RoutedEventArgs e)
        {
            var stackPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

            var dialog = new ContentDialog
            {
                Title = "Account Management",
                Content = stackPanel,
                PrimaryButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };

            bool isAuthenticated = !string.IsNullOrEmpty(await _auth.GetAccessToken());

            if (!isAuthenticated)
            {
                var btnLogin = new Button
                {
                    Content = "Sign in with Spotify",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                btnLogin.Click += async (s, args) => { dialog.Hide(); await _auth.BeginPkceLoginAsync(); };

                var btnScan = new Button
                {
                    Content = "Scan QR to Sign in",
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                btnScan.Click += (s, args) => { dialog.Hide(); Frame.Navigate(typeof(ScannerPage)); };

                stackPanel.Children.Add(btnLogin);
                stackPanel.Children.Add(btnScan);
            }
            else
            {
                var btnShare = new Button
                {
                    Content = "Share My Session (QR)",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                btnShare.Click += async (s, args) => { dialog.Hide(); await ShareCurrentAccountQrAsync(); };

                var btnLogout = new Button
                {
                    Content = "Log Out",
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                btnLogout.Click += (s, args) => { dialog.Hide(); };

                stackPanel.Children.Add(btnShare);
                stackPanel.Children.Add(btnLogout);
            }

            await dialog.ShowAsync();
        }

        private async Task ShareCurrentAccountQrAsync()
        {
            if (_auth.Current == null) return;

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(_auth.Current);
            var qrBitmap = await BarcodeUIService.GenerateQrCodeBitmapAsync(json);

            if (qrBitmap != null)
            {
                var image = new Image
                {
                    Source = qrBitmap,
                    Width = 300,
                    Height = 300,
                    Margin = new Thickness(0, 20, 0, 20)
                };

                var text = new TextBlock
                {
                    Text = "Scan this on your other device to sync immediately. WARNING: This QR code contains your login session. Only share this with your own devices or trusted users.",
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                var container = new StackPanel();
                container.Children.Add(image);
                container.Children.Add(text);

                var qrDialog = new ContentDialog
                {
                    Title = "Share Login Access",
                    Content = container,
                    PrimaryButtonText = "Close"
                };

                await qrDialog.ShowAsync();
            }
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (ScannerPage.LastScanResult != null)
            {
                var rawData = ScannerPage.LastScanResult.Text;
                ScannerPage.LastScanResult = null;

                await ProcessQrLoginAsync(rawData);
            }
        }

        private async Task ProcessQrLoginAsync(string json)
        {
            try
            {
                var importedState = Newtonsoft.Json.JsonConvert.DeserializeObject<AuthState>(json);

                if (importedState != null)
                {
                    var stackPanel = new StackPanel();
                    stackPanel.Children.Add(new TextBlock
                    {
                        Text = "A Spotify session was found in the QR code. Would you like to import it and sign in?",
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

                    bool userConfirmed = false;
                    btnImport.Click += (s, args) => { userConfirmed = true; dialog.Hide(); };
                    stackPanel.Children.Add(btnImport);

                    await dialog.ShowAsync();

                    if (userConfirmed)
                    {
                        RunOnUI(() =>
                        {
                            LoadingProgressRing.IsActive = true;
                            LoadingOverlay.Visibility = Visibility.Visible;
                        });
                        await _auth.ImportAuthStateAsync(importedState);

                        var successDialog = new ContentDialog
                        {
                            Title = "Success",
                            Content = new TextBlock { Text = "Session imported successfully via QR!" },
                            PrimaryButtonText = "OK"
                        };
                        await successDialog.ShowAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"QR Import Error: {ex}");

                var errorDialog = new ContentDialog
                {
                    Title = "Import Failed",
                    Content = new TextBlock { Text = "Failed to read Login QR Code. It may be corrupted or in an invalid format." },
                    PrimaryButtonText = "Close"
                };
                await errorDialog.ShowAsync();
            }
            finally
            {
                RunOnUI(() =>
                {
                    LoadingProgressRing.IsActive = false;
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                });
            }
        }
    }
}