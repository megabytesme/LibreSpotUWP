using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace LibreSpotUWP.Views.Win10_1507
{
    public sealed partial class PlayerPage_Win10_1507 : Page
    {
        private IMediaService Media => App.Media;

        private bool _dragging = false;
        private string _currentTrackUri = null;
        private string _currentArtworkUri = null;
        private uint _lastUpdateSec = uint.MaxValue;
        private DataTransferManager _dataTransferManager;

        public PlayerPage_Win10_1507()
        {
            this.InitializeComponent();
            this.Loaded += PlayerPage_Loaded;
            this.Unloaded += PlayerPage_Unloaded;
        }

        private void PlayerPage_Loaded(object sender, RoutedEventArgs e)
        {
            _dataTransferManager = DataTransferManager.GetForCurrentView();
            _dataTransferManager.DataRequested += DataTransferManager_DataRequested;

            if (Media != null)
            {
                Media.MediaStateChanged += OnMediaStateChanged;
                if (App.Downloads != null)
                    App.Downloads.TrackStatusChanged += Downloads_TrackStatusChanged;
                UpdateUI(Media.Current);
            }
        }

        private void PlayerPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_dataTransferManager != null)
            {
                _dataTransferManager.DataRequested -= DataTransferManager_DataRequested;
                _dataTransferManager = null;
            }

            if (Media != null)
            {
                Media.MediaStateChanged -= OnMediaStateChanged;
            }
            if (App.Downloads != null)
                App.Downloads.TrackStatusChanged -= Downloads_TrackStatusChanged;
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

            }

            UpdateArtistButton(state);
            UpdateContextButton(state);

            if (!string.Equals(_currentArtworkUri, state.ArtworkUri, StringComparison.OrdinalIgnoreCase))
            {
                _currentArtworkUri = state.ArtworkUri;
                AlbumArt.Source = TryCreateBitmap(state.ArtworkUri);
            }

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
            CacheIndicator.Visibility = Visibility.Collapsed;
            UpdateCacheStatus(state);

            UpdateShuffleVisual(state.Shuffle);
            UpdateRepeatVisual(state.RepeatMode);

            VolumeSlider.ValueChanged -= VolumeSlider_ValueChanged;
            double volPercent = state.Volume * 100.0 / 65535.0;
            VolumeSlider.Value = volPercent;
            UpdateVolumeVisual(volPercent);
            VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;

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

        private async void ShuffleButton_Click(object sender, RoutedEventArgs e) => await Media.SetShuffleAsync(!Media.Current.Shuffle);

        private async void RepeatButton_Click(object sender, RoutedEventArgs e)
        {
            int mode = (Media.Current.RepeatMode + 1) % 3;
            await Media.SetRepeatAsync(mode);
        }

        private async void PersistButton_Click(object sender, RoutedEventArgs e)
        {
            await ToggleCurrentTrackPersistenceAsync();
        }

        private void ShareButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Media?.Current?.Track?.Uri))
                return;

            DataTransferManager.ShowShareUI();
        }

        private void ContextButton_Click(object sender, RoutedEventArgs e)
        {
            var contextUri = Media?.Current?.ContextUri;
            if (string.IsNullOrWhiteSpace(contextUri))
                return;

            Helpers.PlaybackNavigationHelper.NavigateToSpotifyUri(this, contextUri);
        }

        private void TrackArtistButton_Click(object sender, RoutedEventArgs e)
        {
            var artists = GetTrackArtists(Media?.Current);
            if (artists.Count == 0)
                return;

            if (artists.Count == 1)
            {
                Helpers.PlaybackNavigationHelper.NavigateToSpotifyUri(this, artists[0].Uri);
                return;
            }

            var flyout = new MenuFlyout();
            foreach (var artist in artists.Where(a => !string.IsNullOrWhiteSpace(a.Uri)))
            {
                var artistUri = artist.Uri;
                var item = new MenuFlyoutItem { Text = artist.Name };
                item.Click += (s, args) => Helpers.PlaybackNavigationHelper.NavigateToSpotifyUri(this, artistUri);
                flyout.Items.Add(item);
            }

            if (flyout.Items.Count > 0)
                flyout.ShowAt(TrackArtistButton);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            var trackUri = Media?.Current?.Track?.Uri;
            if (string.IsNullOrWhiteSpace(trackUri) || Media.Current.IsOffline)
                return;

            await App.Media.PlayAsync(trackUri, null);
        }

        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            Media?.SetVolumeDebounced(e.NewValue);
            UpdateVolumeVisual(e.NewValue);
        }

        private void UpdateShuffleVisual(bool enabled)
        {
            ShuffleIcon.Foreground = (Brush)Application.Current.Resources[enabled ? "SystemControlHighlightAccentBrush" : "SystemControlForegroundBaseMediumBrush"];
        }

        private void UpdateRepeatVisual(int mode)
        {
            bool active = mode > 0;
            RepeatIcon.Foreground = (Brush)Application.Current.Resources[active ? "SystemControlHighlightAccentBrush" : "SystemControlForegroundBaseMediumBrush"];
            switch (mode)
            {
                case 0: RepeatIcon.Glyph = "\uE8EE"; break;
                case 1: RepeatIcon.Glyph = "\uE8EE"; break;
                case 2: RepeatIcon.Glyph = "\uE8ED"; break;
            }
        }

        private void UpdateVolumeVisual(double value)
        {
            if (value <= 0) VolumeIcon.Glyph = "\uE992";
            else if (value < 33) VolumeIcon.Glyph = "\uE993";
            else if (value < 66) VolumeIcon.Glyph = "\uE994";
            else VolumeIcon.Glyph = "\uE995";
        }

        private static string BuildCacheTooltip(MediaState state)
        {
            if (state == null)
                return null;

            if (state.IsOffline && state.IsTrackMetadataFromCache)
                return "Cached details are being used while offline.";

            if (state.IsOffline)
                return "Offline mode is active.";

            if (state.IsTrackMetadataFromCache)
                return "Cached details are currently being shown.";

            return null;
        }

        private static BitmapImage TryCreateBitmap(string uriString)
        {
            return Uri.TryCreate(uriString, UriKind.Absolute, out var uri)
                ? new BitmapImage(uri)
                : null;
        }

        private void UpdateCacheStatus(MediaState state)
        {
            var mainPage = GetMainPage();
            if (mainPage == null)
                return;

            if (state != null && (state.IsTrackMetadataFromCache || state.IsOffline))
            {
                mainPage.SetCacheStatus(
                    BuildCacheTooltip(state),
                    !state.IsOffline,
                    RefreshCurrentTrackAsync);
            }
            else
            {
                mainPage.ClearCacheStatus();
            }
        }

        private MainPage GetMainPage()
        {
            return (Window.Current.Content as Frame)?.Content as MainPage;
        }

        private async Task RefreshCurrentTrackAsync()
        {
            var trackUri = Media?.Current?.Track?.Uri;
            if (string.IsNullOrWhiteSpace(trackUri) || Media.Current.IsOffline)
                return;

            await App.Media.PlayAsync(trackUri, null);
        }

        private Task ToggleCurrentTrackPersistenceAsync()
        {
            if (Media?.Current == null)
                return Task.CompletedTask;

            return Media.SetCurrentTrackPersistedAsync(!Media.Current.IsCurrentTrackPersisted);
        }

        private void DataTransferManager_DataRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            var track = Media?.Current?.Track;
            if (track == null || string.IsNullOrWhiteSpace(track.Uri))
            {
                args.Request.FailWithDisplayText("There is no current track to share.");
                return;
            }

            var trackUrl = BuildSpotifyWebUrl(track.Uri);
            var title = string.IsNullOrWhiteSpace(track.Name) ? "Current track" : track.Name;
            var artist = string.IsNullOrWhiteSpace(track.Artist) ? "Unknown artist" : track.Artist;
            var request = args.Request;

            request.Data.Properties.Title = $"Share {title}";
            request.Data.Properties.Description = $"Share {title} by {artist}";
            request.Data.SetText($"{title} - {artist}\n{trackUrl}");

            if (Uri.TryCreate(trackUrl, UriKind.Absolute, out var uri))
                request.Data.SetWebLink(uri);
        }

        private static string BuildSpotifyWebUrl(string spotifyUri)
        {
            if (string.IsNullOrWhiteSpace(spotifyUri))
                return "https://open.spotify.com/";

            var parts = spotifyUri.Split(':');
            if (parts.Length < 3)
                return "https://open.spotify.com/";

            return $"https://open.spotify.com/{parts[1]}/{parts[2]}";
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

        private void UpdateContextButton(MediaState state)
        {
            var contextName = state?.ContextName;
            var contextUri = state?.ContextUri;
            var hasContext = !string.IsNullOrWhiteSpace(contextName) && !string.IsNullOrWhiteSpace(contextUri);

            ContextText.Text = contextName ?? string.Empty;
            ContextButton.Visibility = hasContext ? Visibility.Visible : Visibility.Collapsed;
            ContextButton.IsEnabled = hasContext;
            ToolTipService.SetToolTip(ContextButton, contextName);
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

        private async void Downloads_TrackStatusChanged(object sender, TrackDownloadStatus e)
        {
            if (Media?.Current?.Track?.Uri != e?.TrackUri)
                return;

            await Dispatcher.RunAsync(
                Windows.UI.Core.CoreDispatcherPriority.Normal,
                () => UpdateUI(Media.Current));
        }
    }
}
