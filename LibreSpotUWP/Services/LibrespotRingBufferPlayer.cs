using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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

        public void Dispose()
        {
            _inputNode?.Stop();
            _graph?.Stop();
            _inputNode?.Dispose();
            _graph?.Dispose();
            while (_framePool.TryDequeue(out var frame)) frame.Dispose();
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