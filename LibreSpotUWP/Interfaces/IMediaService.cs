using LibreSpotUWP.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LibreSpotUWP.Interfaces
{
    public interface IMediaService
    {
        Task InitializeAsync();
        Task PlayAsync(string spotifyUri, string contextUri);
        Task PlayAsync(string spotifyUri, string contextUri, IReadOnlyList<string> orderedTrackUris, int startIndex);
        Task PauseAsync();
        Task ResumeAsync();
        Task StopAsync();
        Task PrepareForSuspendingAsync();
        Task ResumeAfterSuspendingAsync();
        Task SetVolumeAsync(ushort v);
        void SetVolumeDebounced(double v);
        Task SetAudioEffectsPresetAsync(string preset);
        EqualizerBandRange[] GetEqualizerBandRanges();
        Task<AudioOutputDeviceInfo[]> GetAudioOutputDevicesAsync();
        Task SetAudioOutputDeviceAsync(string deviceId);
        Task<SpotifyConnectDeviceInfo[]> GetSpotifyConnectDevicesAsync();
        Task SetSpotifyConnectDeviceAsync(string deviceId);
        Task RefreshCurrentTrackMetadataAsync();
        Task SetShuffleAsync(bool enabled);
        Task SetRepeatAsync(int mode);
        Task SetCurrentTrackPersistedAsync(bool persisted);
        void Next();
        void Previous();
        void Seek(uint posMs);

        MediaState Current { get; }
        string CurrentAudioOutputDeviceId { get; }
        string CurrentSpotifyConnectDeviceId { get; }

        event EventHandler<MediaState> MediaStateChanged;
    }
}
