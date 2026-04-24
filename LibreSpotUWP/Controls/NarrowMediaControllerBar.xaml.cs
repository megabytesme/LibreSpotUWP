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

        private bool _gestureTriggered = false;
        private double _gestureStartX = 0;
        private const double SwipeThreshold = 40;

        public NarrowMediaControllerBar()
        {
            InitializeComponent();

            if (Media != null)
            {
                Media.MediaStateChanged += Media_MediaStateChanged;
                if (App.Downloads != null)
                    App.Downloads.TrackStatusChanged += Downloads_TrackStatusChanged;
                UpdateUI(Media.Current);
            }

            SizeChanged += OnSizeChanged;
            Unloaded += NarrowMediaControllerBar_Unloaded;
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

            if (state.Track?.Uri != _currentTrackUri)
            {
                _currentTrackUri = state.Track?.Uri;

                TrackTitle.Text = state.Track?.Name ?? "";
                TrackArtist.Text = state.Track?.Artist ?? "";
            }

            UpdateArtistButton(state);

            if (!string.Equals(_currentArtworkUri, state.ArtworkUri, StringComparison.OrdinalIgnoreCase))
            {
                _currentArtworkUri = state.ArtworkUri;
                AlbumArt.Source = TryCreateBitmap(state.ArtworkUri);
            }

            UpdateProgress(state);
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
            await ToggleCurrentTrackPersistenceAsync();
        }

        private void PersistButton_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private void Root_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var mainPage = PlaybackNavigationHelper.FindMainPage(this);
            mainPage?.NavigateTo("Player");
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

        private Task ToggleCurrentTrackPersistenceAsync()
        {
            if (Media?.Current == null)
                return Task.CompletedTask;

            return Media.SetCurrentTrackPersistedAsync(!Media.Current.IsCurrentTrackPersisted);
        }

        private static BitmapImage TryCreateBitmap(string uriString)
        {
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
            if (App.Downloads != null)
                App.Downloads.TrackStatusChanged -= Downloads_TrackStatusChanged;
        }

        private void UpdateArtistButton(MediaState state)
        {
            var artists = GetTrackArtists(state);
            TrackArtistButton.Visibility = artists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TrackArtistButton.IsEnabled = artists.Count > 0;
            ToolTipService.SetToolTip(
                TrackArtistButton,
                artists.Count > 1 ? "Choose an artist" : artists.FirstOrDefault()?.Name);
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
