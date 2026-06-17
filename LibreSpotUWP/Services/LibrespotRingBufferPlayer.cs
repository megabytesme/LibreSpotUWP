using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using LibreSpotUWP.Helpers;
using LibreSpotUWP.Models;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using static LibreSpotUWP.Interop.Librespot;

namespace LibreSpotUWP.Services
{
    public sealed class LibrespotRingBufferPlayer : IDisposable
    {
        private readonly AudioEncodingProperties _props;
        private AudioGraph _graph;
        private AudioFrameInputNode _inputNode;
        private EchoEffectDefinition _echoEffect;
        private EqualizerEffectDefinition _equalizerEffect;
        private LimiterEffectDefinition _limiterEffect;
        private ReverbEffectDefinition _reverbEffect;
        private bool _audioEffectsConfigured;
        private double _outgoingGain = 1.0;
        private string _audioEffectsPreset = "None";
        private EqualizerBandRange[] _equalizerBandRanges = Array.Empty<EqualizerBandRange>();

        private IntPtr _bufferPtr;
        private int _capacityBytes;
        private int _readPos;
        private int _frameSize;

        private readonly ConcurrentQueue<PooledFrame> _framePool = new ConcurrentQueue<PooledFrame>();
        private const int PoolSize = 6;
        private uint _maxFrameBytes;

        private class PooledFrame : IDisposable
        {
            public AudioFrame Frame { get; }
            public uint Capacity { get; }

            public PooledFrame(uint capacity)
            {
                Frame = new AudioFrame(capacity);
                Capacity = capacity;
            }

            public void Dispose() => Frame.Dispose();
        }

        public LibrespotRingBufferPlayer(AudioEncodingProperties props)
        {
            _props = props;
        }

        public async Task InitializeAsync()
        {
            using (var process = Process.GetCurrentProcess())
                process.PriorityClass = ProcessPriorityClass.High;

            await WaitForRingBufferAsync();

            _capacityBytes = (int)librespot_audio_get_capacity().ToUInt32();
            _readPos = 0;
            librespot_audio_set_read_cursor((UIntPtr)0);
            _frameSize = (int)(_props.ChannelCount * (_props.BitsPerSample / 8));

            uint samplesPerQuantum = 441;
            _maxFrameBytes = samplesPerQuantum * (uint)_frameSize;

            for (int i = 0; i < PoolSize; i++)
                _framePool.Enqueue(new PooledFrame(_maxFrameBytes));

            var settings = new AudioGraphSettings(AudioRenderCategory.Media)
            {
                EncodingProperties = _props,
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.SystemDefault
            };

            var result = await AudioGraph.CreateAsync(settings);
            if (result.Status != AudioGraphCreationStatus.Success)
                throw new InvalidOperationException($"AudioGraph creation failed: {result.Status}");

            _graph = result.Graph;
            var outResult = await _graph.CreateDeviceOutputNodeAsync();
            _inputNode = _graph.CreateFrameInputNode(_props);
            _inputNode.OutgoingGain = _outgoingGain;
            ApplyAudioEffectsPreset(_audioEffectsPreset);

            _inputNode.QuantumStarted += OnQuantumStarted;
            _inputNode.AddOutgoingConnection(outResult.DeviceOutputNode);

            _graph.Start();
        }

        private unsafe void OnQuantumStarted(AudioFrameInputNode sender, FrameInputNodeQuantumStartedEventArgs args)
        {
            int samplesNeeded = args.RequiredSamples;
            if (samplesNeeded <= 0) return;

            int bytesRequested = samplesNeeded * _frameSize;
            uint writePos = librespot_audio_get_write_cursor().ToUInt32();

            int available = (int)(((long)_capacityBytes + (int)writePos - _readPos) % _capacityBytes);

            int bytesToCopy = Math.Min(available, bytesRequested);
            bytesToCopy -= bytesToCopy % _frameSize;

            if (bytesToCopy <= 0) return;

            if (!_framePool.TryDequeue(out PooledFrame pooled) || pooled.Capacity < bytesToCopy)
            {
                pooled?.Dispose();
                pooled = new PooledFrame((uint)bytesToCopy);
            }

            using (AudioBuffer buffer = pooled.Frame.LockBuffer(AudioBufferAccessMode.Write))
            using (IMemoryBufferReference reference = buffer.CreateReference())
            {
                if (reference is IMemoryBufferByteAccess byteAccess)
                {
                    byteAccess.GetBuffer(out IntPtr dataInPtr, out uint capacity);
                    byte* dest = (byte*)dataInPtr;
                    byte* srcBase = (byte*)_bufferPtr;

                    int firstChunkSize = Math.Min(bytesToCopy, _capacityBytes - _readPos);
                    Buffer.MemoryCopy(srcBase + _readPos, dest, capacity, firstChunkSize);

                    if (bytesToCopy > firstChunkSize)
                    {
                        Buffer.MemoryCopy(srcBase, dest + firstChunkSize, capacity - (uint)firstChunkSize, bytesToCopy - firstChunkSize);
                    }

                    buffer.Length = (uint)bytesToCopy;
                }
            }

            sender.AddFrame(pooled.Frame);

            if (pooled.Capacity <= _maxFrameBytes)
                _framePool.Enqueue(pooled);
            else
                pooled.Dispose();

            _readPos = (_readPos + bytesToCopy) % _capacityBytes;
            librespot_audio_set_read_cursor((UIntPtr)_readPos);
        }

        public void Start() => _graph?.Start();
        public void Stop() => _graph?.Stop();

        public void SetOutgoingGain(double gain)
        {
            _outgoingGain = Math.Max(0d, gain);

            if (_inputNode != null)
                _inputNode.OutgoingGain = _outgoingGain;
        }

        public void SetAudioEffectsPreset(string preset)
        {
            _audioEffectsPreset = NormalizePreset(preset);
            ApplyAudioEffectsPreset(_audioEffectsPreset);
        }

        public EqualizerBandRange[] GetEqualizerBandRanges()
        {
            EnsureAudioEffectsCreated();

            return _equalizerBandRanges
                .Select(range => new EqualizerBandRange
                {
                    MinimumGain = range.MinimumGain,
                    MaximumGain = range.MaximumGain
                })
                .ToArray();
        }

        public void Dispose()
        {
            _inputNode?.Stop();
            _graph?.Stop();
            _inputNode?.Dispose();
            _graph?.Dispose();
            while (_framePool.TryDequeue(out var frame)) frame.Dispose();
        }

        private void ApplyAudioEffectsPreset(string preset)
        {
            if (_inputNode == null || _graph == null)
                return;

            var normalized = NormalizePreset(preset);
            if (string.Equals(normalized, "None", StringComparison.OrdinalIgnoreCase))
            {
                DisableAllAudioEffects();
                return;
            }

            EnsureAudioEffectsCreated();
            DisableAllAudioEffects();

            try
            {
                switch (normalized)
                {
                    case "Echo":
                        ConfigureEchoEffect();
                        _inputNode.EnableEffectsByDefinition(_echoEffect);
                        break;
                    case "Reverb":
                        ConfigureReverbEffect();
                        _inputNode.EnableEffectsByDefinition(_reverbEffect);
                        break;
                    case "Limiter":
                        ConfigureLimiterEffect();
                        _inputNode.EnableEffectsByDefinition(_limiterEffect);
                        break;
                    case "Equalizer":
                    case "BassBoost":
                    case "VocalBoost":
                    case "Warm":
                        ConfigureEqualizerPreset(preset);
                        _inputNode.EnableEffectsByDefinition(_equalizerEffect);
                        break;
                    default:
                        break;
                }
            }
            catch (ArgumentException ex)
            {
                Debug.WriteLine($"Failed to apply audio effects preset '{preset}': {ex.Message}");
                DisableAllAudioEffects();
            }
        }

        private static string NormalizePreset(string preset)
        {
            if (string.IsNullOrWhiteSpace(preset))
                return "None";

            if (string.Equals(preset, "BassBoost", StringComparison.OrdinalIgnoreCase))
                return "BassBoost";

            if (string.Equals(preset, "VocalBoost", StringComparison.OrdinalIgnoreCase))
                return "VocalBoost";

            if (string.Equals(preset, "Warm", StringComparison.OrdinalIgnoreCase))
                return "Warm";

            if (string.Equals(preset, "Echo", StringComparison.OrdinalIgnoreCase))
                return "Echo";

            if (string.Equals(preset, "Reverb", StringComparison.OrdinalIgnoreCase))
                return "Reverb";

            if (string.Equals(preset, "Limiter", StringComparison.OrdinalIgnoreCase))
                return "Limiter";

            if (string.Equals(preset, "Equalizer", StringComparison.OrdinalIgnoreCase))
                return "Equalizer";

            return "None";
        }

        private void EnsureAudioEffectsCreated()
        {
            if (_inputNode == null || _graph == null || _audioEffectsConfigured)
                return;

            _equalizerEffect = new EqualizerEffectDefinition(_graph);
            _echoEffect = new EchoEffectDefinition(_graph);
            _reverbEffect = new ReverbEffectDefinition(_graph);
            _limiterEffect = new LimiterEffectDefinition(_graph);

            _inputNode.EffectDefinitions.Add(_equalizerEffect);
            _inputNode.EffectDefinitions.Add(_echoEffect);
            _inputNode.EffectDefinitions.Add(_reverbEffect);
            _inputNode.EffectDefinitions.Add(_limiterEffect);
            _equalizerBandRanges = CaptureEqualizerBandRanges();
            _audioEffectsConfigured = true;
        }

        private EqualizerBandRange[] CaptureEqualizerBandRanges()
        {
            if (_equalizerEffect?.Bands == null)
                return Array.Empty<EqualizerBandRange>();

            return _equalizerEffect.Bands
                .Select(band => new EqualizerBandRange
                {
                    MinimumGain = ProbeBandLimit(band, positive: false),
                    MaximumGain = ProbeBandLimit(band, positive: true)
                })
                .ToArray();
        }

        private static double ProbeBandLimit(EqualizerBand band, bool positive)
        {
            const double epsilon = 0.0005;
            double low = 0.0;
            double high = 1.0;
            double direction = positive ? 1.0 : -1.0;

            while (CanApplyGain(band, direction * high) && high < 16.0)
            {
                low = high;
                high *= 2.0;
            }

            for (int attempt = 0; attempt < 20; attempt++)
            {
                var mid = (low + high) / 2.0;
                if (CanApplyGain(band, direction * mid))
                    low = mid;
                else
                    high = mid;

                if (high - low <= epsilon)
                    break;
            }

            TryApplyDirectGain(band, 0.0);
            return direction * low;
        }

        private static bool CanApplyGain(EqualizerBand band, double gain)
        {
            if (TryApplyDirectGain(band, gain))
                return true;

            TryApplyDirectGain(band, 0.0);
            return false;
        }

        private static bool TryApplyDirectGain(EqualizerBand band, double gain)
        {
            try
            {
                band.Gain = gain;
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private void DisableAllAudioEffects()
        {
            if (_inputNode == null)
                return;

            if (_equalizerEffect != null)
                _inputNode.DisableEffectsByDefinition(_equalizerEffect);
            if (_echoEffect != null)
                _inputNode.DisableEffectsByDefinition(_echoEffect);
            if (_reverbEffect != null)
                _inputNode.DisableEffectsByDefinition(_reverbEffect);
            if (_limiterEffect != null)
                _inputNode.DisableEffectsByDefinition(_limiterEffect);
        }

        private void ConfigureEchoEffect()
        {
            if (_echoEffect == null)
                return;

            var strength = Clamp01(UserSettings.AudioEffectsStrength);
            try { _echoEffect.Delay = 80.0 + (220.0 * strength); } catch (ArgumentException) { }
            try { _echoEffect.Feedback = 0.08 + (0.36 * strength); } catch (ArgumentException) { }
            try { _echoEffect.WetDryMix = 8.0 + (32.0 * strength); } catch (ArgumentException) { }
        }

        private void ConfigureReverbEffect()
        {
            if (_reverbEffect == null)
                return;

            var strength = Clamp01(UserSettings.AudioEffectsStrength);
            try { _reverbEffect.WetDryMix = 10.0 + (25.0 * strength); } catch (ArgumentException) { }
            try { _reverbEffect.ReverbGain = 0.1 + (0.5 * strength); } catch (ArgumentException) { }
            try { _reverbEffect.RoomSize = 0.35 + (0.45 * strength); } catch (ArgumentException) { }
        }

        private void ConfigureLimiterEffect()
        {
            if (_limiterEffect == null)
                return;

            try { _limiterEffect.Loudness = 0; } catch (ArgumentException) { }
        }

        private void ConfigureEqualizerPreset(string preset)
        {
            if (_equalizerEffect == null)
                return;

            var normalized = NormalizePreset(preset);
            var strength = Clamp01(UserSettings.AudioEffectsStrength);
            var custom = UserSettings.GetEqualizerBandGains();

            int index = 0;
            foreach (var band in _equalizerEffect.Bands)
            {
                double targetGain;
                if (string.Equals(normalized, "Equalizer", StringComparison.OrdinalIgnoreCase))
                {
                    targetGain = index < custom.Length ? ClampGain(custom[index]) : 0.0;
                }
                else
                {
                    targetGain = ClampGain(GetBandGain(normalized, band.FrequencyCenter, strength));
                }

                if (!TryApplyBandGain(band, targetGain))
                    Debug.WriteLine($"Skipping equalizer band at {band.FrequencyCenter}Hz for preset '{preset}': Value does not fall within the expected range.");

                index++;
            }
        }

        private static bool TryApplyBandGain(EqualizerBand band, double targetGain)
        {
            try
            {
                band.Gain = targetGain;
                return true;
            }
            catch (ArgumentException)
            {
            }

            var fallbackGain = targetGain;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                fallbackGain /= 2.0;

                try
                {
                    band.Gain = fallbackGain;
                    return true;
                }
                catch (ArgumentException)
                {
                }
            }

            try
            {
                band.Gain = 0.0;
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static double GetBandGain(string preset, double frequencyCenter, double strength)
        {
            strength = Clamp01(strength);

            switch (NormalizePreset(preset))
            {
                case "BassBoost":
                    if (frequencyCenter <= 125)
                        return 0.14 * strength;
                    if (frequencyCenter <= 500)
                        return 0.1 * strength;
                    if (frequencyCenter <= 2000)
                        return 0.04 * strength;
                    if (frequencyCenter <= 6000)
                        return -0.03 * strength;
                    return -0.06 * strength;

                case "VocalBoost":
                    if (frequencyCenter <= 125)
                        return -0.05 * strength;
                    if (frequencyCenter <= 500)
                        return -0.02 * strength;
                    if (frequencyCenter <= 4000)
                        return 0.12 * strength;
                    if (frequencyCenter <= 8000)
                        return 0.08 * strength;
                    return 0.04 * strength;

                case "Warm":
                    if (frequencyCenter <= 125)
                        return 0.1 * strength;
                    if (frequencyCenter <= 500)
                        return 0.08 * strength;
                    if (frequencyCenter <= 2000)
                        return 0.03 * strength;
                    if (frequencyCenter <= 6000)
                        return -0.02 * strength;
                    return -0.04 * strength;

                default:
                    return 0.0;
            }
        }

        private static double ClampGain(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0.0;

            return Math.Max(-0.25, Math.Min(0.25, value));
        }

        private static double Clamp01(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 1.0;

            return Math.Max(0.0, Math.Min(1.0, value));
        }

        [ComImport]
        [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMemoryBufferByteAccess
        {
            void GetBuffer(out IntPtr buffer, out uint capacity);
        }

        private async Task WaitForRingBufferAsync()
        {
            int waited = 0;
            while (librespot_audio_get_buffer() == IntPtr.Zero)
            {
                if (waited >= 5000) throw new InvalidOperationException("Ring Buffer timeout.");
                await Task.Delay(50);
                waited += 50;
            }
            _bufferPtr = librespot_audio_get_buffer();
        }
    }
}
