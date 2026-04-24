using LibreSpotUWP.Helpers;
using LibreSpotUWP.Models;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LibreSpotUWP.Views.Win10_1507
{
    public sealed partial class LyricsPage_Win10_1507 : Page
    {
        private readonly ObservableCollection<LibrespotLyricsLineData> _lines = new ObservableCollection<LibrespotLyricsLineData>();
        private string _currentTrackUri;
        private bool _autoScrollEnabled;
        private int _currentLineIndex = -1;

        public LyricsPage_Win10_1507()
        {
            InitializeComponent();
            LyricsListView.ItemsSource = _lines;
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
                await LoadLyricsAsync(state.Track.Uri);
            }

            UpdateCurrentLine(state.PositionMs);
        }

        private async Task LoadLyricsAsync(string trackUri)
        {
            SetStatus("Loading lyrics...");
            ClearLyrics();

            try
            {
                if (App.Librespot == null)
                {
                    SetStatus("Lyrics are unavailable right now.");
                    return;
                }

                var lyrics = await App.Librespot.GetLyricsAsync(trackUri);
                if (!string.Equals(_currentTrackUri, trackUri, StringComparison.OrdinalIgnoreCase))
                    return;

                if (lyrics == null || lyrics.Lines == null || lyrics.Lines.Count == 0)
                {
                    SetStatus("No synced lyrics were found for this track.");
                    return;
                }

                SetStatus(string.IsNullOrWhiteSpace(lyrics.SyncType)
                    ? $"Synced lyrics loaded from {lyrics.ProviderDisplayName ?? "Spotify"}."
                    : $"Synced lyrics loaded from {lyrics.ProviderDisplayName ?? "Spotify"} ({lyrics.SyncType}).");

                foreach (var line in lyrics.Lines.Where(line => line != null))
                    _lines.Add(line);

                UpdateCurrentLine(App.Media?.Current?.PositionMs ?? 0);
            }
            catch (Exception ex)
            {
                SetStatus("Lyrics could not be loaded.");
                System.Diagnostics.Debug.WriteLine($"Failed to load lyrics: {ex}");
            }
        }

        private void ClearLyrics()
        {
            _lines.Clear();
            _currentLineIndex = -1;
            LyricsListView.SelectedIndex = -1;
        }

        private void UpdateCurrentLine(uint positionMs)
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

            _currentLineIndex = index;
            LyricsListView.SelectedIndex = index;

            if (index >= 0 && _autoScrollEnabled && index < _lines.Count)
                LyricsListView.ScrollIntoView(_lines[index]);
        }

        private void AutoScrollToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _autoScrollEnabled = AutoScrollToggle.IsOn;
            UserSettings.LyricsAutoScrollEnabled = _autoScrollEnabled;

            if (_autoScrollEnabled && _currentLineIndex >= 0 && _currentLineIndex < _lines.Count)
                LyricsListView.ScrollIntoView(_lines[_currentLineIndex]);
        }

        private void SetStatus(string text)
        {
            StatusText.Text = text ?? string.Empty;
        }
    }
}
