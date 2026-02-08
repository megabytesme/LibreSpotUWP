using System;
using System.Threading.Tasks;
using Windows.Media.Audio;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using static LibreSpotUWP.Librespot;

namespace LibreSpotUWP
{
    public sealed class AudioFormatProbeResult
    {
        public AudioFormat LibrespotFormat { get; set; }
        public AudioEncodingProperties EncodingProperties { get; set; }
        public int BytesPerSample { get; set; }
    }

    public static class AudioFormatProbe
    {
        public static async Task<AudioFormatProbeResult> ProbeAsync()
        {
            const int sampleRate = 44100;
            const int channels = 2;

            var candidates = new[]
            {
                new { LibrespotId = 0, Bits = 64, Bytes = 8 }, // F64
                new { LibrespotId = 1, Bits = 32, Bytes = 4 }, // F32
                new { LibrespotId = 2, Bits = 32, Bytes = 4 }, // S32
                new { LibrespotId = 3, Bits = 24, Bytes = 4 }, // S24
                new { LibrespotId = 4, Bits = 24, Bytes = 3 }, // S24_3 (packed 3-byte)
                new { LibrespotId = 5, Bits = 16, Bytes = 2 }, // S16
            };

            foreach (var c in candidates)
            {
                var props = AudioEncodingProperties.CreatePcm(
                    (uint)sampleRate,
                    (uint)channels,
                    (uint)c.Bits
                );

                var settings = new AudioGraphSettings(AudioRenderCategory.Media)
                {
                    EncodingProperties = props
                };

                var result = await AudioGraph.CreateAsync(settings);
                if (result.Status != AudioGraphCreationStatus.Success)
                    continue;

                result.Graph.Dispose();

                int uwpBytesPerSample = (int)(props.BitsPerSample / 8);

                if (uwpBytesPerSample != c.Bytes)
                    continue;

                int frameSize = uwpBytesPerSample * channels;
                if (frameSize <= 0)
                    continue;

                return new AudioFormatProbeResult
                {
                    LibrespotFormat = (AudioFormat)c.LibrespotId,
                    EncodingProperties = props,
                    BytesPerSample = c.Bytes
                };
            }

            throw new InvalidOperationException("No compatible librespot/UWP PCM format found.");
        }
    }
}