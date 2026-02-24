using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace LibreSpotUWP.Controls
{
    public sealed partial class MediaControllerBar : UserControl
    {
        private IMediaService _media => App.Media;
        private bool _draggingPosition = false;
        private bool _isReady = false;

        public MediaControllerBar()
        {
            InitializeComponent();
            Loaded += MediaControllerBar_Loaded;
        }

        private void MediaControllerBar_Loaded(object sender, RoutedEventArgs e)
        {
            if (_media == null) return;

            _media.MediaStateChanged += (s, state) =>
            {
                var ignored = Dispatcher.RunAsync(
                    Windows.UI.Core.CoreDispatcherPriority.Normal,
                    () => UpdateUI(state));
            };

            UpdateUI(_media.Current);
            _isReady = true;
        }

        private void UpdateUI(MediaState state)
        {
            if (state == null) return;

            string title = state.Track?.Name ?? "Unknown Track";
            string artist = state.Track?.Artist ?? "Unknown Artist";

            TrackTitle.Text = title;
            TrackArtist.Text = artist;

            ToolTipService.SetToolTip(TrackTitle, title);
            ToolTipService.SetToolTip(TrackArtist, artist);

            if (state.Metadata?.Album?.Images?.Count > 0)
            {
                AlbumArt.Source = new BitmapImage(new Uri(state.Metadata.Album.Images[0].Url));
            }

            if (!_draggingPosition)
            {
                PositionSlider.Maximum = state.DurationMs;
                PositionSlider.Value = state.PositionMs;
            }

            CurrentTime.Text = Format(state.PositionMs);
            TotalTime.Text = Format(state.DurationMs);

            PlayPauseIcon.Symbol = state.IsPlaying ? Symbol.Pause : Symbol.Play;

            UpdateShuffleVisual(state.Shuffle);
            UpdateRepeatVisual(state.RepeatMode);

            VolumeSlider.ValueChanged -= VolumeSlider_ValueChanged;
            double volumePercent = state.Volume * 100.0 / 65535.0;
            VolumeSlider.Value = volumePercent;
            VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;

            UpdateVolumeVisual(volumePercent);
        }

        private string Format(uint ms)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
        }

        private void Prev_Click(object sender, RoutedEventArgs e) => _media?.Previous();
        private void Next_Click(object sender, RoutedEventArgs e) => _media?.Next();

        private async void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_media.Current.IsPlaying)
                await _media.PauseAsync();
            else
                await _media.ResumeAsync();
        }

        private async void Shuffle_Click(object sender, RoutedEventArgs e)
        {
            await _media.SetShuffleAsync(!_media.Current.Shuffle);
        }

        private async void Repeat_Click(object sender, RoutedEventArgs e)
        {
            int mode = (_media.Current.RepeatMode + 1) % 3;
            await _media.SetRepeatAsync(mode);
        }

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
            if (!_isReady) return;

            _media?.SetVolumeDebounced(e.NewValue);

            UpdateVolumeVisual(e.NewValue);
        }

        private void UpdateVolumeVisual(double value)
        {
            if (value <= 0)
            {
                VolumeIcon.Glyph = "\uE74F";
            }
            else if (value < 10)
            {
                VolumeIcon.Glyph = "\uE992";
            }
            else if (value < 33)
            {
                VolumeIcon.Glyph = "\uE993";
            }
            else if (value < 66)
            {
                VolumeIcon.Glyph = "\uE994";
            }
            else
            {
                VolumeIcon.Glyph = "\uE995";
            }
        }

        private void UpdateShuffleVisual(bool enabled)
        {
            ShuffleIcon.Foreground = (Brush)Application.Current.Resources[enabled
                ? "SystemControlHighlightAccentBrush"
                : "SystemControlForegroundBaseMediumBrush"];
        }

        private void UpdateRepeatVisual(int mode)
        {
            bool active = mode > 0;
            RepeatIcon.Foreground = (Brush)Application.Current.Resources[active
                ? "SystemControlHighlightAccentBrush"
                : "SystemControlForegroundBaseMediumBrush"];

            switch (mode)
            {
                case 0: RepeatIcon.Glyph = "\uE8EE"; break;
                case 1: RepeatIcon.Glyph = "\uE8EE"; break;
                case 2: RepeatIcon.Glyph = "\uE8ED"; break;
            }
        }
    }
}