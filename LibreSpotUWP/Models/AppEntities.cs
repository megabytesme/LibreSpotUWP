using System;
using System.Collections.Generic;

namespace LibreSpotUWP.Models
{
    public sealed class AppImage
    {
        public string Url { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
    }

    public sealed class AppUserSummary
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
    }

    public sealed class AppSimpleArtist
    {
        public string Id { get; set; }
        public string Uri { get; set; }
        public string Name { get; set; }
    }

    public sealed class AppAlbumSummary
    {
        public string Id { get; set; }
        public string Uri { get; set; }
        public string Name { get; set; }
        public string AlbumType { get; set; }
        public string ReleaseDate { get; set; }
        public int TotalTracks { get; set; }
        public List<AppImage> Images { get; set; } = new List<AppImage>();
        public List<AppSimpleArtist> Artists { get; set; } = new List<AppSimpleArtist>();
    }

    public sealed class AppArtist
    {
        public string Id { get; set; }
        public string Uri { get; set; }
        public string Name { get; set; }
        public List<AppImage> Images { get; set; } = new List<AppImage>();
    }

    public sealed class AppTrack
    {
        public string Id { get; set; }
        public string Uri { get; set; }
        public string Name { get; set; }
        public int DurationMs { get; set; }
        public int DiscNumber { get; set; }
        public int TrackNumber { get; set; }
        public AppAlbumSummary Album { get; set; }
        public List<AppSimpleArtist> Artists { get; set; } = new List<AppSimpleArtist>();
    }

    public sealed class AppPlaylist
    {
        public string Id { get; set; }
        public string Uri { get; set; }
        public string Name { get; set; }
        public AppUserSummary Owner { get; set; }
        public List<AppImage> Images { get; set; } = new List<AppImage>();
    }

    public sealed class AppSavedAlbum
    {
        public DateTime AddedAt { get; set; }
        public AppAlbumSummary Album { get; set; }
    }

    public sealed class AppUserProfile
    {
        public string Id { get; set; }
        public string Uri { get; set; }
        public string DisplayName { get; set; }
        public string Email { get; set; }
        public string Country { get; set; }
        public List<AppImage> Images { get; set; } = new List<AppImage>();
    }
}
