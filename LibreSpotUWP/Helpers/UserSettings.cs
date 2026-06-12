using LibreSpotUWP.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Windows.Storage;

namespace LibreSpotUWP.Helpers
{
    public static class UserSettings
    {
        private const string HomeOrganizationModeKey = "HomeOrganizationMode";
        private const string HomeSectionOrderKey = "HomeSectionOrder";
        private const string LyricsAutoScrollKey = "LyricsAutoScrollEnabled";
        private const string AudioEffectsKey = "AudioEffectsPreset";
        private const string AudioEffectsStrengthKey = "AudioEffectsStrength";
        private const string EqualizerBandsKey = "AudioEffectsEqualizerBands";
        private const string RememberLastPlaybackStateKey = "RememberLastPlaybackState";
        private const string ResumeLastPlaybackIfWasPlayingKey = "ResumeLastPlaybackIfWasPlaying";
        private const string RememberLastPageKey = "RememberLastPage";

        public static readonly string[] DefaultHomeSectionOrder =
        {
            "Recently Played Playlists",
            "Recently Played Albums",
            "Recently Played Artists",
            "Recently Played Tracks",
            "Your Playlists",
            "Top Artists",
            "Top Tracks",
            "Saved Albums",
            "Artists You Follow",
            "Albums From Your Top Artists",
            "Albums You Started",
            "Downloaded Playlists",
            "Downloaded Albums",
            "Downloaded Songs",
            "Mixed For You"
        };

        public static HomeOrganizationMode HomeOrganizationMode
        {
            get => (HomeOrganizationMode)(ApplicationData.Current.LocalSettings.Values.TryGetValue(HomeOrganizationModeKey, out object value) ? (int)value : 0);
            set => ApplicationData.Current.LocalSettings.Values[HomeOrganizationModeKey] = (int)value;
        }

        public static bool LyricsAutoScrollEnabled
        {
            get => !ApplicationData.Current.LocalSettings.Values.TryGetValue(LyricsAutoScrollKey, out object value) || !(value is bool enabled) || enabled;
            set => ApplicationData.Current.LocalSettings.Values[LyricsAutoScrollKey] = value;
        }

        public static string AudioEffectsPreset
        {
            get => ApplicationData.Current.LocalSettings.Values.TryGetValue(AudioEffectsKey, out object value) ? value as string : "None";
            set => ApplicationData.Current.LocalSettings.Values[AudioEffectsKey] = value ?? "None";
        }

        public static double AudioEffectsStrength
        {
            get
            {
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(AudioEffectsStrengthKey, out object value))
                {
                    if (value is double storedDouble)
                        return Clamp01(storedDouble);

                    if (value is float storedFloat)
                        return Clamp01(storedFloat);

                    if (value is int storedInt)
                        return Clamp01(storedInt / 100.0);
                }

                return 1.0;
            }
            set => ApplicationData.Current.LocalSettings.Values[AudioEffectsStrengthKey] = Clamp01(value);
        }

        public static bool RememberLastPlaybackState
        {
            get => !ApplicationData.Current.LocalSettings.Values.TryGetValue(RememberLastPlaybackStateKey, out object value) || !(value is bool enabled) || enabled;
            set => ApplicationData.Current.LocalSettings.Values[RememberLastPlaybackStateKey] = value;
        }

        public static bool RememberLastPage
        {
            get => ApplicationData.Current.LocalSettings.Values.TryGetValue(RememberLastPageKey, out object value) && value is bool enabled && enabled;
            set => ApplicationData.Current.LocalSettings.Values[RememberLastPageKey] = value;
        }

        public static bool ResumeLastPlaybackIfWasPlaying
        {
            get => ApplicationData.Current.LocalSettings.Values.TryGetValue(ResumeLastPlaybackIfWasPlayingKey, out object value) && value is bool enabled && enabled;
            set => ApplicationData.Current.LocalSettings.Values[ResumeLastPlaybackIfWasPlayingKey] = value;
        }

        public static double[] GetEqualizerBandGains()
        {
            var raw = ApplicationData.Current.LocalSettings.Values.TryGetValue(EqualizerBandsKey, out object value)
                ? value as string
                : null;

            var values = new List<double>();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                foreach (var item in raw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                        values.Add(parsed);
                }
            }

            while (values.Count < 5)
                values.Add(0.0);

            return values.Take(5).ToArray();
        }

        public static void SetEqualizerBandGains(IEnumerable<double> gains)
        {
            var items = gains == null
                ? Enumerable.Empty<double>()
                : gains.Select(ClampBandGain);

            ApplicationData.Current.LocalSettings.Values[EqualizerBandsKey] = string.Join("|", items.Select(value => value.ToString(CultureInfo.InvariantCulture)));
        }

        public static string[] GetHomeSectionOrder()
        {
            var raw = ApplicationData.Current.LocalSettings.Values.TryGetValue(HomeSectionOrderKey, out object value)
                ? value as string
                : null;

            if (string.IsNullOrWhiteSpace(raw))
                return DefaultHomeSectionOrder.ToArray();

            var items = raw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var section in DefaultHomeSectionOrder)
            {
                if (!items.Any(item => string.Equals(item, section, StringComparison.OrdinalIgnoreCase)))
                    items.Add(section);
            }

            return items.ToArray();
        }

        public static void SetHomeSectionOrder(IEnumerable<string> order)
        {
            var items = order == null
                ? new List<string>()
                : order.Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            foreach (var section in DefaultHomeSectionOrder)
            {
                if (!items.Any(item => string.Equals(item, section, StringComparison.OrdinalIgnoreCase)))
                    items.Add(section);
            }

            ApplicationData.Current.LocalSettings.Values[HomeSectionOrderKey] = string.Join("|", items);
        }

        public static void ResetHomeSectionOrder()
        {
            SetHomeSectionOrder(DefaultHomeSectionOrder);
        }

        private static double Clamp01(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 1.0;

            return Math.Max(0.0, Math.Min(1.0, value));
        }

        private static double ClampBandGain(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0.0;

            return Math.Max(-0.25, Math.Min(0.25, value));
        }
    }
}
