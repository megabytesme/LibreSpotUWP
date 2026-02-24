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
        private string _currentTrackUri = null;
        private uint _lastUpdateSec = uint.MaxValue;

        public PlayerPage_Win10_1507()
        {
            this.InitializeComponent();
            this.Loaded += PlayerPage_Loaded;
            this.Unloaded += PlayerPage_Unloaded;
        }

        private void PlayerPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (Media != null)
            {
                Media.MediaStateChanged += OnMediaStateChanged;
                UpdateUI(Media.Current);
            }
        }

        private void PlayerPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (Media != null)
            {
                Media.MediaStateChanged -= OnMediaStateChanged;
            }
        }

        private void OnMediaStateChanged(object sender, MediaState state)
        {
            var ignore = Dispatcher.RunAsync(
                Windows.UI.Core.CoreDispatcherPriority.Normal,
                () => UpdateUI(state));
        }

        private void UpdateUI(MediaState state)
        {
            if (state == null) return;

            if (state.Track?.Uri != _currentTrackUri)
            {
                _currentTrackUri = state.Track?.Uri;

                TrackTitle.Text = state.Track?.Name ?? "";
                TrackArtist.Text = state.Track?.Artist ?? "";
                TotalTime.Text = Format(state.DurationMs);

                if (state.Metadata?.Album?.Images?.Count > 0)
                {
                    var newUri = new Uri(state.Metadata.Album.Images[0].Url);

                    if (!(AlbumArt.Source is BitmapImage existing && existing.UriSource == newUri))
                    {
                        AlbumArt.Source = new BitmapImage(newUri);
                    }
                }
                else
                {
                    AlbumArt.Source = null;
                }
            }

            PlayPauseIcon.Symbol = state.IsPlaying ? Symbol.Pause : Symbol.Play;
            
            uint currentSec = state.PositionMs / 1000;
            if (currentSec != _lastUpdateSec || _dragging)
            {
                _lastUpdateSec = currentSec;

                if (!_dragging)
                {
                    if (PositionSlider.Maximum != state.DurationMs)
                    {
                        PositionSlider.Maximum = state.DurationMs;
                    }

                    PositionSlider.Value = state.PositionMs;
                }

                ElapsedTime.Text = Format(state.PositionMs);
            }
        }

        private string Format(uint ms)
        {
            uint totalSeconds = ms / 1000;
            uint minutes = totalSeconds / 60;
            uint seconds = totalSeconds % 60;
            return $"{minutes}:{seconds:D2}";
        }

        private void PositionSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _dragging = true;
        }

        private void PositionSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _dragging = false;
            Media.Seek((uint)PositionSlider.Value);
        }

        private void PositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_dragging)
            {
                ElapsedTime.Text = Format((uint)e.NewValue);
            }
        }

        private void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            Media.Previous();
        }

        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (Media.Current == null) return;

            if (Media.Current.IsPlaying)
                await Media.PauseAsync();
            else
                await Media.ResumeAsync();
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            Media.Next();
        }
    }
}