using LibreSpotUWP.Models;
using System;
using System.Threading.Tasks;

namespace LibreSpotUWP.Interfaces
{
    public interface IMediaService
    {
        Task InitializeAsync();
        Task PlayAsync(string spotifyUri, string contextUri);
        Task PauseAsync();
        Task ResumeAsync();
        Task StopAsync();
        Task SetVolumeAsync(ushort v);
        void SetVolumeDebounced(double v);
        Task SetAudioEffectsPresetAsync(string preset);
        Task RefreshCurrentTrackMetadataAsync();
        Task SetShuffleAsync(bool enabled);
        Task SetRepeatAsync(int mode);
        Task SetCurrentTrackPersistedAsync(bool persisted);
        void Next();
        void Previous();
        void Seek(uint posMs);

        MediaState Current { get; }

        event EventHandler<MediaState> MediaStateChanged;
    }
}
