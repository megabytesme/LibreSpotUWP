using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using System;
using System.Threading.Tasks;
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
        private string _currentArtworkUri = null;

        public MediaControllerBar()
        {
            InitializeComponent();
            Loaded += MediaControllerBar_Loaded;
            Unloaded += MediaControllerBar_Unloaded;
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
            if (App.Downloads != null)
                App.Downloads.TrackStatusChanged += Downloads_TrackStatusChanged;

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

            if (!string.Equals(_currentArtworkUri, state.ArtworkUri, StringComparison.OrdinalIgnoreCase))
            {
                _currentArtworkUri = state.ArtworkUri;
                AlbumArt.Source = TryCreateBitmap(state.ArtworkUri);
            }

            if (!_draggingPosition)
            {
                PositionSlider.Maximum = state.DurationMs;
                PositionSlider.Value = state.PositionMs;
            }

            CurrentTime.Text = Format(state.PositionMs);
            TotalTime.Text = Format(state.DurationMs);

            PlayPauseIcon.Symbol = state.IsPlaying ? Symbol.Pause : Symbol.Play;
            var downloadState = App.Downloads?.GetTrackStatus(state.Track?.Uri)?.State ?? DownloadTrackState.Idle;
            var isDownloading = downloadState == DownloadTrackState.Queued || downloadState == DownloadTrackState.Downloading;
            PersistButton.IsEnabled = state.Track != null && !isDownloading;
            PersistButton.Visibility = isDownloading ? Visibility.Collapsed : Visibility.Visible;
            PersistProgressRing.IsActive = isDownloading;
            PersistProgressRing.Visibility = isDownloading ? Visibility.Visible : Visibility.Collapsed;
            PersistIcon.Glyph = (state.IsCurrentTrackPersisted || downloadState == DownloadTrackState.Completed) ? "\uE738" : "\uE710";
            ToolTipService.SetToolTip(
                PersistButton,
                state.IsCurrentTrackPersisted ? "Remove from downloads" : "Download this track");

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

        private async void PersistButton_Click(object sender, RoutedEventArgs e)
        {
            await ToggleCurrentTrackPersistenceAsync();
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

        private Task ToggleCurrentTrackPersistenceAsync()
        {
            if (_media?.Current == null)
                return Task.CompletedTask;

            return _media.SetCurrentTrackPersistedAsync(!_media.Current.IsCurrentTrackPersisted);
        }

        private async void Downloads_TrackStatusChanged(object sender, TrackDownloadStatus e)
        {
            if (_media?.Current?.Track?.Uri != e?.TrackUri)
                return;

            await Dispatcher.RunAsync(
                Windows.UI.Core.CoreDispatcherPriority.Normal,
                () => UpdateUI(_media.Current));
        }

        private void MediaControllerBar_Unloaded(object sender, RoutedEventArgs e)
        {
            if (App.Downloads != null)
                App.Downloads.TrackStatusChanged -= Downloads_TrackStatusChanged;
        }

        private static BitmapImage TryCreateBitmap(string uriString)
        {
            return Uri.TryCreate(uriString, UriKind.Absolute, out var uri)
                ? new BitmapImage(uri)
                : null;
        }
    }
}
