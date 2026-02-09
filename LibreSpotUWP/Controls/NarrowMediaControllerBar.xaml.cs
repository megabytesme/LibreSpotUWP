using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;

namespace LibreSpotUWP.Controls
{
    public sealed partial class NarrowMediaControllerBar : UserControl
    {
        private IMediaService Media => App.Media;

        private bool _gestureTriggered = false;
        private double _gestureStartX = 0;
        private const double SwipeThreshold = 40;

        public NarrowMediaControllerBar()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (Media == null)
                return;

            Media.MediaStateChanged += (s, state) =>
            {
                Dispatcher.RunAsync(
                    Windows.UI.Core.CoreDispatcherPriority.Normal,
                    () => UpdateUI(state));
            };

            UpdateUI(Media.Current);
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateProgress(Media?.Current);
        }

        private void UpdateUI(MediaState state)
        {
            if (state == null)
                return;

            TrackTitle.Text = state.Track?.Name ?? "";
            TrackArtist.Text = state.Track?.Artist ?? "";

            if (state.Metadata?.Album?.Images?.Count > 0)
                AlbumArt.Source = new BitmapImage(new Uri(state.Metadata.Album.Images[0].Url));

            UpdateProgress(state);

            PlayPauseIcon.Symbol = state.IsPlaying ? Symbol.Pause : Symbol.Play;
        }

        private void UpdateProgress(MediaState state)
        {
            if (state == null || state.DurationMs == 0)
            {
                ProgressFill.Width = 0;
                return;
            }

            double pct = (double)state.PositionMs / state.DurationMs;
            ProgressFill.Width = pct * ActualWidth;
        }

        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (Media.Current.IsPlaying)
                await Media.PauseAsync();
            else
                await Media.ResumeAsync();
        }

        private void Root_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // todo
        }

        private void Root_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
        {
            _gestureTriggered = false;
            _gestureStartX = e.Position.X;
        }

        private void Root_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            if (_gestureTriggered)
                return;

            double deltaX = e.Position.X - _gestureStartX;

            if (deltaX > SwipeThreshold)
            {
                _gestureTriggered = true;
                Media?.Next();
            }
            else if (deltaX < -SwipeThreshold)
            {
                _gestureTriggered = true;
                Media?.Previous();
            }
        }

        private void Root_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            _gestureTriggered = false;
        }
    }
}