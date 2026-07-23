using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interop;
using LibreSpotUWP.Models;
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LibreSpotUWP.Services
{
    /// <summary>
    /// Controls a Rust-owned Windows audio renderer. It intentionally has no
    /// AudioGraph, AudioFrameInputNode, or managed PCM ring buffer.
    /// </summary>
    internal sealed class NativeWindowsAudioPlayer : ILibrespotAudioPlayer
    {
        private static long _nextInstanceId;
        private static readonly object BackendSelectionSync = new object();
        private readonly AudioBackendKind _backend;
        private readonly string _outputDeviceId;
        private readonly long _instanceId = Interlocked.Increment(ref _nextInstanceId);
        private int _disposed;

        public NativeWindowsAudioPlayer(AudioBackendKind backend, string outputDeviceId)
        {
            if (backend == AudioBackendKind.RingBuffer)
                throw new ArgumentException("The native player cannot host the ring-buffer backend.", nameof(backend));

            _backend = backend;
            _outputDeviceId = outputDeviceId ?? string.Empty;
        }

        public long GraphInstanceId => -_instanceId;
        public bool HasPendingTransition => false;

#pragma warning disable CS0067
        public event EventHandler<ProducerStalledEventArgs> ProducerStalled;
#pragma warning restore CS0067

        public async Task InitializeAsync()
        {
            ThrowIfDisposed();
            await SelectBackendAsync(_backend, _outputDeviceId).ConfigureAwait(false);
            ApplyEffects();
            LogService.Info($"[NativeWindowsAudioPlayer.InitializeAsync] backend={_backend}, outputDevice={(_outputDeviceId.Length == 0 ? "default" : _outputDeviceId)}, nativePlayerId={_instanceId}.");
        }

        internal static Task SelectBackendAsync(AudioBackendKind backend, string outputDeviceId)
        {
            return Task.Run(() => SelectBackend(backend, outputDeviceId));
        }

        internal static void SelectBackend(AudioBackendKind backend, string outputDeviceId)
        {
            // Preserve an empty device selection. XAudio2 interprets a null
            // device ID as the default renderer, while WASAPI uses its native
            // default-render-device activation token. Expanding the default
            // into a concrete endpoint ID is rejected on some W10M builds.
            var resolvedDeviceId = string.IsNullOrWhiteSpace(outputDeviceId)
                ? string.Empty
                : outputDeviceId;

            lock (BackendSelectionSync)
            {
                var devicePtr = AllocUtf8(resolvedDeviceId ?? string.Empty);
                try
                {
                    if (!Librespot.librespot_audio_set_backend((uint)backend, devicePtr))
                    {
                        var nativeError = GetLastBackendError();
                        var detail = string.IsNullOrWhiteSpace(nativeError)
                            ? string.Empty
                            : $" Native error: {nativeError}";
                        throw new InvalidOperationException(
                            $"Rust audio backend '{backend}' could not initialize the selected output device.{detail}");
                    }
                }
                finally
                {
                    if (devicePtr != IntPtr.Zero)
                        Marshal.FreeHGlobal(devicePtr);
                }
            }
        }

        private static string GetLastBackendError()
        {
            var errorPtr = Librespot.librespot_audio_get_last_error();
            if (errorPtr == IntPtr.Zero)
                return string.Empty;

            try
            {
                var length = 0;
                while (Marshal.ReadByte(errorPtr, length) != 0)
                    length++;

                if (length == 0)
                    return string.Empty;

                var bytes = new byte[length];
                Marshal.Copy(errorPtr, bytes, 0, length);
                return Encoding.UTF8.GetString(bytes, 0, bytes.Length);
            }
            finally
            {
                Librespot.librespot_string_free(errorPtr);
            }
        }

        public long BeginTransition(string reason, string oldTrackUri, string newTrackUri, bool preserveCurrent, bool shouldPlay) => _instanceId;
        public long BeginAutomaticTransition(string oldTrackUri, bool shouldPlay) => _instanceId;
        public bool ObserveLoading(ulong playRequestId) => true;
        public bool ObserveTrackChanged(string trackUri, ulong playRequestId, ulong audioGeneration, bool wasPreloaded) => true;
        public bool ObserveSeek(ulong playRequestId, ulong audioGeneration) => true;
        public Task<bool> RequestPlaybackAsync(ulong playRequestId, ulong audioGeneration) => Task.FromResult(true);
        public bool IsEventForPendingTransition(ulong playRequestId, ulong audioGeneration) => true;
        public bool IsEventForActiveGeneration(ulong playRequestId, ulong audioGeneration) => true;
        public Task PauseAsync() => Task.CompletedTask;
        public void Stop() { }
        public void SetSessionState(bool connected, long sessionGeneration) { }
        public void ReportTransportFailure(long sessionGeneration, string reason) { }

        public void SetAudioEffectsPreset(string preset)
        {
            ThrowIfDisposed();
            ApplyEffects();
        }

        public EqualizerBandRange[] GetEqualizerBandRanges()
        {
            var ranges = new EqualizerBandRange[5];
            for (var i = 0; i < ranges.Length; i++)
            {
                ranges[i] = new EqualizerBandRange
                {
                    MinimumGain = UserSettings.EqualizerMinGainDb,
                    MaximumGain = UserSettings.EqualizerMaxGainDb
                };
            }
            return ranges;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
        }

        public Task DisposeAsync()
        {
            Dispose();
            return Task.CompletedTask;
        }

        internal static void ApplyEffects()
        {
            var gains = UserSettings.GetEqualizerBandGains();
            var nativeGains = new float[5];
            for (var i = 0; i < nativeGains.Length && i < gains.Length; i++)
                nativeGains[i] = (float)gains[i];

            var gainsPtr = Marshal.AllocHGlobal(nativeGains.Length * sizeof(float));
            try
            {
                Marshal.Copy(nativeGains, 0, gainsPtr, nativeGains.Length);
                Librespot.librespot_audio_set_effects(
                    PresetToNative(UserSettings.AudioEffectsPreset),
                    (float)UserSettings.AudioEffectsStrength,
                    UserSettings.AudioEchoEffectEnabled,
                    UserSettings.AudioReverbEffectEnabled,
                    UserSettings.AudioLimiterEffectEnabled,
                    gainsPtr,
                    (UIntPtr)(uint)nativeGains.Length);
            }
            finally
            {
                Marshal.FreeHGlobal(gainsPtr);
            }
        }

        private static uint PresetToNative(string preset)
        {
            if (string.Equals(preset, "BassBoost", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(preset, "VocalBoost", StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(preset, "Warm", StringComparison.OrdinalIgnoreCase)) return 3;
            if (string.Equals(preset, "Equalizer", StringComparison.OrdinalIgnoreCase)) return 4;
            return 0;
        }

        private static IntPtr AllocUtf8(string value)
        {
            if (string.IsNullOrEmpty(value))
                return IntPtr.Zero;

            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            var pointer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return pointer;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(NativeWindowsAudioPlayer));
        }
    }
}
