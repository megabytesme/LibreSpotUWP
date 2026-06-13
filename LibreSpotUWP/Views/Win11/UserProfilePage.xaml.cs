using LibreSpotUWP.ViewModels;
using System;
using LibreSpotUWP.Helpers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LibreSpotUWP.Views.Win11
{
    public sealed partial class UserProfilePage : Page
    {
        public UserProfilePageViewModel ViewModel { get; } = new UserProfilePageViewModel();

        public UserProfilePage()
        {
            InitializeComponent();
            DataContext = ViewModel;

            PlaylistsGrid.PlaylistClicked += (s, playlistId) =>
            {
                PlaybackNavigationHelper.FindShell(this)?.NavigateToPlaylist(playlistId);
            };
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var userId = e.Parameter as string;
            LoadingOverlay.Visibility = Visibility.Visible;

            try
            {
                await ViewModel.LoadAsync(userId);
                UpdateUi();
            }
            finally
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateUi()
        {
            var profile = ViewModel.Profile;
            ProfileNameText.Text = profile?.DisplayName ?? profile?.Id ?? "Profile";
            ProfileIdText.Text = profile?.Id ?? string.Empty;
            ProfileMetaText.Text = BuildMetaLine(profile);
            StatusText.Text = ViewModel.StatusMessage ?? string.Empty;
            StatusText.Visibility = string.IsNullOrWhiteSpace(ViewModel.StatusMessage)
                ? Visibility.Collapsed
                : Visibility.Visible;

            var imageUrl = profile?.Images != null && profile.Images.Count > 0
                ? profile.Images[0].Url
                : null;
            ProfileImage.Source = ImageUriHelper.CreateBitmapImage(imageUrl, useFallback: true);

            PlaylistsGrid.SetPlaylists(ViewModel.Playlists);
        }

        private static string BuildMetaLine(Models.AppUserProfile profile)
        {
            if (profile == null)
                return string.Empty;

            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(profile.Email))
                parts.Add(profile.Email);
            if (!string.IsNullOrWhiteSpace(profile.Country))
                parts.Add(profile.Country);

            return string.Join("  ", parts);
        }
    }
}


