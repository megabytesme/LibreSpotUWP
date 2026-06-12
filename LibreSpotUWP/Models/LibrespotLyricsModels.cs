using System;
using System.Collections.Generic;

namespace LibreSpotUWP.Models
{
    public sealed class LibrespotLyricsColorsData
    {
        public int Background { get; set; }
        public int Text { get; set; }
        public int HighlightText { get; set; }
    }

    public sealed class LibrespotLyricsLineData
    {
        public string StartTimeMs { get; set; }
        public string EndTimeMs { get; set; }
        public string Words { get; set; }

        public long StartTimeMsValue => long.TryParse(StartTimeMs, out var value) ? value : 0;
        public long EndTimeMsValue => long.TryParse(EndTimeMs, out var value) ? value : StartTimeMsValue;
        public bool IsSpacer => string.IsNullOrWhiteSpace(Words);
        public string DisplayWords => NormalizeWords(Words);

        private static string NormalizeWords(string words)
        {
            if (string.IsNullOrEmpty(words))
                return string.Empty;

            return words
                .Replace("â™ª", "♪")
                .Replace("â™", "♪");
        }
    }

    public sealed class LibrespotLyricsData
    {
        public string Provider { get; set; }
        public string ProviderDisplayName { get; set; }
        public string Language { get; set; }
        public string SyncType { get; set; }
        public bool HasVocalRemoval { get; set; }
        public bool IsDenseTypeface { get; set; }
        public bool IsRtlLanguage { get; set; }
        public string SyncLyricsUri { get; set; }
        public List<LibrespotLyricsLineData> Lines { get; set; } = new List<LibrespotLyricsLineData>();
        public LibrespotLyricsColorsData Colors { get; set; } = new LibrespotLyricsColorsData();
    }
}
