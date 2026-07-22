using LibreSpotUWP.Models;
using System;
using System.Threading.Tasks;

namespace LibreSpotUWP.Interfaces
{
    public enum LibrespotAppDataKind
    {
        Track = 1,
        Album = 2,
        Artist = 3,
        Playlist = 4,
        UserProfile = 5,
        UserPlaylists = 6,
        SavedTracks = 7,
        Search = 8,
        FollowedArtists = 9,
        Lyrics = 10,
        LyricsForImage = 11,
        Episode = 12,
        Show = 13,
        PlaylistAnnotation = 14,
        UserFollowersJson = 15,
        UserFollowingJson = 16,
        RadioForTrackJson = 17,
        ApolloStationJson = 18,
        NextPageJson = 19,
        AudioStorageJson = 20,
        AudioPreviewBinary = 21,
        HeadFileBinary = 22,
        ImageBinary = 23,
        ContextJson = 24,
        AutoplayContextJson = 25,
        RootlistJson = 26
    }

    public interface ILibrespotService : IDisposable
    {
        Task InitializeAsync();
        LibrespotSessionState Session { get; }
        long SessionGeneration { get; }
        LibrespotPlaybackState PlaybackState { get; }
        LibrespotTrackInfo CurrentTrack { get; }
        ushort Volume { get; }
        string DeviceId { get; }
        string DeviceName { get; }

        Task ConnectWithAccessTokenAsync(string accessToken);
        Task ReconnectWithAccessTokenAsync(string accessToken);
        Task DisconnectAsync();
        Task<LibrespotTrackData> GetTrackAsync(string trackUri);
        Task<LibrespotAlbumData> GetAlbumAsync(string albumUri);
        Task<LibrespotArtistData> GetArtistAsync(string artistUri);
        Task<LibrespotPlaylistData> GetPlaylistAsync(string playlistUri);
        Task<LibrespotUserProfileData> GetUserProfileAsync(string userId);
        Task<LibrespotPlaylistListData> GetUserPlaylistsAsync(string userId);
        Task<LibrespotTrackListData> GetSavedTracksAsync(string userId);
        Task<LibrespotArtistListData> GetFollowedArtistsAsync(string userId);
        Task<LibrespotLyricsData> GetLyricsAsync(string trackUri, string imageIdHex = null);
        Task<string> GetLyricsJsonAsync(string trackUri, string imageIdHex = null);
        Task<LibrespotSearchData> SearchAsync(string query);
        Task LoadAndPlayAsync(string spotifyUri, string startUri);
        Task SetTrackPersistedAsync(string trackUri, bool persisted);
        Task PauseAsync();
        Task ResumeAsync();
        Task StopAsync();
        Task SetVolumeAsync(ushort volume);

        Task SetShuffleAsync(bool enabled);
        Task SetRepeatAsync(uint mode);

        uint GetPositionMs();
        void Seek(uint posMs);
        void Next();
        void Previous();

        event EventHandler<LibrespotSessionState> SessionStateChanged;
        event EventHandler<LibrespotTrackInfo> TrackChanged;
        event EventHandler<LibrespotPlaybackState> PlaybackStateChanged;
        event EventHandler<LibrespotPlaybackEvent> PlaybackEvent;
        event EventHandler<LibrespotPositionUpdate> PositionChanged;
        event EventHandler<ushort> VolumeChanged;
        event EventHandler<bool> ShuffleChanged;
        event EventHandler<uint> RepeatChanged;
        event EventHandler<LibrespotTrackBoundaryInfo> EndOfTrack;
        event EventHandler<LibrespotTrackBoundaryInfo> TimeToPreloadNextTrack;
        event EventHandler<LibrespotTrackBoundaryInfo> TrackPreloading;
        event EventHandler<string> LogMessage;
        event EventHandler<string> Panic;
    }

}
