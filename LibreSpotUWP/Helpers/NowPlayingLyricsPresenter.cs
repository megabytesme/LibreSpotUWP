using LibreSpotUWP.Models;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace LibreSpotUWP.Helpers
{
    public sealed class NowPlayingLyricsPresenter
    {
        private readonly Border _container;
        private readonly TextBlock _text;
        private string _trackUri;
        private LibrespotLyricsData _lyrics;
        private int _currentLineIndex = -1;
        private int _loadVersion;
        private uint _lastPositionMs;

        public NowPlayingLyricsPresenter(Border container, TextBlock text)
        {
            _container = container;
            _text = text;
        }

        public void Update(MediaState state)
        {
            if (_container == null || _text == null)
                return;

            if (!UserSettings.NowPlayingLyricsEnabled || state?.Track == null || string.IsNullOrWhiteSpace(state.Track.Uri))
            {
                Hide();
                return;
            }

            if (!string.Equals(_trackUri, state.Track.Uri, StringComparison.OrdinalIgnoreCase))
            {
                _trackUri = state.Track.Uri;
                _lyrics = null;
                _currentLineIndex = -1;
                Hide();
                _ = LoadLyricsAsync(_trackUri, ++_loadVersion);
            }

            _lastPositionMs = state.PositionMs;
            UpdateLine(state.PositionMs);
        }

        public void RefreshTheme()
        {
            if (_currentLineIndex >= 0)
                ApplyTheme();
        }

        private async Task LoadLyricsAsync(string trackUri, int version)
        {
            LibrespotLyricsData lyrics = null;
            try
            {
                lyrics = App.Librespot == null ? null : await App.Librespot.GetLyricsAsync(trackUri);
            }
            catch
            {
                lyrics = null;
            }

            var ignored = _container.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (version != _loadVersion || !string.Equals(_trackUri, trackUri, StringComparison.OrdinalIgnoreCase))
                    return;

                _lyrics = HasLyrics(lyrics) ? lyrics : null;
                _currentLineIndex = -1;
                if (_lyrics == null)
                    Hide();
                else
                    UpdateLine(_lastPositionMs);
            });
        }

        private void UpdateLine(uint positionMs)
        {
            if (!HasLyrics(_lyrics))
            {
                Hide();
                return;
            }

            var index = -1;
            for (int i = 0; i < _lyrics.Lines.Count; i++)
            {
                var line = _lyrics.Lines[i];
                if (positionMs < line.StartTimeMsValue)
                    break;

                if (!line.IsSpacer)
                    index = i;
            }

            if (index < 0)
            {
                Hide();
                return;
            }

            if (index == _currentLineIndex && _container.Visibility == Visibility.Visible)
                return;

            _currentLineIndex = index;
            _text.Text = _lyrics.Lines[index].DisplayWords;
            ApplyTheme();
            _container.Visibility = Visibility.Visible;
        }

        private void ApplyTheme()
        {
            var useSpotifyTheme = UserSettings.LyricsUseSpotifyTheme && _lyrics?.Colors != null;
            var accentBrush = Application.Current.Resources["SystemControlHighlightAccentBrush"] as SolidColorBrush
                ?? new SolidColorBrush(Colors.DodgerBlue);

            _container.Background = useSpotifyTheme
                ? BrushFromArgb(_lyrics.Colors.Background)
                : accentBrush;
            _text.Foreground = useSpotifyTheme
                ? BrushFromArgb(_lyrics.Colors.HighlightText)
                : new SolidColorBrush(Colors.White);
        }

        private void Hide()
        {
            _container.Visibility = Visibility.Collapsed;
            _text.Text = string.Empty;
        }

        private static bool HasLyrics(LibrespotLyricsData lyrics)
        {
            return lyrics?.Lines != null && lyrics.Lines.Any(line => !line.IsSpacer);
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
    }
}
