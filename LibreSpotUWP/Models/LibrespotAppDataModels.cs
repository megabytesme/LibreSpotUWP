using System.Collections.Generic;

namespace LibreSpotUWP.Models
{
    public sealed class LibrespotImageData
    {
        public string Url { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public sealed class LibrespotArtistSummaryData
    {
        public string Id { get; set; }
        public string Uri { get; set; }
        public string Name { get; set; }
    }

    public sealed class LibrespotAlbumSummaryData
    {
        public string Id { get; set; }
        public string Uri { get; set; }
        public string Name { get; set; }
        public string AlbumType { get; set; }
        public string ReleaseDate { get; set; }
        public int TotalTracks { get; set; }
        public List<LibrespotImageData> Images { get; set; } = new List<LibrespotImageData>();
        public List<LibrespotArtistSummaryData> Artists { get; set; } = new List<LibrespotArtistSummaryData>();
    }

    public sealed class LibrespotSimpleTrackData
    {
        public string Id { get; set; }
        public string Uri { get; set; }
        public string Name { get; set; }
        public int DurationMs { get; set; }
        public int DiscNumber { get; set; }
        public int TrackNumber { get; set; }
        public List<LibrespotArtistSummaryData> Artists { get; set; } = new List<LibrespotArtistSummaryData>();
    }

    public sealed class LibrespotTrackData
    {
        public string Id { get; set; }
        public string Uri { get; set; }
        public string Name { get; set; }
        public int DurationMs { get; set; }
        public int DiscNumber { get; set; }
        public int TrackNumber { get; set; }
        public List<LibrespotArtistSummaryData> Artists { get; set; } = new List<LibrespotArtistSummaryData>();
        public LibrespotAlbumSummaryData Album { get; set; }
    }

    public sealed class LibrespotAlbumData
    {
        public string Id { get; set; }
        public string Uri { get; set; }
        public string Name { get; set; }
        public string AlbumType { get; set; }
        public string ReleaseDate { get; set; }
        public int TotalTracks { get; set; }
        public List<LibrespotImageData> Images { get; set; } = new List<LibrespotImageData>();
        public List<LibrespotArtistSummaryData> Artists { get; set; } = new List<LibrespotArtistSummaryData>();
        public List<LibrespotSimpleTrackData> Tracks { get; set; } = new List<LibrespotSimpleTrackData>();
    }

    public sealed class LibrespotArtistData
    {
        public string Id { get; set; }
        public string Uri { get; set; }
        public string Name { get; set; }
        public List<LibrespotImageData> Images { get; set; } = new List<LibrespotImageData>();
        public List<LibrespotAlbumSummaryData> Albums { get; set; } = new List<LibrespotAlbumSummaryData>();
    }

    public sealed class LibrespotOwnerData
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
    }

    public sealed class LibrespotPlaylistSummaryData
    {
        public string Id { get; set; }
        public string Uri { get; set; }
        public string Name { get; set; }
        public List<LibrespotImageData> Images { get; set; } = new List<LibrespotImageData>();
    }

    public sealed class LibrespotPlaylistData
    {
        public string Id { get; set; }
        public string Uri { get; set; }
        public string Name { get; set; }
        public List<LibrespotImageData> Images { get; set; } = new List<LibrespotImageData>();
        public LibrespotOwnerData Owner { get; set; }
        public List<LibrespotTrackData> Tracks { get; set; } = new List<LibrespotTrackData>();
    }

    public sealed class LibrespotUserProfileData
    {
        public string Id { get; set; }
        public string Uri { get; set; }
        public string DisplayName { get; set; }
        public string Email { get; set; }
        public string Country { get; set; }
        public List<LibrespotImageData> Images { get; set; } = new List<LibrespotImageData>();
    }

    public sealed class LibrespotPlaylistListData
    {
        public List<LibrespotPlaylistSummaryData> Items { get; set; } = new List<LibrespotPlaylistSummaryData>();
    }

    public sealed class LibrespotTrackListData
    {
        public List<LibrespotTrackData> Items { get; set; } = new List<LibrespotTrackData>();
    }

    public sealed class LibrespotArtistListData
    {
        public List<LibrespotArtistSummaryData> Items { get; set; } = new List<LibrespotArtistSummaryData>();
    }

    public sealed class LibrespotSearchData
    {
        public List<LibrespotTrackData> Tracks { get; set; } = new List<LibrespotTrackData>();
        public List<LibrespotAlbumSummaryData> Albums { get; set; } = new List<LibrespotAlbumSummaryData>();
        public List<LibrespotArtistSummaryData> Artists { get; set; } = new List<LibrespotArtistSummaryData>();
        public List<LibrespotPlaylistSummaryData> Playlists { get; set; } = new List<LibrespotPlaylistSummaryData>();
    }
}
