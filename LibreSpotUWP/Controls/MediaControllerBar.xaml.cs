using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;

namespace LibreSpotUWP.Controls
{
    public sealed partial class MediaControllerBar : UserControl
    {
        private IMediaService _media => App.Media;

        private bool _draggingPosition = false;

        public MediaControllerBar()
        {
            InitializeComponent();
            Loaded += MediaControllerBar_Loaded;
        }

        private void MediaControllerBar_Loaded(object sender, RoutedEventArgs e)
        {
            if (_media == null)
                return;

            _media.MediaStateChanged += (s, state) =>
            {
                Dispatcher.RunAsync(
                    Windows.UI.Core.CoreDispatcherPriority.Normal,
                    () => UpdateUI(state));
            };

            UpdateUI(_media.Current);
        }

        private void UpdateUI(MediaState state)
        {
            TrackTitle.Text = state.Track?.Name ?? "";
            TrackArtist.Text = state.Track?.Artist ?? "";

            if (state.Metadata?.Album?.Images?.Count > 0)
                AlbumArt.Source = new BitmapImage(new Uri(state.Metadata.Album.Images[0].Url));

            if (!_draggingPosition)
            {
                PositionSlider.Maximum = state.DurationMs;
                PositionSlider.Value = state.PositionMs;
            }

            CurrentTime.Text = Format(state.PositionMs);
            TotalTime.Text = Format(state.DurationMs);

            PlayButton.Visibility = state.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
            PauseButton.Visibility = state.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        }

        private string Format(uint ms)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
        }

        private void Prev_Click(object sender, RoutedEventArgs e) => _media?.Previous();
        private void Next_Click(object sender, RoutedEventArgs e) => _media?.Next();
        private async void Play_Click(object sender, RoutedEventArgs e) => await _media?.ResumeAsync();
        private async void Pause_Click(object sender, RoutedEventArgs e) => await _media?.PauseAsync();
        private async void Stop_Click(object sender, RoutedEventArgs e) => await _media?.StopAsync();

        private void PositionSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
            => _draggingPosition = true;

        private void PositionSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _draggingPosition = false;
            _media?.Seek((uint)PositionSlider.Value);
        }

        private void PositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_draggingPosition)
                CurrentTime.Text = Format((uint)e.NewValue);
        }

        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            _media?.SetVolumeDebounced(e.NewValue);
        }
    }
}