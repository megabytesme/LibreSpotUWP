using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;

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
                _librespot.VolumeChanged += (s, vol) => RunOnUI(() => VolumeSlider.Value = vol);
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
            _librespot?.SetVolumeAsync((ushort)e.NewValue);
        }

        private async void SpotifyLoginButton_Click(object sender, RoutedEventArgs e) => await _auth.BeginPkceLoginAsync();

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