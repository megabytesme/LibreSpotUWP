using System;

namespace LibreSpotUWP.Models
{
    public sealed class MediaState
    {
        public LibrespotPlaybackState PlaybackState { get; set; }
        public LibrespotTrackInfo Track { get; set; }
        public SpotifyAPI.Web.FullTrack Metadata { get; set; }
        public ushort Volume { get; set; }
        public uint PositionMs { get; set; }
        public uint DurationMs { get; set; }
        public bool IsSessionConnected { get; set; }
        public bool IsOffline { get; set; }
        public bool IsTrackMetadataFromCache { get; set; }
        public bool IsCurrentTrackPersisted { get; set; }
        public bool IsRecoveringOnlinePlayback { get; set; }
        public string StatusMessage { get; set; }
        public string ArtworkUri { get; set; }
        public string ContextUri { get; set; }
        public string ContextName { get; set; }
        public string SpotifyConnectDeviceId { get; set; }
        public string SpotifyConnectDeviceName { get; set; }
        public bool IsSpotifyConnectRemote { get; set; }

        public bool IsPlaying => PlaybackState == LibrespotPlaybackState.Playing;

        public bool Shuffle { get; set; }
        public int RepeatMode { get; set; }

        public MediaState Clone()
        {
            return new MediaState
            {
                PlaybackState = this.PlaybackState,
                Track = this.Track,
                Metadata = this.Metadata,
                Volume = this.Volume,
                PositionMs = this.PositionMs,
                DurationMs = this.DurationMs,
                IsSessionConnected = this.IsSessionConnected,
                IsOffline = this.IsOffline,
                IsTrackMetadataFromCache = this.IsTrackMetadataFromCache,
                IsCurrentTrackPersisted = this.IsCurrentTrackPersisted,
                IsRecoveringOnlinePlayback = this.IsRecoveringOnlinePlayback,
                StatusMessage = this.StatusMessage,
                ArtworkUri = this.ArtworkUri,
                ContextUri = this.ContextUri,
                ContextName = this.ContextName,
                SpotifyConnectDeviceId = this.SpotifyConnectDeviceId,
                SpotifyConnectDeviceName = this.SpotifyConnectDeviceName,
                IsSpotifyConnectRemote = this.IsSpotifyConnectRemote,
                Shuffle = this.Shuffle,
                RepeatMode = this.RepeatMode
            };
        }
    }
}
