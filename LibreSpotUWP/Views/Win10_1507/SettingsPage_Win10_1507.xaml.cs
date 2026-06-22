using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using LibreSpotUWP.Services;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Storage;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Navigation;

namespace LibreSpotUWP.Views.Win10_1507
{
    public sealed partial class SettingsPage_Win10_1507 : Page
    {
        private readonly ObservableCollection<string> _homeSectionOrder = new ObservableCollection<string>();
        private readonly DispatcherTimer _equalizerApplyDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        private IMediaService _media;
        private ISpotifyAuthService _auth;
        private EventHandler<AuthState> _authStateChangedHandler;

        protected bool _loading = true;
        protected bool _suppressAppearanceChange;

        public SettingsPage_Win10_1507()
        {
            InitializeComponent();
            _equalizerApplyDebounceTimer.Tick += EqualizerApplyDebounceTimer_Tick;
            Loaded += SettingsPage_Loaded;
        }

        private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            _media = App.Media;
            _auth = App.SpotifyAuth;
            SpotifyCustomClientIdTextBox.Text = UserSettings.SpotifyCustomClientId;
            SyncAppearanceRadioSelection();
            OfflineModeToggle.IsOn = ConnectivityHelper.IsManualOfflineModeEnabled();
            LyricsThemeToggle.IsOn = UserSettings.LyricsUseSpotifyTheme;
            LoadLiveTileSettings();
            RememberLastPlaybackToggle.IsOn = UserSettings.RememberLastPlaybackState;
            ResumeLastPlaybackToggle.IsOn = UserSettings.ResumeLastPlaybackIfWasPlaying;
            var resumeVisible = RememberLastPlaybackToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
            ResumeLastPlaybackToggle.Visibility = resumeVisible;
            ResumeLastPlaybackDescription.Visibility = resumeVisible;
            RememberLastPageToggle.IsOn = UserSettings.RememberLastPage;
            SelectAudioEffectsPreset(UserSettings.AudioEffectsPreset);
            EchoEffectToggle.IsOn = UserSettings.AudioEchoEffectEnabled;
            ReverbEffectToggle.IsOn = UserSettings.AudioReverbEffectEnabled;
            LimiterEffectToggle.IsOn = UserSettings.AudioLimiterEffectEnabled;
            LoadAudioEffectSettings();
            LoadHomeOrderSettings();

            if (_auth != null)
            {
                if (_authStateChangedHandler == null)
                    _authStateChangedHandler = (s, state) => RunOnUI(() => UpdateSpotifyApiStatus(state));

                _auth.AuthStateChanged -= _authStateChangedHandler;
                _auth.AuthStateChanged += _authStateChangedHandler;
            }

            UpdateLibrespotStatus(App.Librespot?.Session);
            UpdateSpotifyApiStatus(_auth?.Current);
            _ = RefreshStorageStatusAsync();
            _loading = false;
        }

        private void SyncAppearanceRadioSelection()
        {
            var selectedTag = ModeToTag(AppearanceService.Current);
            _suppressAppearanceChange = true;

            foreach (var radioButton in AppearanceStackPanel.Children.OfType<RadioButton>())
            {
                radioButton.Visibility = IsAppearanceOptionSupported(radioButton.Tag as string)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                radioButton.IsChecked = string.Equals(radioButton.Tag as string, selectedTag, StringComparison.Ordinal);
            }

            _suppressAppearanceChange = false;
        }

        private static bool IsAppearanceOptionSupported(string tag)
        {
            if (tag == "11")
                return OSHelper.SupportsWin11Appearance;

            if (tag == "1709")
                return OSHelper.SupportsWin10_1709Appearance;

            return OSHelper.SupportsWin10_1507Appearance;
        }

        private async void OfflineModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            ConnectivityHelper.SetManualOfflineModeEnabled(OfflineModeToggle.IsOn);

            if (App.Librespot == null)
                return;

            if (OfflineModeToggle.IsOn)
            {
                await App.Librespot.DisconnectAsync();
                UpdateLibrespotStatus(App.Librespot.Session);
                return;
            }

            try
            {
                var token = _auth == null
                    ? null
                    : await _auth.EnsureValidAccessTokenAsync();
                if (!string.IsNullOrWhiteSpace(token))
                    await App.Librespot.ConnectWithAccessTokenAsync(token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to reconnect librespot after leaving offline mode: {ex}");
            }

            UpdateLibrespotStatus(App.Librespot.Session);
        }

        private void RememberLastPlaybackToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            UserSettings.RememberLastPlaybackState = RememberLastPlaybackToggle.IsOn;
            var resumeVisible = RememberLastPlaybackToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
            ResumeLastPlaybackToggle.Visibility = resumeVisible;
            ResumeLastPlaybackDescription.Visibility = resumeVisible;

            if (!RememberLastPlaybackToggle.IsOn)
            {
                ResumeLastPlaybackToggle.IsOn = false;
                UserSettings.ResumeLastPlaybackIfWasPlaying = false;
            }
        }

        private void RememberLastPageToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            UserSettings.RememberLastPage = RememberLastPageToggle.IsOn;
        }

        private void LyricsThemeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            UserSettings.LyricsUseSpotifyTheme = LyricsThemeToggle.IsOn;
        }

        private void LoadLiveTileSettings()
        {
            LiveTilesToggle.IsOn = UserSettings.LiveTilesEnabled;
            LiveTileOpenRandomToggle.IsOn = UserSettings.LiveTileOpenRandomItems;
            LiveTileNowPlayingToggle.IsOn = UserSettings.LiveTileNowPlayingEnabled;
            LiveTileRecentSongsToggle.IsOn = UserSettings.LiveTileRecentSongsEnabled;
            LiveTileRecentArtistsToggle.IsOn = UserSettings.LiveTileRecentArtistsEnabled;
            LiveTileRecentPlaylistsToggle.IsOn = UserSettings.LiveTileRecentPlaylistsEnabled;
            LiveTileRecentAlbumsToggle.IsOn = UserSettings.LiveTileRecentAlbumsEnabled;
            LiveTileRandomArtistToggle.IsOn = UserSettings.LiveTileRandomArtistEnabled;
            LiveTileRandomPlaylistToggle.IsOn = UserSettings.LiveTileRandomPlaylistEnabled;
            LiveTileRandomAlbumToggle.IsOn = UserSettings.LiveTileRandomAlbumEnabled;
            LiveTileSpotifyPlaylistToggle.IsOn = UserSettings.LiveTileSpotifyPlaylistEnabled;
            LiveTileProfileToggle.IsOn = UserSettings.LiveTileProfileEnabled;
        }

        private void LiveTileToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            UserSettings.LiveTilesEnabled = LiveTilesToggle.IsOn;
            UserSettings.LiveTileOpenRandomItems = LiveTileOpenRandomToggle.IsOn;
            UserSettings.LiveTileNowPlayingEnabled = LiveTileNowPlayingToggle.IsOn;
            UserSettings.LiveTileRecentSongsEnabled = LiveTileRecentSongsToggle.IsOn;
            UserSettings.LiveTileRecentArtistsEnabled = LiveTileRecentArtistsToggle.IsOn;
            UserSettings.LiveTileRecentPlaylistsEnabled = LiveTileRecentPlaylistsToggle.IsOn;
            UserSettings.LiveTileRecentAlbumsEnabled = LiveTileRecentAlbumsToggle.IsOn;
            UserSettings.LiveTileRandomArtistEnabled = LiveTileRandomArtistToggle.IsOn;
            UserSettings.LiveTileRandomPlaylistEnabled = LiveTileRandomPlaylistToggle.IsOn;
            UserSettings.LiveTileRandomAlbumEnabled = LiveTileRandomAlbumToggle.IsOn;
            UserSettings.LiveTileSpotifyPlaylistEnabled = LiveTileSpotifyPlaylistToggle.IsOn;
            UserSettings.LiveTileProfileEnabled = LiveTileProfileToggle.IsOn;

            App.LiveTiles?.RefreshForSettingsChanged();
        }

        private void SpotifyCustomClientIdTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading)
                return;

            UserSettings.SpotifyCustomClientId = SpotifyCustomClientIdTextBox.Text;
        }

        private void ResumeLastPlaybackToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            UserSettings.ResumeLastPlaybackIfWasPlaying = ResumeLastPlaybackToggle.IsOn;
        }

        private async void AudioEffectsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading)
                return;

            if (!(AudioEffectsComboBox.SelectedItem is ComboBoxItem selectedItem))
                return;

            var preset = selectedItem.Tag as string ?? "None";
            UserSettings.AudioEffectsPreset = preset;
            UpdateAudioEffectVisibility();

            if (_media == null)
                return;

            try
            {
                await _media.SetAudioEffectsPresetAsync(preset);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to update audio effects preset: {ex}");
            }
        }

        private void AudioEffectToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            UserSettings.AudioEchoEffectEnabled = EchoEffectToggle.IsOn;
            UserSettings.AudioReverbEffectEnabled = ReverbEffectToggle.IsOn;
            UserSettings.AudioLimiterEffectEnabled = LimiterEffectToggle.IsOn;
            UpdateAudioEffectVisibility();
            ApplyCurrentAudioEffect();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            if (_auth != null && _authStateChangedHandler != null)
                _auth.AuthStateChanged -= _authStateChangedHandler;
        }

        protected async void AppearanceRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressAppearanceChange || _loading) return;

            if (sender is RadioButton rb && rb.Tag is string tag)
            {
                var selectedMode = SettingsPage_Win10_1507.TagToMode(tag);

                SetAppearance(selectedMode);
            }
        }

        protected void SetAppearance(AppearanceMode mode)
        {
            AppearanceService.Set(mode);
            ApplyAppearanceWithoutRestart();
        }

        private void SelectAudioEffectsPreset(string preset)
        {
            foreach (var item in AudioEffectsComboBox.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag as string, preset, StringComparison.OrdinalIgnoreCase))
                {
                    AudioEffectsComboBox.SelectedItem = item;
                    return;
                }
            }

            AudioEffectsComboBox.SelectedIndex = 0;
        }

        private void LoadAudioEffectSettings()
        {
            ApplyEqualizerSliderRanges();

            SetEffectStrengthSlider(UserSettings.AudioEffectsStrength);

            var gains = UserSettings.GetEqualizerBandGains();
            if (gains.Length >= 5)
            {
                EqualizerLowSlider.Value = ClampSliderToRange(EqualizerLowSlider, gains[0]);
                EqualizerLowMidSlider.Value = ClampSliderToRange(EqualizerLowMidSlider, gains[1]);
                EqualizerMidSlider.Value = ClampSliderToRange(EqualizerMidSlider, gains[2]);
                EqualizerHighMidSlider.Value = ClampSliderToRange(EqualizerHighMidSlider, gains[3]);
                EqualizerHighSlider.Value = ClampSliderToRange(EqualizerHighSlider, gains[4]);
            }

            UpdateAudioEffectVisibility();
        }

        private void ApplyEqualizerSliderRanges()
        {
            var ranges = _media?.GetEqualizerBandRanges();
            if (ranges == null || ranges.Length < 5)
                return;

            ApplySliderRange(EqualizerLowSlider, ranges[0]);
            ApplySliderRange(EqualizerLowMidSlider, ranges[1]);
            ApplySliderRange(EqualizerMidSlider, ranges[2]);
            ApplySliderRange(EqualizerHighMidSlider, ranges[3]);
            ApplySliderRange(EqualizerHighSlider, ranges[4]);
        }

        private static void ApplySliderRange(Slider slider, EqualizerBandRange range)
        {
            if (slider == null || range == null)
                return;

            slider.Minimum = Math.Round(range.MinimumGain, 2);
            slider.Maximum = Math.Round(range.MaximumGain, 2);
        }

        private static double ClampSliderToRange(Slider slider, double value)
        {
            return Math.Max(slider.Minimum, Math.Min(slider.Maximum, value));
        }

        private void UpdateAudioEffectVisibility()
        {
            var preset = GetSelectedPreset();
            bool showStrength = preset == "BassBoost" || preset == "VocalBoost" || preset == "Warm" || EchoEffectToggle.IsOn || ReverbEffectToggle.IsOn;

            EffectStrengthPanel.Visibility = showStrength ? Visibility.Visible : Visibility.Collapsed;
            EqualizerPanel.Visibility = string.Equals(preset, "Equalizer", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;

            EffectStrengthValueText.Text = $"Strength: {Math.Round(UserSettings.AudioEffectsStrength * 100)}%";
        }

        private string GetSelectedPreset()
        {
            return (AudioEffectsComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "None";
        }

        private void SetEffectStrengthSlider(double strength)
        {
            EffectStrengthSlider.ValueChanged -= EffectStrengthSlider_ValueChanged;
            EffectStrengthSlider.Value = Math.Max(0, Math.Min(100, strength * 100));
            EffectStrengthValueText.Text = $"Strength: {Math.Round(EffectStrengthSlider.Value)}%";
            EffectStrengthSlider.ValueChanged += EffectStrengthSlider_ValueChanged;
        }

        private void ApplyCurrentAudioEffect()
        {
            if (_media == null)
                return;

            var preset = GetSelectedPreset();
            _ = _media.SetAudioEffectsPresetAsync(preset);
        }

        private void EffectStrengthSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_loading)
                return;

            UserSettings.AudioEffectsStrength = e.NewValue / 100.0;
            EffectStrengthValueText.Text = $"Strength: {Math.Round(e.NewValue)}%";
            ApplyCurrentAudioEffect();
        }

        private void EqualizerSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_loading)
                return;

            UserSettings.SetEqualizerBandGains(new[]
            {
                EqualizerLowSlider.Value,
                EqualizerLowMidSlider.Value,
                EqualizerMidSlider.Value,
                EqualizerHighMidSlider.Value,
                EqualizerHighSlider.Value,
            });

            _equalizerApplyDebounceTimer.Stop();
            _equalizerApplyDebounceTimer.Start();
        }

        private void EqualizerApplyDebounceTimer_Tick(object sender, object e)
        {
            _equalizerApplyDebounceTimer.Stop();
            ApplyCurrentAudioEffect();
        }

        private void LoadHomeOrderSettings()
        {
            _homeSectionOrder.Clear();
            foreach (var section in UserSettings.GetHomeSectionOrder())
                _homeSectionOrder.Add(section);

            HomeOrderList.ItemsSource = _homeSectionOrder;
            HomeOrderList.SelectedIndex = _homeSectionOrder.Count > 0 ? 0 : -1;
            UpdateHomeOrderButtons();
        }

        private void HomeOrderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading)
                return;

            UpdateHomeOrderButtons();
        }

        private void UpdateHomeOrderButtons()
        {
            var index = HomeOrderList.SelectedIndex;
            HomeOrderUpButton.IsEnabled = index > 0;
            HomeOrderDownButton.IsEnabled = index >= 0 && index < _homeSectionOrder.Count - 1;
        }

        private void MoveHomeOrderSelection(int offset)
        {
            if (HomeOrderList.SelectedIndex < 0)
                return;

            int sourceIndex = HomeOrderList.SelectedIndex;
            int targetIndex = sourceIndex + offset;
            if (targetIndex < 0 || targetIndex >= _homeSectionOrder.Count)
                return;

            var item = _homeSectionOrder[sourceIndex];
            _homeSectionOrder.RemoveAt(sourceIndex);
            _homeSectionOrder.Insert(targetIndex, item);
            HomeOrderList.SelectedIndex = targetIndex;
            UserSettings.SetHomeSectionOrder(_homeSectionOrder);
            UpdateHomeOrderButtons();
        }

        private void HomeOrderUpButton_Click(object sender, RoutedEventArgs e)
        {
            MoveHomeOrderSelection(-1);
        }

        private void HomeOrderDownButton_Click(object sender, RoutedEventArgs e)
        {
            MoveHomeOrderSelection(1);
        }

        private void ResetHomeOrderButton_Click(object sender, RoutedEventArgs e)
        {
            UserSettings.ResetHomeSectionOrder();
            LoadHomeOrderSettings();
        }

        protected void ApplyAppearanceWithoutRestart()
        {
            var window = Window.Current;
            window.Content = null;
            App.ApplyThemeResources();

            var frame = new Frame();
            window.Content = frame;
            frame.Navigate(NavigationHelper.GetPageType("Shell"), null);
            window.Activate();
        }

        public static AppearanceMode TagToMode(string tag)
        {
            if (tag == "1709") return AppearanceMode.Win10_1709;
            if (tag == "11") return AppearanceMode.Win11;
            return AppearanceMode.Win10_1507;
        }

        public static string ModeToTag(AppearanceMode mode)
        {
            if (mode == AppearanceMode.Win10_1709) return "1709";
            if (mode == AppearanceMode.Win11) return "11";
            return "1507";
        }

        protected async void BtnResetAllSettings_Click(object sender, RoutedEventArgs e)
        {
            var dialog = CreateDialog();
            dialog.Title = "Reset All Settings";
            dialog.Content = "This will delete all LibreSpotUWP configuration and cached data. Continue?";
            dialog.PrimaryButtonText = "Yes";
            dialog.SecondaryButtonText = "No";

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            try
            {
                LoadingProgressRing.IsActive = true;
                LoadingOverlay.Visibility = Visibility.Visible;
                await AppResetHelper.ResetAllAppDataAsync();
#if UWP1507
                await ShowSimpleDialogAsync("Restart Required", "The app will now close. Please restart it to apply the reset.");
                Application.Current.Exit();
#else

                await ShowSimpleDialogAsync("Restarting", "The app will now restart to apply the reset.");
                await CoreApplication.RequestRestartAsync("");
#endif
            }
            catch (Exception ex) { await ShowSimpleDialogAsync("Error", ex.Message); }
            finally
            {
                LoadingProgressRing.IsActive = false;
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        protected ContentDialog CreateDialog() => new ContentDialog();

        protected async Task ShowSimpleDialogAsync(string title, string content)
        {
            var dialog = CreateDialog();
            dialog.Title = title;
            dialog.Content = content;
            dialog.PrimaryButtonText = "OK";
            await dialog.ShowAsync();
        }

        protected async void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            var scrollContent = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Inlines =
                    {
                        new Run { Text = "LibreSpotUWP", FontWeight = FontWeights.Bold, FontSize = 18 },
                        new LineBreak(),
                        new Run { Text = $"Version {OSHelper.AppVersion} ({OSHelper.PlatformFamily}) {OSHelper.Architecture}" },
                        new LineBreak(),

                        new Run { Text = $"LibreSpot 0.8.0 (MegaBytesMe fork - FFI)" },
                        new LineBreak(),

                        new Run { Text = "LibreSpot Commit: " },
                        new Hyperlink
                        {
                            NavigateUri = new Uri("https://github.com/megabytesme/librespot/tree/1b988c18657bb9b1dadbdb9b75f34034819e9a8f"),
                            Inlines = { new Run { Text = "1b988c18657bb9b1dadbdb9b75f34034819e9a8f" } }
                        },
                        new LineBreak(),
                        new LineBreak(),

                        new Run { Text = "Copyright © 2026 MegaBytesMe" },
                        new LineBreak(),
                        new LineBreak(),

                        new Run { Text = "LibreSpotUWP is a Spotify client designed with UWP in mind, powered by LibreSpot." },
                        new LineBreak(),
                        new LineBreak(),

                        new Run { Text = "Source code available on " },
                        new Hyperlink
                        {
                            NavigateUri = new Uri("https://github.com/megabytesme/LibreSpotUWP"),
                            Inlines = { new Run { Text = "GitHub" } }
                        },
                        new LineBreak(),

                        new Run { Text = "Found a bug? Report it here: " },
                        new Hyperlink
                        {
                            NavigateUri = new Uri("https://github.com/megabytesme/LibreSpotUWP/issues"),
                            Inlines = { new Run { Text = "Issue Tracker" } }
                        },
                        new LineBreak(),
                        new LineBreak(),

                        new Run { Text = "Like what you see? Consider supporting me on " },
                        new Hyperlink
                        {
                            NavigateUri = new Uri("https://ko-fi.com/megabytesme"),
                            Inlines = { new Run { Text = "Ko-fi!" } }
                        },
                        new LineBreak(),
                        new LineBreak(),

                        new Hyperlink
                        {
                            NavigateUri = new Uri("https://github.com/megabytesme/LibreSpotUWP/blob/master/LICENSE.md"),
                            Inlines = { new Run { Text = "License:" } }
                        },
                        new LineBreak(),
                        new Run { Text = "• App (Client): CC BY-NC-SA 4.0" },
                        new LineBreak(),
                        new Run { Text = "• LibreSpot (Core): MIT License" }
                    },
                    TextWrapping = TextWrapping.Wrap
                }
            };

            var dialog = CreateDialog();
            dialog.Title = "About";
            dialog.Content = scrollContent;
            dialog.PrimaryButtonText = "OK";
            await dialog.ShowAsync();
        }

        protected async void DisclaimerButton_Click(object sender, RoutedEventArgs e)
        {
            var textBlock = new TextBlock { TextWrapping = TextWrapping.Wrap };

            textBlock.Inlines.Add(new Run
            {
                Text = "This is an unofficial, third-party Spotify client. This project is "
            });
            textBlock.Inlines.Add(new Run
            {
                Text = "not affiliated with, endorsed, or sponsored by Spotify AB.",
                FontWeight = FontWeights.Bold
            });
            textBlock.Inlines.Add(new LineBreak());
            textBlock.Inlines.Add(new LineBreak());
            textBlock.Inlines.Add(new Run { Text = "\"Spotify\" is a trademark of Spotify AB." });
            textBlock.Inlines.Add(new LineBreak());
            textBlock.Inlines.Add(new LineBreak());
            textBlock.Inlines.Add(new Run
            {
                Text = "The author (MegaBytesMe) claims no responsibility for any issues that may arise from using this app."
            });

            var dialog = CreateDialog();
            dialog.Title = "Disclaimer";
            dialog.Content = new ScrollViewer { Content = textBlock };
            dialog.PrimaryButtonText = "I Understand";
            await dialog.ShowAsync();
        }

        private void RunOnUI(Action action)
            => _ = Dispatcher.RunAsync(
                Windows.UI.Core.CoreDispatcherPriority.Normal,
                () =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        LogService.Error(ex, "[SettingsPage_Win10_1507.RunOnUI] Failed.");
                        throw;
                    }
                });

        private void UpdateLibrespotStatus(LibrespotSessionState state)
        {
            if (state == null) LibrespotStatusText.Text = "Not Initialized";
            else LibrespotStatusText.Text = state.IsConnected ? $"Connected as {state.UserName}" : "Disconnected";
        }

        private void UpdateSpotifyApiStatus(AuthState state)
        {
            try
            {
                SpotifyApiStatusText.Text = (state == null || state.IsExpired) ? "Web API: Logged Out" : "Web API: Authenticated";
                LastTokenRefreshText.Text = state?.LastTokenRefreshAt.HasValue == true
                    ? $"Last token refresh: {state.LastTokenRefreshAt.Value.LocalDateTime:G}"
                    : "Last token refresh: Never";
                TokenValidUntilText.Text = state != null
                    ? $"Token valid until: {state.ExpiresAt.LocalDateTime:G}"
                    : "Token valid until: Unknown";
                RefreshTokenValidUntilText.Visibility = state?.RefreshTokenExpiresAt.HasValue == true
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                RefreshTokenValidUntilText.Text = state?.RefreshTokenExpiresAt.HasValue == true
                    ? $"Refresh token expiry: {state.RefreshTokenExpiresAt.Value.LocalDateTime:G}"
                    : string.Empty;
            }
            catch (Exception ex)
            {
                LogService.Error(ex, "[SettingsPage_Win10_1507.UpdateSpotifyApiStatus] Failed.");
                throw;
            }
        }

        private async Task RefreshStorageStatusAsync()
        {
            try
            {
                var persistedAudioPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "audio");
                var cachedAudioPath = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "audio");

                var persistedStats = await Task.Run(() => GetStorageStats(persistedAudioPath));
                var cachedStats = await Task.Run(() => GetStorageStats(cachedAudioPath));

                await Dispatcher.RunAsync(
                    Windows.UI.Core.CoreDispatcherPriority.Normal,
                    () =>
                    {
                        PersistedStorageText.Text =
                            $"Persisted audio: {FormatBytes(persistedStats.Bytes)} across {persistedStats.FileCount} song{(persistedStats.FileCount == 1 ? string.Empty : "s")}";
                        CachedStorageText.Text =
                            $"Cached audio: {FormatBytes(cachedStats.Bytes)} across {cachedStats.FileCount} song{(cachedStats.FileCount == 1 ? string.Empty : "s")}";
                    });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to refresh storage status: {ex}");
                PersistedStorageText.Text = "Persisted audio: Unavailable";
                CachedStorageText.Text = "Cached audio: Unavailable";
            }
        }

        private static (long Bytes, int FileCount) GetStorageStats(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return (0, 0);

            long bytes = 0;
            int count = 0;

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(file);
                    bytes += info.Length;
                    count++;
                }
                catch
                {
                }
            }

            return (bytes, count);
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;

            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await QrLoginHelper.TryConsumePendingScanAsync(
                _auth ?? App.SpotifyAuth,
                isBusy => RunOnUI(() =>
                {
                    LoadingProgressRing.IsActive = isBusy;
                    LoadingOverlay.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
                }));
        }

    }
}
