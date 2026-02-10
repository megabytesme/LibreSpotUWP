using LibreSpotUWP.Exceptions;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.ViewModels;
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LibreSpotUWP.Views.Win10_1507
{
    public sealed partial class HomePage_Win10_1507 : Page
    {
        public HomePageViewModel ViewModel { get; } = new HomePageViewModel();

        private ISpotifyAuthService _auth;
        private ISpotifyWebService _spotify;
        private CancellationTokenSource _cts;

        public HomePage_Win10_1507()
        {
            this.InitializeComponent();
            this.DataContext = this;
            this.Loaded += HomePage_Loaded;
            this.Unloaded += (s, e) => _cts?.Cancel();
        }

        private async void HomePage_Loaded(object sender, RoutedEventArgs e)
        {
            _auth = App.SpotifyAuth;
            _spotify = App.SpotifyWeb;

            if (!await EnsureAuthenticatedAsync())
                return;

            await LoadHomepageAsync();
        }

        private async Task<bool> EnsureAuthenticatedAsync()
        {
            if (_auth.Current == null) return false;

            if (_auth.Current.IsExpired)
            {
                try
                {
                    await _auth.RefreshAsync();
                }
                catch
                {
                    return false;
                }
            }
            return !_auth.Current.IsExpired;
        }

        private async Task LoadHomepageAsync()
        {
            try
            {
                _cts?.Cancel();
                _cts = new CancellationTokenSource();

                await ViewModel.LoadAsync(_spotify, _cts.Token);
            }
            catch (OperationCanceledException) {  }
            catch (SpotifyWebException ex)
            {
                System.Diagnostics.Debug.WriteLine("Homepage Load Failed: " + ex.Message);
            }
        }
    }
}