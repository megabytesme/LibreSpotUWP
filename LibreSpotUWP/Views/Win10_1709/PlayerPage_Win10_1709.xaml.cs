using LibreSpotUWP.Controls;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using LibreSpotUWP.Helpers;
using LibreSpotUWP.Services;
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

namespace LibreSpotUWP.Views.Win10_1709
{
    public sealed partial class PlayerPage_Win10_1709 : Page
    {
        private IMediaService Media => App.Media;

        private readonly PositionSeekInteraction _positionSeekInteraction = new PositionSeekInteraction();
        private string _currentTrackUri = null;
        private string _currentArtworkUri = null;
        private uint _lastUpdateSec = uint.MaxValue;
        private DataTransferManager _dataTransferManager;
        private NowPlayingLyricsPresenter _lyricsPresenter;
        private bool _loadingOutputDevices;
        private bool _loadingSpotifyConnectDevices;
        private bool _spotifyConnectDropdownOpen;
        private bool _spotifyConnectRefreshPending;

        public PlayerPage_Win10_1709()
        {
            this.InitializeComponent();
            _lyricsPresenter = new NowPlayingLyricsPresenter(CurrentLyricPreview, CurrentLyricText);
            this.Loaded += PlayerPage_Loaded;
            this.Unloaded += PlayerPage_Unloaded;
        }

        private void PlayerPage_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateAlbumArtLayout(new Windows.Foundation.Size(ActualWidth, ActualHeight));
            _dataTransferManager = DataTransferManager.GetForCurrentView();
            _dataTransferManager.DataRequested += DataTransferManager_DataRequested;
            ShowCurrentLyricToggle.IsOn = UserSettings.NowPlayingLyricsEnabled;
            _ = LoadOutputDevicesAsync();
            _ = LoadSpotifyConnectDevicesAsync();

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

        private void PlayerPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateAlbumArtLayout(e.NewSize);
        }

        private void OnMediaStateChanged(object sender, MediaState state)
        {
            UiWorkScheduler.RunLatest(this, Dispatcher, () => UpdateUI(state));
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
            _lyricsPresenter?.Update(state);

            if (!string.Equals(_currentArtworkUri, state.ArtworkUri, StringComparison.OrdinalIgnoreCase))
            {
                _currentArtworkUri = state.ArtworkUri;
                AlbumArt.Source = TryCreateBitmap(state.ArtworkUri);
            }

            PlayPauseIcon.Symbol = state.IsPlaying ? Symbol.Pause : Symbol.Play;
            PersistButton.Visibility = Visibility.Visible;
            PersistProgressRing.IsActive = false;
            PersistProgressRing.Visibility = Visibility.Collapsed;
            _ = TrackAddToFlyoutHelper.UpdateTrackLikeVisualAsync(state, PersistIcon, PersistButton);
            CacheIndicator.Visibility = Visibility.Collapsed;
            UpdateCacheStatus(state);

            UpdateShuffleVisual(state.Shuffle);
            UpdateRepeatVisual(state.RepeatMode);

            VolumeSlider.ValueChanged -= VolumeSlider_ValueChanged;
            double volPercent = state.Volume * 100.0 / 65535.0;
            VolumeSlider.Value = volPercent;
            UpdateVolumeVisual(volPercent);
            VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
            UpdateSpotifyConnectSelection(state);

            uint currentSec = state.PositionMs / 1000;
            if (currentSec != _lastUpdateSec && !_positionSeekInteraction.IsDragging)
            {
                _lastUpdateSec = currentSec;

                if (PositionSlider.Maximum != state.DurationMs)
                {
                    PositionSlider.Maximum = state.DurationMs;
                }

                PositionSlider.Value = state.PositionMs;
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
            _positionSeekInteraction.BeginDrag();
        }

        private void PositionSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            CommitPositionSeek();
        }

        private void PositionSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            CommitPositionSeek();
        }

        private void PositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_positionSeekInteraction.IsDragging)
            {
                ElapsedTime.Text = Format((uint)e.NewValue);
            }
        }

        private void CommitPositionSeek()
        {
            if (PositionSlider == null)
                return;

            uint positionMs;
            if (_positionSeekInteraction.TryCommit((uint)PositionSlider.Value, out positionMs))
                Media.Seek(positionMs);
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
            await TrackAddToFlyoutHelper.HandleTrackAddToAsync(PersistButton, Media?.Current, PersistIcon);
        }

        private void ShareButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Media?.Current?.Track?.Uri))
                return;

            DataTransferManager.ShowShareUI();
        }

        private void LyricsButton_Click(object sender, RoutedEventArgs e)
        {
            var frame = Window.Current.Content as Frame;
            var mainPage = frame?.Content as LibreSpotUWP.MainPage;
            mainPage?.NavigateTo("Lyrics");
        }

        private void ShowCurrentLyricToggle_Toggled(object sender, RoutedEventArgs e)
        {
            UserSettings.NowPlayingLyricsEnabled = ShowCurrentLyricToggle.IsOn;
            _lyricsPresenter?.Update(Media?.Current);
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
            if (Media?.Current == null || Media.Current.IsOffline)
                return;

            await Media.RefreshCurrentTrackMetadataAsync();
        }

        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            Media?.SetVolumeDebounced(e.NewValue);
            UpdateVolumeVisual(e.NewValue);
        }

        private async Task LoadOutputDevicesAsync()
        {
            if (Media == null || OutputDeviceComboBox == null)
                return;

            _loadingOutputDevices = true;
            try
            {
                var devices = await Media.GetAudioOutputDevicesAsync();
                OutputDeviceComboBox.ItemsSource = devices;
                var selected = devices.FirstOrDefault(device => string.Equals(device.Id, Media.CurrentAudioOutputDeviceId, StringComparison.Ordinal))
                    ?? devices.FirstOrDefault();
                OutputDeviceComboBox.SelectedItem = selected;
            }
            finally
            {
                _loadingOutputDevices = false;
            }
        }

        private async void OutputDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingOutputDevices || !(OutputDeviceComboBox.SelectedItem is AudioOutputDeviceInfo device) || Media == null)
                return;

            await Media.SetAudioOutputDeviceAsync(device.Id);
        }

        private async Task LoadSpotifyConnectDevicesAsync()
        {
            if (Media == null || SpotifyConnectDeviceComboBox == null)
                return;

            _loadingSpotifyConnectDevices = true;
            try
            {
                var devices = await Media.GetSpotifyConnectDevicesAsync();
                if (_spotifyConnectDropdownOpen)
                {
                    _spotifyConnectRefreshPending = true;
                    return;
                }

                SpotifyConnectDeviceComboBox.ItemsSource = devices;
                SelectSpotifyConnectDevice(devices, Media.CurrentSpotifyConnectDeviceId);
            }
            finally
            {
                _loadingSpotifyConnectDevices = false;
            }
        }

        private void UpdateSpotifyConnectSelection(MediaState state)
        {
            if (_loadingSpotifyConnectDevices || _spotifyConnectDropdownOpen || state == null || SpotifyConnectDeviceComboBox?.ItemsSource == null)
                return;

            var devices = SpotifyConnectDeviceComboBox.ItemsSource as IEnumerable<SpotifyConnectDeviceInfo>;
            if (devices == null)
                return;

            if (!devices.Any(device => string.Equals(device.Id, state.SpotifyConnectDeviceId, StringComparison.OrdinalIgnoreCase)))
            {
                _ = LoadSpotifyConnectDevicesAsync();
                return;
            }

            SelectSpotifyConnectDevice(devices, state.SpotifyConnectDeviceId);
        }

        private void SelectSpotifyConnectDevice(IEnumerable<SpotifyConnectDeviceInfo> devices, string deviceId)
        {
            if (_spotifyConnectDropdownOpen || devices == null)
                return;

            var selected = devices.FirstOrDefault(device => string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault();
            if (selected == null || ReferenceEquals(SpotifyConnectDeviceComboBox.SelectedItem, selected))
                return;

            _loadingSpotifyConnectDevices = true;
            SpotifyConnectDeviceComboBox.SelectedItem = selected;
            _loadingSpotifyConnectDevices = false;
        }

        private async void SpotifyConnectDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingSpotifyConnectDevices || !(SpotifyConnectDeviceComboBox.SelectedItem is SpotifyConnectDeviceInfo device) || Media == null)
                return;

            await Media.SetSpotifyConnectDeviceAsync(device.Id);
            await LoadSpotifyConnectDevicesAsync();
        }

        private async void SpotifyConnectDeviceComboBox_DropDownOpened(object sender, object e)
        {
            _spotifyConnectDropdownOpen = true;
        }

        private void SpotifyConnectDeviceComboBox_DropDownClosed(object sender, object e)
        {
            _spotifyConnectDropdownOpen = false;
            if (!_spotifyConnectRefreshPending)
                return;

            _spotifyConnectRefreshPending = false;
            _ = LoadSpotifyConnectDevicesAsync();
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

        private void UpdateAlbumArtLayout(Windows.Foundation.Size size)
        {
            if (size.Width <= 0 || size.Height <= 0)
                return;

            var maxSide = Math.Max(160.0, Math.Min(size.Width * 0.62, size.Height * 0.46));
            AlbumArt.Width = maxSide;
            AlbumArt.Height = maxSide;
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

        private void Downloads_TrackStatusChanged(object sender, TrackDownloadStatus e)
        {
            if (Media?.Current?.Track?.Uri != e?.TrackUri)
                return;

            UiWorkScheduler.RunLatest(this, Dispatcher, () => UpdateUI(Media.Current));
        }
    }
}


