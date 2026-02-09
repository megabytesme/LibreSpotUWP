using System;

namespace LibreSpotUWP.Models
{
    public sealed class MediaState
    {
        public LibrespotPlaybackState PlaybackState { get; set; }
        public LibrespotTrackInfo Track { get; set; }
        public SpotifyAPI.Web.FullTrack Metadata { get; set; }
        public ushort Volume { get; set; }

        public MediaState Clone()
        {
            return new MediaState
            {
                PlaybackState = this.PlaybackState,
                Track = this.Track,
                Metadata = this.Metadata,
                Volume = this.Volume
            };
        }
    }
}