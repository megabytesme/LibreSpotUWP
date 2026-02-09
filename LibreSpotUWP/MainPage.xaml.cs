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
        private ILibrespotService _librespot;
        private ISpotifyAuthService _auth;
        private DispatcherTimer _positionTimer;
        private bool _isDraggingSlider = false;

        public MainPage()
        {
            this.InitializeComponent();
            this.Loaded += MainPage_Loaded;
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            _librespot = App.Librespot;
            _auth = App.SpotifyAuth;
            var media = App.Media;

            if (_librespot != null)
            {
                _librespot.SessionStateChanged += (s, state) => RunOnUI(() => UpdateLibrespotStatus(state));
                _librespot.TrackChanged += (s, track) => RunOnUI(() => UpdateTrackUI(track));
                _librespot.PlaybackStateChanged += (s, state) => RunOnUI(() => UpdatePlaybackButtons(state));
                _librespot.VolumeChanged += (s, vol) => RunOnUI(() =>
                {
                    double uiValue = (vol * 100.0) / 65535.0;
                    VolumeSlider.Value = uiValue;
                });
            }

            if (media != null)
            {
                media.MediaStateChanged += (s, state) => RunOnUI(() => UpdateMetadataUI(state));
            }

            if (_auth != null)
                _auth.AuthStateChanged += (s, state) => RunOnUI(() => UpdateSpotifyApiStatus(state));

            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _positionTimer.Tick += PositionTimer_Tick;
            _positionTimer.Start();

            UpdateLibrespotStatus(_librespot?.Session);
            UpdateSpotifyApiStatus(_auth?.Current);
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
        private void SetLoading(bool isLoading)
        {
            RunOnUI(() => {
                LoadingProgressRing.IsActive = isLoading;
                LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            });
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
                        SetLoading(true);
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
                SetLoading(false);
            }
        }

        private void PositionTimer_Tick(object sender, object e)
        {
            if (_librespot == null || _isDraggingSlider || _librespot.PlaybackState != LibrespotPlaybackState.Playing)
                return;

            uint posMs = _librespot.GetPositionMs();
            PositionSlider.Value = posMs;
            CurrentTimeText.Text = FormatTime(posMs);
        }

        private void UpdateTrackUI(LibrespotTrackInfo track)
        {
            if (track == null) return;
            TrackTitleText.Text = track.Name;
            TrackArtistText.Text = track.Artist;
            PositionSlider.Maximum = track.Duration.TotalMilliseconds;
            TotalTimeText.Text = FormatTime((uint)track.Duration.TotalMilliseconds);
        }

        private void UpdateMetadataUI(MediaState state)
        {
            if (state.Metadata?.Album?.Images != null && state.Metadata.Album.Images.Count > 0)
            {
                var url = state.Metadata.Album.Images[0].Url;
                AlbumArtImage.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri(url));
            }
        }

        private void UpdatePlaybackButtons(LibrespotPlaybackState state)
        {
            ResumeButton.Visibility = (state == LibrespotPlaybackState.Playing) ? Visibility.Collapsed : Visibility.Visible;
            PauseButton.Visibility = (state == LibrespotPlaybackState.Playing) ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(TrackUriTextBox.Text))
                await _librespot.LoadAndPlayAsync(TrackUriTextBox.Text);
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e) => _librespot.PauseAsync();
        private void ResumeButton_Click(object sender, RoutedEventArgs e) => _librespot.ResumeAsync();
        private void StopButton_Click(object sender, RoutedEventArgs e) => _librespot.StopAsync();
        private void NextButton_Click(object sender, RoutedEventArgs e) => _librespot.Next();
        private void PrevButton_Click(object sender, RoutedEventArgs e) => _librespot.Previous();

        private void PositionSlider_PointerPressed(object sender, PointerRoutedEventArgs e) => _isDraggingSlider = true;

        private void PositionSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _librespot.Seek((uint)PositionSlider.Value);
            _isDraggingSlider = false;
        }

        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_librespot == null) return;

            ushort rawVolume = (ushort)(e.NewValue * 65535 / 100);
            _librespot.SetVolumeAsync(rawVolume);
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
                    Content = "Scan QR to Join",
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
                btnLogout.Click += (s, args) => { dialog.Hide();};

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

        private string FormatTime(uint ms)
        {
            TimeSpan t = TimeSpan.FromMilliseconds(ms);
            return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
        }

        private void RunOnUI(Action action) => _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => action());

        private void UpdateLibrespotStatus(LibrespotSessionState state)
        {
            if (state == null) LibrespotStatusText.Text = "Not Initialized";
            else LibrespotStatusText.Text = state.IsConnected ? $"Connected as {state.UserName}" : "Disconnected";
        }

        private void UpdateSpotifyApiStatus(AuthState state)
        {
            SpotifyApiStatusText.Text = (state == null || state.IsExpired) ? "Web API: Logged Out" : "Web API: Authenticated";
        }
    }
}