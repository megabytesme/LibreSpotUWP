using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using LibreSpotUWP.Services;
using LibreSpotUWP.Helpers;
using System;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LibreSpotUWP.Controls
{
    public class SpotifyAccountManager
    {
        private static readonly Lazy<SpotifyAccountManager> _instance =
            new Lazy<SpotifyAccountManager>(() => new SpotifyAccountManager());

        public static SpotifyAccountManager Instance => _instance.Value;

        public AppUserProfile User { get; private set; }

        public event EventHandler<AppUserProfile> UserChanged;

        private SpotifyAccountManager() { }

        public void SetUser(AppUserProfile user)
        {
            User = user;
            UserChanged?.Invoke(this, user);
        }
    }

    public sealed partial class SpotifyAccountControl : UserControl
    {
        private static readonly Uri LoginHelperProjectUri = new Uri("https://github.com/megabytesme/LibreSpotUWPLoginHelper/releases/latest");
        private ISpotifyAuthService _auth;
        private ISpotifyPlaybackAuthService _playbackAuth;
        private ISpotifyWebService _web;

        private AppUserProfile _user;
        private bool _isLoading;

        public SpotifyAccountControl()
        {
            InitializeComponent();

            _auth = App.SpotifyAuth;
            _playbackAuth = App.SpotifyPlaybackAuth;
            _web = App.SpotifyWeb;

            Loaded += async (s, e) => await Initialize();

            SpotifyAccountManager.Instance.UserChanged += OnGlobalUserChanged;
            _auth.AuthStateChanged += OnAuthStateChanged;
            _playbackAuth.PlaybackAuthStateChanged += OnPlaybackAuthStateChanged;
            Unloaded += OnUnloaded;
        }

        public event EventHandler<UserChangedEventArgs> UserChanged;
        public event EventHandler<bool> LoadingStateChanged;

        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                _isLoading = value;
                LoadingStateChanged?.Invoke(this, value);
            }
        }

        public async Task Initialize()
        {
            UpdatePlaybackStatus(_playbackAuth.Current);
            var token = await _auth.GetAccessToken();

            if (string.IsNullOrEmpty(token))
            {
                UpdateUserUI(null);
                return;
            }

            if (SpotifyAccountManager.Instance.User != null)
            {
                UpdateUserUI(SpotifyAccountManager.Instance.User);
                return;
            }

            await RefreshUserProfileAsync();
        }

        private async void OnAuthStateChanged(object sender, AuthState state)
        {
            if (state == null)
            {
                _user = null;
                if (SpotifyAccountManager.Instance.User != null)
                    SpotifyAccountManager.Instance.SetUser(null);
                else
                    UpdateUserUI(null);
                return;
            }

            await RefreshUserProfileAsync();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            SpotifyAccountManager.Instance.UserChanged -= OnGlobalUserChanged;
            _auth.AuthStateChanged -= OnAuthStateChanged;
            _playbackAuth.PlaybackAuthStateChanged -= OnPlaybackAuthStateChanged;
            Unloaded -= OnUnloaded;
        }

        private async void OnPlaybackAuthStateChanged(object sender, PlaybackAuthState state)
        {
            await Dispatcher.RunAsync(
                Windows.UI.Core.CoreDispatcherPriority.Normal,
                () => UpdatePlaybackStatus(state));
        }

        private void UpdatePlaybackStatus(PlaybackAuthState state)
        {
            if (state == null || state.Status == PlaybackAuthorizationStatus.Missing)
            {
                PlaybackStatusText.Text = "Playback authorization required";
                return;
            }

            switch (state.Status)
            {
                case PlaybackAuthorizationStatus.BootstrapPending:
                    PlaybackStatusText.Text = "Finishing playback authorization...";
                    break;
                case PlaybackAuthorizationStatus.Ready:
                    PlaybackStatusText.Text = "Playback authorized";
                    break;
                case PlaybackAuthorizationStatus.Rejected:
                    PlaybackStatusText.Text = "Playback authorization expired";
                    break;
                default:
                    PlaybackStatusText.Text = "Playback authorization required";
                    break;
            }
        }

        private void OnGlobalUserChanged(object sender, AppUserProfile user)
        {
            _user = user;
            UpdateUserUI(user);
        }

        private async Task RefreshUserProfileAsync()
        {
            IsLoading = true;

            try
            {
                var result = await _web.GetCurrentUserProfileAsync(forceRefresh: true);
                SpotifyAccountManager.Instance.SetUser(result?.Value);
            }
            catch
            {
                SpotifyAccountManager.Instance.SetUser(null);
            }
            finally
            {
                IsLoading = false;
            }
        }

        public void UpdateUserUI(AppUserProfile user)
        {
            _user = user;

            if (user != null)
            {
                DisplayNameText.Text = user.DisplayName ?? "Unknown User";
                EmailText.Text = user.Email ?? user.Id;

                var img = user.Images != null && user.Images.Count > 0 ? user.Images[0].Url : null;
                UserAvatarBrush.ImageSource = ImageUriHelper.CreateBitmapImage(img, useFallback: true);

                BtnManage.Content = "Manage";
            }
            else
            {
                DisplayNameText.Text = "Not Signed In";
                EmailText.Text = "Connect your Spotify account";
                PlaybackStatusText.Text = "Sign in to authorize playback";
                UserAvatarBrush.ImageSource = null;
                BtnManage.Content = "Sign In";
            }

            UserChanged?.Invoke(this, new UserChangedEventArgs { User = user });
        }

        public class UserChangedEventArgs : EventArgs
        {
            public AppUserProfile User { get; set; }
        }

        private async void BtnManage_Click(object sender, RoutedEventArgs e)
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
                var btnScan = new Button
                {
                    Content = "Scan QR to Sign in",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                btnScan.Click += async (s, args) =>
                {
                    dialog.Hide();
                    await ShowQrSignInHelpAsync();
                };

                var btnPaste = new Button
                {
                    Content = "Paste Sign-in Details",
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                btnPaste.Click += async (s, args) =>
                {
                    dialog.Hide();
                    await PasteSignInDetailsAsync();
                };

                if (OSHelper.SupportsBrowserSpotifyLogin && UserSettings.HasSpotifyCustomClientId)
                {
                    var btnLogin = new Button
                    {
                        Content = "Sign in with Spotify",
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Margin = new Thickness(0, 0, 0, 10)
                    };
                    btnLogin.Click += async (s, args) =>
                    {
                        dialog.Hide();
                        if (await AudioKeyCompatibilityWarning.ShowIfNeededAsync(allowCancel: true))
                            await _auth.BeginPkceLoginAsync();
                    };
                    stackPanel.Children.Add(btnLogin);
                }

                stackPanel.Children.Add(btnScan);
                stackPanel.Children.Add(btnPaste);
            }
            else
            {
                var playbackPackage = await App.SpotifyPlaybackAuth.ExportAsync();
                if (playbackPackage == null)
                {
                    if (OSHelper.SupportsBrowserSpotifyLogin)
                    {
                        var btnAuthorizePlaybackInBrowser = new Button
                        {
                            Content = "Authorize Playback in Browser",
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            Margin = new Thickness(0, 0, 0, 10)
                        };
                        btnAuthorizePlaybackInBrowser.Click += async (s, args) =>
                        {
                            dialog.Hide();
                            await PlaybackBrowserAuthorizationHelper.ShowAsync(
                                _user?.Id ?? App.SpotifyPlaybackAuth.Current?.AccountId);
                        };
                        stackPanel.Children.Add(btnAuthorizePlaybackInBrowser);
                    }

                    var btnAuthorizePlayback = new Button
                    {
                        Content = "Authorize Playback with Login Helper",
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Margin = new Thickness(0, 0, 0, 10)
                    };
                    btnAuthorizePlayback.Click += async (s, args) =>
                    {
                        dialog.Hide();
                        await ShowQrSignInHelpAsync();
                    };
                    stackPanel.Children.Add(btnAuthorizePlayback);
                }

                var btnShare = new Button
                {
                    Content = "Share My Session (QR)",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                btnShare.IsEnabled = playbackPackage != null;
                btnShare.Click += async (s, args) => { dialog.Hide(); await ShareCurrentAccountQrAsync(); };

                var btnShareText = new Button
                {
                    Content = "Share My Session (Text)",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                btnShareText.IsEnabled = playbackPackage != null;
                btnShareText.Click += async (s, args) => { dialog.Hide(); await ShareCurrentAccountTextAsync(); };

                var btnLogout = new Button
                {
                    Content = "Log Out",
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                btnLogout.Click += async (s, args) =>
                {
                    dialog.Hide();
                    await App.Librespot.DisconnectAsync();
                    await App.SpotifyPlaybackAuth.ResetAsync();
                    await _auth.ResetAuthStateAsync();
                    SpotifyAccountManager.Instance.SetUser(null);
                };

                stackPanel.Children.Add(btnShare);
                stackPanel.Children.Add(btnShareText);
                stackPanel.Children.Add(btnLogout);
            }

            await dialog.ShowAsync();
        }

        private async Task ShareCurrentAccountQrAsync()
        {
            if (_auth.Current == null)
                return;

            string json = await BuildCurrentAccountPayloadAsync();
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
                    Text = "Scan this on your other device to sign in. WARNING: This QR code contains your login session. Only share this with your own devices or trusted users.",
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

        private async Task ShareCurrentAccountTextAsync()
        {
            if (_auth.Current == null)
                return;

            var textBox = new TextBox
            {
                Text = await BuildCurrentAccountPayloadAsync(),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                IsReadOnly = true,
                Height = 220
            };

            var container = new StackPanel();
            container.Children.Add(new TextBlock
            {
                Text = "This text contains your login session. Only paste it into your own LibreSpotUWP devices or trusted clients.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });
            container.Children.Add(textBox);

            var dialog = new ContentDialog
            {
                Title = "Share Login Details",
                Content = container,
                PrimaryButtonText = "Close"
            };

            await dialog.ShowAsync();
        }

        private async Task PasteSignInDetailsAsync()
        {
            await QrLoginHelper.ShowPasteSignInDetailsAsync(_auth, value => IsLoading = value);
        }

        private async Task<string> BuildCurrentAccountPayloadAsync()
        {
            var playback = await App.SpotifyPlaybackAuth.ExportAsync();
            if (playback == null)
                throw new InvalidOperationException("Spotify playback must be authorized before this session can be shared.");

            var package = new LoginPackage
            {
                Format = LoginPackage.CurrentFormat,
                Version = LoginPackage.CurrentVersion,
                MinimumAppVersion = "1.0.5.0",
                AccountId = _user?.Id ?? App.SpotifyPlaybackAuth.Current?.AccountId,
                Web = _auth.Current,
                Playback = playback
            };

            return await Task.Run(() =>
            {
                UiResponsivenessTelemetry.VerifyBackgroundThread("account export JSON serialization");
                return Newtonsoft.Json.JsonConvert.SerializeObject(package);
            });
        }

        private async Task ShowQrSignInHelpAsync()
        {
            var content = new StackPanel();

            content.Children.Add(new TextBlock
            {
                Text = "Scan a QR code from another LibreSpotUWP device that is already signed in, or use the LibreSpotUWP Login Helper app on another Windows device to generate one.",
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap
            });

            var linkButton = new HyperlinkButton
            {
                Content = "Open LibreSpotUWP Login Helper on GitHub",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 12)
            };
            linkButton.Click += async (s, e) => await Launcher.LaunchUriAsync(LoginHelperProjectUri);
            content.Children.Add(linkButton);

            content.Children.Add(new TextBlock
            {
                Text = "When you are ready, press Start Scanning and point your camera at the QR code.",
                TextWrapping = TextWrapping.Wrap
            });

            var dialog = new ContentDialog
            {
                Title = "Sign in with QR Code",
                Content = content,
                PrimaryButtonText = "Start Scanning",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            var frame = Window.Current.Content as Frame;
            frame?.Navigate(typeof(ScannerPage));
        }
    }
}
