using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;

namespace LibreSpotUWP.Views.Win10_1507
{
    public sealed partial class PlayerPage_Win10_1507 : Page
    {
        private IMediaService Media => App.Media;
        private bool _dragging = false;

        public PlayerPage_Win10_1507()
        {
            InitializeComponent();
            Loaded += PlayerPage_Loaded;
        }

        private void PlayerPage_Loaded(object sender, RoutedEventArgs e)
        {
            Media.MediaStateChanged += (s, state) =>
            {
                Dispatcher.RunAsync(
                    Windows.UI.Core.CoreDispatcherPriority.Normal,
                    () => UpdateUI(state));
            };

            UpdateUI(Media.Current);
        }

        private void UpdateUI(MediaState state)
        {
            if (state == null)
                return;

            TrackTitle.Text = state.Track?.Name ?? "";
            TrackArtist.Text = state.Track?.Artist ?? "";

            if (state.Metadata?.Album?.Images?.Count > 0)
                AlbumArt.Source = new BitmapImage(new Uri(state.Metadata.Album.Images[0].Url));

            if (!_dragging)
            {
                PositionSlider.Maximum = state.DurationMs;
                PositionSlider.Value = state.PositionMs;
            }

            ElapsedTime.Text = Format(state.PositionMs);
            TotalTime.Text = Format(state.DurationMs);

            PlayPauseIcon.Symbol = state.IsPlaying ? Symbol.Pause : Symbol.Play;
        }

        private string Format(uint ms)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
        }

        private void PositionSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
            => _dragging = true;

        private void PositionSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _dragging = false;
            Media.Seek((uint)PositionSlider.Value);
        }

        private void PositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_dragging)
                ElapsedTime.Text = Format((uint)e.NewValue);
        }

        private void PrevButton_Click(object sender, RoutedEventArgs e)
            => Media.Previous();

        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (Media.Current.IsPlaying)
                await Media.PauseAsync();
            else
                await Media.ResumeAsync();
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
            => Media.Next();
    }
}