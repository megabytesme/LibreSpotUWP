using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Services;
using SpotifyAPI.Web;
using System;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;

namespace LibreSpotUWP.Controls
{
    public class SpotifyAccountManager
    {
        private static readonly Lazy<SpotifyAccountManager> _instance =
            new Lazy<SpotifyAccountManager>(() => new SpotifyAccountManager());

        public static SpotifyAccountManager Instance => _instance.Value;

        public PrivateUser User { get; private set; }

        public event EventHandler<PrivateUser> UserChanged;

        private SpotifyAccountManager() { }

        public void SetUser(PrivateUser user)
        {
            User = user;
            UserChanged?.Invoke(this, user);
        }
    }

    public sealed partial class SpotifyAccountControl : UserControl
    {
        private ISpotifyAuthService _auth;
        private ISpotifyWebService _web;

        private PrivateUser _user;

        public SpotifyAccountControl()
        {
            InitializeComponent();

            _auth = App.SpotifyAuth;
            _web = App.SpotifyWeb;

            Loaded += async (s, e) => await Initialize();

            SpotifyAccountManager.Instance.UserChanged += OnGlobalUserChanged;
            _auth.AuthStateChanged += OnAuthStateChanged;
        }

        public async Task Initialize()
        {
            var token = await _auth.GetAccessToken();

            if (string.IsNullOrEmpty(token))
            {
                UpdateUserUI(null);
                return;
            }

            if (SpotifyAccountManager.Instance.User != null)
            {
                UpdateUserUI(SpotifyAccountManager.Instance.User);
            }
            else
            {
                await RefreshUserProfileAsync();
            }
        }

        private async void OnAuthStateChanged(object sender, Models.AuthState state)
        {
            if (state == null)
            {
                UpdateUserUI(null);
                return;
            }

            await RefreshUserProfileAsync();
        }

        private void OnGlobalUserChanged(object sender, PrivateUser user)
        {
            _user = user;
            UpdateUserUI(user);
        }

        private async Task RefreshUserProfileAsync()
        {
            IsLoading = true;

            try
            {
                var result = await _web.GetCurrentUserAsync(forceRefresh: true);
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

        public void UpdateUserUI(PrivateUser user)
        {
            _user = user;

            if (user != null)
            {
                DisplayNameText.Text = user.DisplayName ?? "Unknown User";
                EmailText.Text = user.Email ?? user.Id;

                var img = user.Images?.Count > 0 ? user.Images[0].Url : null;
                UserAvatarBrush.ImageSource = img != null ? new BitmapImage(new Uri(img)) : null;

                BtnManage.Content = "Manage";
            }
            else
            {
                DisplayNameText.Text = "Not Signed In";
                EmailText.Text = "Connect your Spotify account";
                UserAvatarBrush.ImageSource = null;
                BtnManage.Content = "Sign In";
            }

            UserChanged?.Invoke(this, new UserChangedEventArgs { User = user });
        }

        public class UserChangedEventArgs : EventArgs
        {
            public PrivateUser User { get; set; }
        }

        public event EventHandler<UserChangedEventArgs> UserChanged;

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
                btnScan.Click += (s, args) =>
                {
                    dialog.Hide();

                    var frame = Window.Current.Content as Frame;
                    frame?.Navigate(typeof(ScannerPage));
                };

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
                btnLogout.Click += (s, args) =>
                {
                    dialog.Hide();

                    _auth.ResetAuthStateAsync();

                    SpotifyAccountManager.Instance.SetUser(null);
                };

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
                var image = new Windows.UI.Xaml.Controls.Image
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

        public event EventHandler<bool> LoadingStateChanged;

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; LoadingStateChanged?.Invoke(this, value); }
        }
    }
}