using System;

namespace LibreSpotUWP.Models
{
    public enum DownloadTrackState
    {
        Idle = 0,
        Queued = 1,
        Downloading = 2,
        Completed = 3,
        Failed = 4
    }

    public sealed class TrackDownloadStatus
    {
        public string GroupId { get; set; }
        public string TrackUri { get; set; }
        public string TrackName { get; set; }
        public DownloadTrackState State { get; set; }
        public string ErrorMessage { get; set; }
    }

    public sealed class DownloadGroupStatus
    {
        public string GroupId { get; set; }
        public string Title { get; set; }
        public int TotalTracks { get; set; }
        public int CompletedTracks { get; set; }
        public int FailedTracks { get; set; }
        public int ActiveTracks { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }

        public bool IsFinished => CompletedTracks + FailedTracks >= TotalTracks;
    }
}
