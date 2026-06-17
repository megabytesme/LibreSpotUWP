using System;
using System.Collections.Generic;

namespace LibreSpotUWP.Models
{
    public class OfflineTrackEntry
    {
        public string TrackUri { get; set; }
        public string TrackId { get; set; }
        public string Name { get; set; }
        public List<string> ArtistNames { get; set; } = new List<string>();
        public string ArtistLine { get; set; }
        public string AlbumId { get; set; }
        public string AlbumName { get; set; }
        public string ImageUrl { get; set; }
        public string ImageLocalUri { get; set; }
        public int DurationMs { get; set; }
        public bool IsExplicitlySaved { get; set; }
        public DownloadTrackState DownloadState { get; set; } = DownloadTrackState.Idle;
        public List<string> AlbumMembershipIds { get; set; } = new List<string>();
        public List<string> PlaylistMembershipIds { get; set; } = new List<string>();
        public Uri ImageUri => OfflineImageUriHelper.TryCreateImageUri(ImageLocalUri)
            ?? OfflineImageUriHelper.TryCreateImageUri(ImageUrl);
    }

    public class OfflineAlbumEntry
    {
        public string AlbumId { get; set; }
        public string Name { get; set; }
        public string ArtistLine { get; set; }
        public string ImageUrl { get; set; }
        public string ImageLocalUri { get; set; }
        public List<string> TrackUris { get; set; } = new List<string>();
        public DateTimeOffset SavedAtUtc { get; set; }
        public Uri ImageUri => OfflineImageUriHelper.TryCreateImageUri(ImageLocalUri)
            ?? OfflineImageUriHelper.TryCreateImageUri(ImageUrl);
    }

    public class OfflinePlaylistEntry
    {
        public string PlaylistId { get; set; }
        public string Name { get; set; }
        public string OwnerName { get; set; }
        public string ImageUrl { get; set; }
        public string ImageLocalUri { get; set; }
        public List<string> TrackUris { get; set; } = new List<string>();
        public DateTimeOffset SavedAtUtc { get; set; }
        public Uri ImageUri => OfflineImageUriHelper.TryCreateImageUri(ImageLocalUri)
            ?? OfflineImageUriHelper.TryCreateImageUri(ImageUrl);
    }

    public class OfflineCatalogData
    {
        public List<OfflineTrackEntry> Tracks { get; set; } = new List<OfflineTrackEntry>();
        public List<OfflineAlbumEntry> Albums { get; set; } = new List<OfflineAlbumEntry>();
        public List<OfflinePlaylistEntry> Playlists { get; set; } = new List<OfflinePlaylistEntry>();
    }

    internal static class OfflineImageUriHelper
    {
        internal static Uri TryCreateImageUri(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
        }
    }
}
