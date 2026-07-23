using LibreSpotUWP.Models;
using System;
using System.Threading.Tasks;

namespace LibreSpotUWP.Services
{
    public interface ILibrespotAudioPlayer : IDisposable
    {
        long GraphInstanceId { get; }
        bool HasPendingTransition { get; }

        event EventHandler<ProducerStalledEventArgs> ProducerStalled;

        Task InitializeAsync();
        long BeginTransition(string reason, string oldTrackUri, string newTrackUri, bool preserveCurrent, bool shouldPlay);
        long BeginAutomaticTransition(string oldTrackUri, bool shouldPlay);
        bool ObserveLoading(ulong playRequestId);
        bool ObserveTrackChanged(string trackUri, ulong playRequestId, ulong audioGeneration, bool wasPreloaded);
        bool ObserveSeek(ulong playRequestId, ulong audioGeneration);
        Task<bool> RequestPlaybackAsync(ulong playRequestId, ulong audioGeneration);
        bool IsEventForPendingTransition(ulong playRequestId, ulong audioGeneration);
        bool IsEventForActiveGeneration(ulong playRequestId, ulong audioGeneration);
        Task PauseAsync();
        void Stop();
        void SetSessionState(bool connected, long sessionGeneration);
        void ReportTransportFailure(long sessionGeneration, string reason);
        void SetAudioEffectsPreset(string preset);
        EqualizerBandRange[] GetEqualizerBandRanges();
        Task DisposeAsync();
    }
}
