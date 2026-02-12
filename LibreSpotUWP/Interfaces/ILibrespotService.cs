using LibreSpotUWP.Models;
using System;
using System.Threading.Tasks;

namespace LibreSpotUWP.Interfaces
{
    public interface ILibrespotService : IDisposable
    {
        Task InitializeAsync();
        LibrespotSessionState Session { get; }
        LibrespotPlaybackState PlaybackState { get; }
        LibrespotTrackInfo CurrentTrack { get; }
        ushort Volume { get; }

        Task ConnectWithAccessTokenAsync(string accessToken);
        Task LoadAndPlayAsync(string spotifyUri);
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
        event EventHandler<ushort> VolumeChanged;
        event EventHandler<string> LogMessage;
        event EventHandler<string> Panic;
    }

}