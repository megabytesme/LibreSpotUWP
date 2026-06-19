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
        private const string NowPlayingLyricsKey = "NowPlayingLyricsEnabled";
        private const string AudioEffectsKey = "AudioEffectsPreset";
        private const string AudioEffectsStrengthKey = "AudioEffectsStrength";
        private const string AudioEchoEffectEnabledKey = "AudioEchoEffectEnabled";
        private const string AudioReverbEffectEnabledKey = "AudioReverbEffectEnabled";
        private const string AudioLimiterEffectEnabledKey = "AudioLimiterEffectEnabled";
        private const string AudioOutputDeviceIdKey = "AudioOutputDeviceId";
        private const string SpotifyConnectDeviceIdKey = "SpotifyConnectDeviceId";
        private const string SpotifyCustomClientIdKey = "SpotifyCustomClientId";
        private const string EqualizerBandsKey = "AudioEffectsEqualizerBands";
        private const string EqualizerBandsUnitKey = "AudioEffectsEqualizerBandsUnit";
        private const string EqualizerBandsUnitDb = "Db";
        private const string RememberLastPlaybackStateKey = "RememberLastPlaybackState";
        private const string ResumeLastPlaybackIfWasPlayingKey = "ResumeLastPlaybackIfWasPlaying";
        private const string RememberLastPageKey = "RememberLastPage";
        private const string LyricsUseSpotifyThemeKey = "LyricsUseSpotifyTheme";
        public const double EqualizerMinGainDb = -18.0;
        public const double EqualizerMaxGainDb = 18.0;

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

        public static bool NowPlayingLyricsEnabled
        {
            get => !ApplicationData.Current.LocalSettings.Values.TryGetValue(NowPlayingLyricsKey, out object value) || !(value is bool enabled) || enabled;
            set => ApplicationData.Current.LocalSettings.Values[NowPlayingLyricsKey] = value;
        }

        public static string AudioEffectsPreset
        {
            get
            {
                var settings = ApplicationData.Current.LocalSettings;
                var raw = settings.Values.TryGetValue(AudioEffectsKey, out object value) ? value as string : "None";
                var normalized = NormalizeAudioEffectsPreset(raw);
                if (!string.Equals(raw, normalized, StringComparison.Ordinal))
                    settings.Values[AudioEffectsKey] = normalized;

                return normalized;
            }
            set => ApplicationData.Current.LocalSettings.Values[AudioEffectsKey] = NormalizeAudioEffectsPreset(value);
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

        public static bool AudioEchoEffectEnabled
        {
            get => ApplicationData.Current.LocalSettings.Values.TryGetValue(AudioEchoEffectEnabledKey, out object value) && value is bool enabled && enabled;
            set => ApplicationData.Current.LocalSettings.Values[AudioEchoEffectEnabledKey] = value;
        }

        public static bool AudioReverbEffectEnabled
        {
            get => ApplicationData.Current.LocalSettings.Values.TryGetValue(AudioReverbEffectEnabledKey, out object value) && value is bool enabled && enabled;
            set => ApplicationData.Current.LocalSettings.Values[AudioReverbEffectEnabledKey] = value;
        }

        public static bool AudioLimiterEffectEnabled
        {
            get => ApplicationData.Current.LocalSettings.Values.TryGetValue(AudioLimiterEffectEnabledKey, out object value) && value is bool enabled && enabled;
            set => ApplicationData.Current.LocalSettings.Values[AudioLimiterEffectEnabledKey] = value;
        }

        public static string AudioOutputDeviceId
        {
            get => ApplicationData.Current.LocalSettings.Values.TryGetValue(AudioOutputDeviceIdKey, out object value) ? value as string : string.Empty;
            set => ApplicationData.Current.LocalSettings.Values[AudioOutputDeviceIdKey] = value ?? string.Empty;
        }

        public static string SpotifyConnectDeviceId
        {
            get => ApplicationData.Current.LocalSettings.Values.TryGetValue(SpotifyConnectDeviceIdKey, out object value) ? value as string : string.Empty;
            set => ApplicationData.Current.LocalSettings.Values[SpotifyConnectDeviceIdKey] = value ?? string.Empty;
        }

        public static string SpotifyCustomClientId
        {
            get => ApplicationData.Current.LocalSettings.Values.TryGetValue(SpotifyCustomClientIdKey, out object value) ? value as string : string.Empty;
            set => ApplicationData.Current.LocalSettings.Values[SpotifyCustomClientIdKey] = value?.Trim() ?? string.Empty;
        }

        public static bool HasSpotifyCustomClientId =>
            !string.IsNullOrWhiteSpace(SpotifyCustomClientId);

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

        public static bool LyricsUseSpotifyTheme
        {
            get => ApplicationData.Current.LocalSettings.Values.TryGetValue(LyricsUseSpotifyThemeKey, out object value) && value is bool enabled && enabled;
            set => ApplicationData.Current.LocalSettings.Values[LyricsUseSpotifyThemeKey] = value;
        }

        public static double[] GetEqualizerBandGains()
        {
            var settings = ApplicationData.Current.LocalSettings;
            var raw = settings.Values.TryGetValue(EqualizerBandsKey, out object value)
                ? value as string
                : null;
            var storedUnit = settings.Values.TryGetValue(EqualizerBandsUnitKey, out object unitValue)
                ? unitValue as string
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

            if (!string.Equals(storedUnit, EqualizerBandsUnitDb, StringComparison.Ordinal) && values.Count > 0)
            {
                values = values.Select(ConvertLegacyEqualizerGain).ToList();
                settings.Values[EqualizerBandsKey] = string.Join("|", values.Select(item => item.ToString(CultureInfo.InvariantCulture)));
                settings.Values[EqualizerBandsUnitKey] = EqualizerBandsUnitDb;
            }

            while (values.Count < 5)
                values.Add(0.0);

            return values.Take(5).Select(ClampBandGain).ToArray();
        }

        public static void SetEqualizerBandGains(IEnumerable<double> gains)
        {
            var items = gains == null
                ? Enumerable.Empty<double>()
                : gains.Select(ClampBandGain);

            ApplicationData.Current.LocalSettings.Values[EqualizerBandsKey] = string.Join("|", items.Select(value => value.ToString(CultureInfo.InvariantCulture)));
            ApplicationData.Current.LocalSettings.Values[EqualizerBandsUnitKey] = EqualizerBandsUnitDb;
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

            return Math.Max(EqualizerMinGainDb, Math.Min(EqualizerMaxGainDb, value));
        }

        private static double ConvertLegacyEqualizerGain(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0.0;

            const double legacyMaxGain = 0.25;
            var clampedLegacy = Math.Max(-legacyMaxGain, Math.Min(legacyMaxGain, value));
            return ClampBandGain(clampedLegacy / legacyMaxGain * EqualizerMaxGainDb);
        }

        private static string NormalizeAudioEffectsPreset(string preset)
        {
            if (string.IsNullOrWhiteSpace(preset))
                return "None";

            if (string.Equals(preset, "BassBoost", StringComparison.OrdinalIgnoreCase))
                return "BassBoost";

            if (string.Equals(preset, "VocalBoost", StringComparison.OrdinalIgnoreCase))
                return "VocalBoost";

            if (string.Equals(preset, "Warm", StringComparison.OrdinalIgnoreCase))
                return "Warm";

            if (string.Equals(preset, "Equalizer", StringComparison.OrdinalIgnoreCase))
                return "Equalizer";

            if (string.Equals(preset, "Echo", StringComparison.OrdinalIgnoreCase))
            {
                AudioEchoEffectEnabled = true;
                return "None";
            }

            if (string.Equals(preset, "Reverb", StringComparison.OrdinalIgnoreCase))
            {
                AudioReverbEffectEnabled = true;
                return "None";
            }

            if (string.Equals(preset, "Limiter", StringComparison.OrdinalIgnoreCase))
            {
                AudioLimiterEffectEnabled = true;
                return "None";
            }

            return "None";
        }
    }
}
