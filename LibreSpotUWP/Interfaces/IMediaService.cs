using LibreSpotUWP.Models;
using System;
using System.Threading.Tasks;

namespace LibreSpotUWP.Interfaces
{
    public interface IMediaService
    {
        Task InitializeAsync();
        Task PlayTrackAsync(string spotifyUri);
        Task PauseAsync();
        Task ResumeAsync();
        Task StopAsync();
        Task SetVolumeAsync(ushort v);
        void SetVolumeDebounced(double v);
        Task SetShuffleAsync(bool enabled);
        Task SetRepeatAsync(int mode);
        void Next();
        void Previous();
        void Seek(uint posMs);

        MediaState Current { get; }

        event EventHandler<MediaState> MediaStateChanged;
    }
}