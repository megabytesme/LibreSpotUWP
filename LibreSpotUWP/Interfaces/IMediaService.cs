using LibreSpotUWP.Models;
using System;
using System.Threading.Tasks;

namespace LibreSpotUWP.Interfaces
{
    public interface IMediaService
    {
        MediaState Current { get; }

        Task InitializeAsync();

        Task PlayTrackAsync(string spotifyUri);
        Task PauseAsync();
        Task ResumeAsync();
        Task StopAsync();
        Task SetVolumeAsync(ushort volume);

        event EventHandler<MediaState> MediaStateChanged;
    }
}