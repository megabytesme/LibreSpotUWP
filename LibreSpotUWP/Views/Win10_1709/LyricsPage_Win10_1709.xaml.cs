using LibreSpotUWP.Helpers;
using LibreSpotUWP.Models;
using LibreSpotUWP.Services;
using Newtonsoft.Json;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace LibreSpotUWP.Views.Win10_1709
{
    public sealed partial class LyricsPage_Win10_1709 : Page
    {
        private readonly ObservableCollection<LibrespotLyricsLineData> _lines = new ObservableCollection<LibrespotLyricsLineData>();
        private string _currentTrackUri;
        private bool _autoScrollEnabled;
        private int _currentLineIndex = -1;
        private bool _lyricsLoadInProgress;
        private DateTime _ignoreAutoScrollDisableUntil = DateTime.MinValue;
        private DispatcherTimer _ignoreAutoScrollDisableTimer;
        private ScrollViewer _scrollViewer;
        private LibrespotLyricsData _currentLyrics;

        public LyricsPage_Win10_1709()
        {
            InitializeComponent();
            LyricsListView.ItemsSource = _lines;
            Loaded += LyricsPage_Loaded;
            SizeChanged += LyricsPage_SizeChanged;
            _ignoreAutoScrollDisableTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(900)
            };
            _ignoreAutoScrollDisableTimer.Tick += IgnoreAutoScrollDisableTimer_Tick;
        }

        private void LyricsPage_Loaded(object sender, RoutedEventArgs e)
        {
            _scrollViewer = LyricsScrollViewer;
            if (_scrollViewer != null)
                _scrollViewer.ViewChanged += LyricsScrollViewer_ViewChanged;
        }

        private void LyricsListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is LibrespotLyricsLineData)
            {
                var index = args.ItemIndex;
                UpdateItemVisual(index, index == _currentLineIndex, animate: false);
            }
        }

        private async void Media_MediaStateChanged(object sender, MediaState state)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                _ = UpdateFromStateAsync(state);
            });
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _autoScrollEnabled = UserSettings.LyricsAutoScrollEnabled;
            AutoScrollToggle.IsOn = _autoScrollEnabled;
            UpdateAutoScrollControls();

            if (App.Media != null)
            {
                App.Media.MediaStateChanged -= Media_MediaStateChanged;
                App.Media.MediaStateChanged += Media_MediaStateChanged;
            }

            await UpdateForCurrentStateAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            if (App.Media != null)
                App.Media.MediaStateChanged -= Media_MediaStateChanged;

            if (_scrollViewer != null)
                _scrollViewer.ViewChanged -= LyricsScrollViewer_ViewChanged;

            SizeChanged -= LyricsPage_SizeChanged;
        }

        private async Task UpdateForCurrentStateAsync()
        {
            await UpdateFromStateAsync(App.Media?.Current);
        }

        private async Task UpdateFromStateAsync(MediaState state)
        {
            if (state?.Track == null || string.IsNullOrWhiteSpace(state.Track.Uri))
            {
                SetStatus("No track is playing.");
                ClearLyrics();
                return;
            }

            TrackTitleText.Text = state.Track.Name ?? "Lyrics";
            TrackSubtitleText.Text = string.IsNullOrWhiteSpace(state.Track.Artist)
                ? "No artist information available."
                : state.Track.Artist;

            if (!string.Equals(state.Track.Uri, _currentTrackUri, StringComparison.OrdinalIgnoreCase))
            {
                _currentTrackUri = state.Track.Uri;
                await LoadLyricsAsync(state);
            }

            UpdateCurrentLine(state.PositionMs, animate: false);
        }

        private async Task LoadLyricsAsync(MediaState state)
        {
            if (_lyricsLoadInProgress)
                return;

            SetStatus("Loading lyrics...");
            ClearLyrics();
            _lyricsLoadInProgress = true;

            try
            {
                if (App.Librespot == null)
                {
                    SetStatus("Lyrics are unavailable right now.");
                    return;
                }

                var trackUri = state?.Track?.Uri;
                if (string.IsNullOrWhiteSpace(trackUri))
                {
                    SetStatus("Lyrics are unavailable for this track.");
                    return;
                }

                LogService.Info($"Requesting lyrics for {trackUri}.");
                var lyricsJson = await TryLoadLyricsJsonAsync(trackUri);
                var lyrics = DeserializeLyrics(lyricsJson);
                LogService.Info(
                    $"Lyrics response for {trackUri}: provider={lyrics?.Provider ?? "(null)"}, syncType={lyrics?.SyncType ?? "(null)"}, lineCount={lyrics?.Lines?.Count ?? -1}.");

                if (!string.Equals(_currentTrackUri, trackUri, StringComparison.OrdinalIgnoreCase))
                    return;

                if (!HasLyrics(lyrics))
                {
                    SetStatus("No synced lyrics were found for this track.");
                    LogService.Info(
                        $"No lyric lines returned for {trackUri}. provider={lyrics?.Provider ?? "(null)"}, syncType={lyrics?.SyncType ?? "(null)"}, hasVocalRemoval={lyrics?.HasVocalRemoval.ToString() ?? "(null)"}.");
                    return;
                }

                _currentLyrics = lyrics;
                SetStatus(string.IsNullOrWhiteSpace(lyrics.SyncType)
                    ? $"Synced lyrics loaded from {lyrics.ProviderDisplayName ?? "Spotify"}."
                    : $"Synced lyrics loaded from {lyrics.ProviderDisplayName ?? "Spotify"} ({lyrics.SyncType}).");

                foreach (var line in lyrics.Lines.Where(line => line != null))
                    _lines.Add(line);

                RefreshAllLyricsVisuals();
                UpdateCurrentLine(App.Media?.Current?.PositionMs ?? 0, animate: false);
            }
            catch (Exception ex)
            {
                SetStatus("Lyrics could not be loaded.");
                LogService.Warn($"Failed to load lyrics: {ex}");
            }
            finally
            {
                _lyricsLoadInProgress = false;
            }
        }

        private static bool HasLyrics(LibrespotLyricsData lyrics)
        {
            return lyrics != null && lyrics.Lines != null && lyrics.Lines.Count > 0;
        }

        private async Task<string> TryLoadLyricsJsonAsync(string trackUri)
        {
            if (string.IsNullOrWhiteSpace(trackUri))
                return null;

            try
            {
                return await App.Librespot.GetLyricsJsonAsync(trackUri);
            }
            catch (Exception ex)
            {
                LogService.Warn($"Lyrics lookup failed for {trackUri}: {ex}");
                return null;
            }
        }

        private static LibrespotLyricsData DeserializeLyrics(string lyricsJson)
        {
            if (string.IsNullOrWhiteSpace(lyricsJson))
                return null;

            try
            {
                var wrapper = JsonConvert.DeserializeObject<LibrespotAppDataResponse<LibrespotLyricsData>>(lyricsJson);
                return wrapper?.Data;
            }
            catch
            {
                try
                {
                    return JsonConvert.DeserializeObject<LibrespotLyricsData>(lyricsJson);
                }
                catch
                {
                    return null;
                }
            }
        }

        private void ClearLyrics()
        {
            _lines.Clear();
            _currentLineIndex = -1;
            LyricsListView.SelectedIndex = -1;
            _currentLyrics = null;
            LyricsScrollViewer.Background = null;
        }

        private void RefreshAllLyricsVisuals()
        {
            for (int i = 0; i < _lines.Count; i++)
            {
                var isCurrent = i == _currentLineIndex;
                UpdateItemVisual(i, isCurrent, animate: false);
            }
        }

        private void UpdateCurrentLine(uint positionMs, bool animate)
        {
            if (_lines.Count == 0)
                return;

            int index = -1;
            for (int i = 0; i < _lines.Count; i++)
            {
                if (positionMs >= _lines[i].StartTimeMsValue)
                    index = i;
                else
                    break;
            }

            if (index == _currentLineIndex)
                return;

            var previousIndex = _currentLineIndex;
            _currentLineIndex = index;
            LyricsListView.SelectedIndex = index;

            UpdateItemVisual(previousIndex, false, animate);
            UpdateItemVisual(index, true, animate);
            RefreshAllLyricsVisuals();

            if (_autoScrollEnabled && index >= 0 && index < _lines.Count)
                ScrollToLine(index);
        }

        private void LyricsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var line = e.ClickedItem as LibrespotLyricsLineData;
            if (line == null)
                return;

            var index = _lines.IndexOf(line);
            if (index < 0)
                return;

            var seekTo = (uint)Math.Max(0, line.StartTimeMsValue);
            App.Media?.Seek(seekTo);
            UpdateCurrentLine(seekTo, animate: true);
        }

        private void AutoScrollToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _autoScrollEnabled = AutoScrollToggle.IsOn;
            UserSettings.LyricsAutoScrollEnabled = _autoScrollEnabled;
            UpdateAutoScrollControls();

            if (_autoScrollEnabled && _currentLineIndex >= 0 && _currentLineIndex < _lines.Count)
                ScrollToLine(_currentLineIndex);
        }

        private void LyricsPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_autoScrollEnabled && _currentLineIndex >= 0 && _currentLineIndex < _lines.Count)
                ScrollToLine(_currentLineIndex);
        }

        private void ScrollToLine(int index)
        {
            if (index < 0 || index >= _lines.Count)
                return;

            var item = _lines[index];
            _ignoreAutoScrollDisableUntil = DateTime.UtcNow.AddMilliseconds(900);
            _ignoreAutoScrollDisableTimer.Stop();
            _ignoreAutoScrollDisableTimer.Start();
            _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () =>
            {
                try
                {
                    var container = LyricsListView.ContainerFromItem(item) as ListViewItem;
                    if (container == null)
                    {
                        LyricsListView.ScrollIntoView(item);
                        return;
                    }

                    CenterContainer(container);
                }
                catch (Exception ex)
                {
                    LogService.Warn($"Lyrics auto-scroll failed: {ex}");
                }
                finally
                {
                    _ignoreAutoScrollDisableUntil = DateTime.UtcNow.AddMilliseconds(900);
                }
            });
        }

        private void CenterContainer(FrameworkElement container)
        {
            if (_scrollViewer == null || container == null)
                return;

            var transform = container.TransformToVisual(_scrollViewer);
            var point = transform.TransformPoint(new Point(0, 0));
            var targetOffset = _scrollViewer.VerticalOffset + point.Y -
                               ((_scrollViewer.ViewportHeight - container.ActualHeight) / 2.0);

            targetOffset = Math.Max(0, Math.Min(targetOffset, _scrollViewer.ScrollableHeight));
            _scrollViewer.ChangeView(null, targetOffset, null, false);
        }

        private void UpdateItemVisual(int index, bool isCurrent, bool animate)
        {
            if (index < 0 || index >= _lines.Count)
                return;

            var line = _lines[index];
            var container = LyricsListView.ContainerFromIndex(index) as ListViewItem;
            if (container == null)
                return;

            var border = FindDescendant<Border>(container);
            var text = FindDescendant<TextBlock>(container);
            if (border == null || text == null)
                return;

            var useSpotifyTheme = UserSettings.LyricsUseSpotifyTheme && _currentLyrics?.Colors != null;
            var baseBrush = useSpotifyTheme
                ? BrushFromArgb(_currentLyrics.Colors.Background)
                : (SolidColorBrush)Application.Current.Resources["SystemControlBackgroundChromeMediumLowBrush"];
            var normalTextBrush = useSpotifyTheme
                ? BrushFromArgb(_currentLyrics.Colors.Text)
                : (SolidColorBrush)Application.Current.Resources["SystemControlForegroundBaseMediumBrush"];
            var currentTextBrush = useSpotifyTheme
                ? BrushFromArgb(_currentLyrics.Colors.HighlightText)
                : new SolidColorBrush(Colors.White);
            var accentBrush = (SolidColorBrush)Application.Current.Resources["SystemControlHighlightAccentBrush"];
            var inactiveBackgroundBrush = useSpotifyTheme
                ? baseBrush
                : WithOpacity(accentBrush, 0.28);
            var currentBackgroundBrush = useSpotifyTheme
                ? baseBrush
                : accentBrush;
            var inactiveOpacity = useSpotifyTheme ? 0.88 : 0.82;
            var activeOpacity = 1.0;

            if (line.IsSpacer)
            {
                border.Background = new SolidColorBrush(Colors.Transparent);
                border.Opacity = 1.0;
                text.Visibility = Visibility.Collapsed;
                border.Padding = new Thickness(0);
                border.Height = 18;
                return;
            }

            text.Visibility = Visibility.Visible;
            border.Padding = new Thickness(20, 16, 20, 16);
            border.Height = double.NaN;

            border.Background = isCurrent ? currentBackgroundBrush : inactiveBackgroundBrush;
            text.Foreground = isCurrent ? currentTextBrush : normalTextBrush;

            EnsureScale(border);
            var scale = border.RenderTransform as ScaleTransform;
            var target = isCurrent ? 1.05 : 1.0;

            if (!animate)
            {
                scale.ScaleX = target;
                scale.ScaleY = target;
                border.Opacity = isCurrent ? activeOpacity : inactiveOpacity;
                return;
            }

            AnimateScale(scale, target);
            AnimateOpacity(border, isCurrent ? activeOpacity : inactiveOpacity);
            text.Foreground = isCurrent ? currentTextBrush : normalTextBrush;
        }

        private void LyricsScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (DateTime.UtcNow < _ignoreAutoScrollDisableUntil)
                return;

            if (e.IsIntermediate && _autoScrollEnabled)
            {
                _autoScrollEnabled = false;
                AutoScrollToggle.IsOn = false;
                UserSettings.LyricsAutoScrollEnabled = false;
                UpdateAutoScrollControls();
            }
        }

        private void UpdateAutoScrollControls()
        {
            if (AutoScrollToggle != null)
                AutoScrollToggle.Visibility = _autoScrollEnabled ? Visibility.Collapsed : Visibility.Visible;
        }

        private void IgnoreAutoScrollDisableTimer_Tick(object sender, object e)
        {
            _ignoreAutoScrollDisableTimer.Stop();
            _ignoreAutoScrollDisableUntil = DateTime.MinValue;
        }

        private static SolidColorBrush BrushFromArgb(int argb)
        {
            unchecked
            {
                var a = (byte)((argb >> 24) & 0xFF);
                var r = (byte)((argb >> 16) & 0xFF);
                var g = (byte)((argb >> 8) & 0xFF);
                var b = (byte)(argb & 0xFF);
                return new SolidColorBrush(Color.FromArgb(a, r, g, b));
            }
        }

        private static SolidColorBrush WithOpacity(SolidColorBrush brush, double opacity)
        {
            if (brush == null)
                return new SolidColorBrush(Colors.Transparent);

            var color = brush.Color;
            var clamped = Math.Max(0.0, Math.Min(1.0, opacity));
            return new SolidColorBrush(Color.FromArgb((byte)(color.A * clamped), color.R, color.G, color.B));
        }

        private static void EnsureScale(Border border)
        {
            if (border.RenderTransform is ScaleTransform)
                return;

            border.RenderTransformOrigin = new Point(0.5, 0.5);
            border.RenderTransform = new ScaleTransform { ScaleX = 1.0, ScaleY = 1.0 };
        }

        private static void AnimateScale(ScaleTransform target, double to)
        {
            AnimateDouble(target, "(ScaleTransform.ScaleX)", to);
            AnimateDouble(target, "(ScaleTransform.ScaleY)", to);
        }

        private static void AnimateOpacity(UIElement target, double to)
        {
            AnimateDouble(target, "(UIElement.Opacity)", to);
        }

        private static void AnimateDouble(DependencyObject target, string propertyPath, double to)
        {
            var animation = new DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, propertyPath);
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        private void SetStatus(string text)
        {
            StatusText.Text = text ?? string.Empty;
        }

        private sealed class LibrespotAppDataResponse<T>
        {
            public string Kind { get; set; }
            public T Data { get; set; }
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null)
                return null;

            var count = Windows.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = Windows.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                    return match;

                var descendant = FindDescendant<T>(child);
                if (descendant != null)
                    return descendant;
            }

            return null;
        }
    }
}


