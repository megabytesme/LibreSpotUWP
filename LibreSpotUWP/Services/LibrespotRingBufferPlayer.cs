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
        private const int DefaultEqualizerBandCount = 5;
        private const double EqualizerMinLinearGain = 0.126;
        private const double EqualizerMaxLinearGain = 7.94;
        private const double EqualizerDefaultLinearGain = 1.0;
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
            if (_equalizerBandRanges.Length == 0)
                _equalizerBandRanges = BuildEqualizerBandRanges(DefaultEqualizerBandCount);

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
            _equalizerBandRanges = BuildEqualizerBandRanges(Math.Max(DefaultEqualizerBandCount, _equalizerEffect.Bands?.Count ?? 0));
            _audioEffectsConfigured = true;
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
                double targetGainDb;
                if (string.Equals(normalized, "Equalizer", StringComparison.OrdinalIgnoreCase))
                {
                    targetGainDb = index < custom.Length ? ClampGainDb(custom[index]) : 0.0;
                }
                else
                {
                    targetGainDb = ClampGainDb(GetBandGainDb(normalized, band.FrequencyCenter, strength));
                }

                if (!TryApplyBandGain(band, DecibelsToLinearGain(targetGainDb)))
                    Debug.WriteLine($"Skipping equalizer band at {band.FrequencyCenter}Hz for preset '{preset}' ({targetGainDb} dB): Value does not fall within the expected range.");

                index++;
            }
        }

        private static bool TryApplyBandGain(EqualizerBand band, double targetGain)
        {
            try
            {
                band.Gain = ClampLinearGain(targetGain);
                return true;
            }
            catch (ArgumentException)
            {
            }

            try
            {
                band.Gain = EqualizerDefaultLinearGain;
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static double GetBandGainDb(string preset, double frequencyCenter, double strength)
        {
            strength = Clamp01(strength);

            switch (NormalizePreset(preset))
            {
                case "BassBoost":
                    if (frequencyCenter <= 125)
                        return 8.0 * strength;
                    if (frequencyCenter <= 500)
                        return 5.0 * strength;
                    if (frequencyCenter <= 2000)
                        return 2.0 * strength;
                    if (frequencyCenter <= 6000)
                        return -1.0 * strength;
                    return -2.0 * strength;

                case "VocalBoost":
                    if (frequencyCenter <= 125)
                        return -3.0 * strength;
                    if (frequencyCenter <= 500)
                        return -1.0 * strength;
                    if (frequencyCenter <= 4000)
                        return 5.0 * strength;
                    if (frequencyCenter <= 8000)
                        return 4.0 * strength;
                    return 2.0 * strength;

                case "Warm":
                    if (frequencyCenter <= 125)
                        return 4.0 * strength;
                    if (frequencyCenter <= 500)
                        return 3.0 * strength;
                    if (frequencyCenter <= 2000)
                        return 1.0 * strength;
                    if (frequencyCenter <= 6000)
                        return -1.0 * strength;
                    return -2.0 * strength;

                default:
                    return 0.0;
            }
        }

        private static EqualizerBandRange[] BuildEqualizerBandRanges(int bandCount)
        {
            bandCount = Math.Max(DefaultEqualizerBandCount, bandCount);
            return Enumerable.Range(0, bandCount)
                .Select(_ => new EqualizerBandRange
                {
                    MinimumGain = UserSettings.EqualizerMinGainDb,
                    MaximumGain = UserSettings.EqualizerMaxGainDb
                })
                .ToArray();
        }

        private static double DecibelsToLinearGain(double decibels)
        {
            return ClampLinearGain(Math.Pow(10.0, ClampGainDb(decibels) / 20.0));
        }

        private static double ClampLinearGain(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return EqualizerDefaultLinearGain;

            return Math.Max(EqualizerMinLinearGain, Math.Min(EqualizerMaxLinearGain, value));
        }

        private static double ClampGainDb(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0.0;

            return Math.Max(UserSettings.EqualizerMinGainDb, Math.Min(UserSettings.EqualizerMaxGainDb, value));
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
