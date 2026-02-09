using System;

namespace LibreSpotUWP.Models
{
    public sealed class LibrespotTrackInfo
    {
        public string Uri { get; set; }
        public string Name { get; set; }
        public string Artist { get; set; }
        public string Album { get; set; }
        public string CoverUrl { get; set; }
        public TimeSpan Duration { get; set; }

        public LibrespotTrackInfo Clone()
        {
            return new LibrespotTrackInfo
            {
                Uri = this.Uri,
                Name = this.Name,
                Artist = this.Artist,
                Album = this.Album,
                CoverUrl = this.CoverUrl,
                Duration = this.Duration
            };
        }
    }
}