using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;

namespace LibreSpotUWP.Controls
{
    public sealed partial class NarrowMediaControllerBar : UserControl
    {
        private IMediaService Media => App.Media;

        private string _currentTrackUri = null;
        private string _currentArtworkUri = null;
        private EventHandler<MediaState> _mediaStateChangedHandler;

        private bool _gestureTriggered = false;
        private double _gestureStartX = 0;
        private const double SwipeThreshold = 40;

        public NarrowMediaControllerBar()
        {
            InitializeComponent();

            Loaded += NarrowMediaControllerBar_Loaded;
            SizeChanged += OnSizeChanged;
            Unloaded += NarrowMediaControllerBar_Unloaded;
        }

        private void NarrowMediaControllerBar_Loaded(object sender, RoutedEventArgs e)
        {
            if (Media == null)
                return;

            if (_mediaStateChangedHandler == null)
            {
                _mediaStateChangedHandler = Media_MediaStateChanged;
                Media.MediaStateChanged += _mediaStateChangedHandler;
            }

            if (App.Downloads != null)
            {
                App.Downloads.TrackStatusChanged -= Downloads_TrackStatusChanged;
                App.Downloads.TrackStatusChanged += Downloads_TrackStatusChanged;
            }

            UpdateUI(Media.Current);
        }

        private async void Media_MediaStateChanged(object sender, MediaState state)
        {
            await Dispatcher.RunAsync(
                Windows.UI.Core.CoreDispatcherPriority.Normal,
                () => UpdateUI(state));
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateProgress(Media?.Current);
        }

        private void UpdateUI(MediaState state)
        {
            if (state == null) return;

            var trackUri = state.Metadata?.Uri ?? state.Track?.Uri;
            if (!string.Equals(trackUri, _currentTrackUri, StringComparison.OrdinalIgnoreCase))
            {
                _currentTrackUri = trackUri;
            }

            TrackTitle.Text = GetTrackTitle(state);
            TrackArtist.Text = GetTrackArtist(state);
            UpdateArtistButton(state);

            var artworkUri = GetArtworkUri(state);
            if (!string.Equals(_currentArtworkUri, artworkUri, StringComparison.OrdinalIgnoreCase))
            {
                _currentArtworkUri = artworkUri;
                AlbumArt.Source = TryCreateBitmap(artworkUri);
            }

            UpdateProgress(state);
            PlayPauseIcon.Glyph = state.IsPlaying ? "\uE769" : "\uE768";
            PersistButton.Visibility = Visibility.Visible;
            PersistProgressRing.IsActive = false;
            PersistProgressRing.Visibility = Visibility.Collapsed;
            _ = TrackAddToFlyoutHelper.UpdateTrackLikeVisualAsync(state, PersistIcon, PersistButton);
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

        private void PlayPauseButton_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private async void PersistButton_Click(object sender, RoutedEventArgs e)
        {
            await TrackAddToFlyoutHelper.HandleTrackAddToAsync(PersistButton, Media?.Current, PersistIcon);
        }

        private void PersistButton_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private void Root_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var shell = PlaybackNavigationHelper.FindShell(this);
            shell?.NavigateTo("Player");
        }

        private void TrackArtistButton_Click(object sender, RoutedEventArgs e)
        {
            var artists = GetTrackArtists(Media?.Current);
            if (artists.Count == 0)
                return;

            if (artists.Count == 1)
            {
                PlaybackNavigationHelper.NavigateToSpotifyUri(this, artists[0].Uri);
                return;
            }

            var flyout = new MenuFlyout();
            foreach (var artist in artists.Where(a => !string.IsNullOrWhiteSpace(a.Uri)))
            {
                var artistUri = artist.Uri;
                var item = new MenuFlyoutItem { Text = artist.Name };
                item.Click += (s, args) => PlaybackNavigationHelper.NavigateToSpotifyUri(this, artistUri);
                flyout.Items.Add(item);
            }

            if (flyout.Items.Count > 0)
                flyout.ShowAt(TrackArtistButton);
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

        private static BitmapImage TryCreateBitmap(string uriString)
        {
            uriString = ImageUriHelper.NormalizeImageUrl(uriString);
            return Uri.TryCreate(uriString, UriKind.Absolute, out var uri)
                ? new BitmapImage(uri)
                : null;
        }

        private async void Downloads_TrackStatusChanged(object sender, TrackDownloadStatus e)
        {
            if (Media?.Current?.Track?.Uri != e?.TrackUri)
                return;

            await Dispatcher.RunAsync(
                Windows.UI.Core.CoreDispatcherPriority.Normal,
                () => UpdateUI(Media.Current));
        }

        private void NarrowMediaControllerBar_Unloaded(object sender, RoutedEventArgs e)
        {
            if (Media != null && _mediaStateChangedHandler != null)
            {
                Media.MediaStateChanged -= _mediaStateChangedHandler;
                _mediaStateChangedHandler = null;
            }

            if (App.Downloads != null)
                App.Downloads.TrackStatusChanged -= Downloads_TrackStatusChanged;
        }

        private void UpdateArtistButton(MediaState state)
        {
            var artists = GetTrackArtists(state);
            TrackArtistButton.Visibility = string.IsNullOrWhiteSpace(GetTrackArtist(state))
                ? Visibility.Collapsed
                : Visibility.Visible;
            TrackArtistButton.IsEnabled = artists.Count > 0;
            ToolTipService.SetToolTip(
                TrackArtistButton,
                artists.Count > 1 ? "Choose an artist" : artists.FirstOrDefault()?.Name ?? GetTrackArtist(state));
        }

        private static string GetTrackTitle(MediaState state)
        {
            return FirstText(
                state?.Metadata?.Name,
                state?.Track?.Name);
        }

        private static string GetTrackArtist(MediaState state)
        {
            var metadataArtists = state?.Metadata?.Artists?
                .Select(artist => artist?.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name));

            return FirstText(
                metadataArtists == null ? null : string.Join(", ", metadataArtists),
                state?.Track?.Artist);
        }

        private static string GetArtworkUri(MediaState state)
        {
            return FirstText(
                state?.ArtworkUri,
                state?.Metadata?.Album?.Images?.FirstOrDefault()?.Url,
                state?.Track?.CoverUrl);
        }

        private static string FirstText(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return string.Empty;
        }

        private static List<AppSimpleArtist> GetTrackArtists(MediaState state)
        {
            var metadataArtists = state?.Metadata?.Artists;
            if (metadataArtists?.Count > 0)
            {
                return metadataArtists
                    .Where(a => !string.IsNullOrWhiteSpace(a?.Id))
                    .Select(a => new AppSimpleArtist
                    {
                        Id = a.Id,
                        Name = a.Name,
                        Uri = a.Uri ?? $"spotify:artist:{a.Id}"
                    })
                    .ToList();
            }

            return new List<AppSimpleArtist>();
        }
    }
}
