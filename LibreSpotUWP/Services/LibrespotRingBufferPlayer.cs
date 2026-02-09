using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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

        public LibrespotRingBufferPlayer(AudioEncodingProperties props)
        {
            _props = props;
        }

        public async Task InitializeAsync()
        {
            await WaitForRingBufferAsync();

            _capacityBytes = (int)librespot_audio_get_capacity().ToUInt32();
            _readPos = 0;
            librespot_audio_set_read_cursor((UIntPtr)0);

            _frameSize = (int)(_props.ChannelCount * (_props.BitsPerSample / 8));

            var settings = new AudioGraphSettings(AudioRenderCategory.Media)
            {
                EncodingProperties = _props
            };

            var result = await AudioGraph.CreateAsync(settings);
            if (result.Status != AudioGraphCreationStatus.Success)
                throw new InvalidOperationException($"AudioGraph creation failed: {result.Status}");

            _graph = result.Graph;

            var outResult = await _graph.CreateDeviceOutputNodeAsync();
            if (outResult.Status != AudioDeviceNodeCreationStatus.Success)
                throw new InvalidOperationException($"DeviceOutputNode creation failed: {outResult.Status}");

            _inputNode = _graph.CreateFrameInputNode(_props);
            _inputNode.QuantumStarted += OnQuantumStarted;
            _inputNode.AddOutgoingConnection(outResult.DeviceOutputNode);

            _graph.Start();
        }

        private async Task WaitForRingBufferAsync()
        {
            int waited = 0;
            while (true)
            {
                var ptr = librespot_audio_get_buffer();
                if (ptr != IntPtr.Zero)
                {
                    _bufferPtr = ptr;
                    return;
                }
                if (waited >= 5000) throw new InvalidOperationException("Ring Buffer timeout.");
                await Task.Delay(50);
                waited += 50;
            }
        }

        private void OnQuantumStarted(AudioFrameInputNode sender, FrameInputNodeQuantumStartedEventArgs args)
        {
            uint numSamplesNeeded = (uint)args.RequiredSamples;
            if (numSamplesNeeded == 0) return;

            int bytesRequested = (int)numSamplesNeeded * _frameSize;

            uint writePos = librespot_audio_get_write_cursor().ToUInt32();
            int available = (int)(((long)_capacityBytes + (int)writePos - _readPos) % _capacityBytes);

            if (available < _frameSize) return;

            int bytesToCopy = Math.Min(available, bytesRequested);
            bytesToCopy -= bytesToCopy % _frameSize;

            var frame = new AudioFrame((uint)bytesToCopy);

            using (AudioBuffer buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
            using (var reference = buffer.CreateReference())
            {
                var byteAccess = reference as IMemoryBufferByteAccess;
                if (byteAccess != null)
                {
                    byteAccess.GetBuffer(out IntPtr dataInPtr, out uint capacity);
                    byte[] tempManagedBuffer = new byte[bytesToCopy];
                    int firstChunkSize = Math.Min(bytesToCopy, _capacityBytes - _readPos);

                    Marshal.Copy(_bufferPtr + _readPos, tempManagedBuffer, 0, firstChunkSize);

                    if (bytesToCopy > firstChunkSize)
                    {
                        Marshal.Copy(_bufferPtr, tempManagedBuffer, firstChunkSize, bytesToCopy - firstChunkSize);
                    }

                    Marshal.Copy(tempManagedBuffer, 0, dataInPtr, bytesToCopy);

                    buffer.Length = (uint)bytesToCopy;
                }
            }

            _readPos = (_readPos + bytesToCopy) % _capacityBytes;
            librespot_audio_set_read_cursor((UIntPtr)_readPos);

            sender.AddFrame(frame);
        }

        public void Dispose()
        {
            _inputNode?.Stop();
            _graph?.Stop();
            _inputNode?.Dispose();
            _graph?.Dispose();
        }

        [ComImport]
        [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMemoryBufferByteAccess
        {
            void GetBuffer(out IntPtr buffer, out uint capacity);
        }
    }
}