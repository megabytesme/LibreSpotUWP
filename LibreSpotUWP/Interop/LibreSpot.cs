using System;
using System.Runtime.InteropServices;

namespace LibreSpotUWP.Interop
{
    public static class Librespot
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void LibrespotCallback(IntPtr evt, IntPtr userData);

        public struct LibrespotConfig
        {
            public IntPtr device_name;
            public IntPtr device_type;
            public IntPtr cache_dir;

            [MarshalAs(UnmanagedType.U1)]
            public bool enable_discovery;

            [MarshalAs(UnmanagedType.U1)]
            public bool enable_volume_normalisation;

            public Bitrate bitrate;
            public AudioFormat format;

            public IntPtr username;
            public IntPtr password;
            public IntPtr auth_blob;
            public IntPtr access_token;
        }

        public enum Bitrate : int
        {
            B96 = 96,
            B160 = 160,
            B320 = 320
        }

        public enum AudioFormat : int
        {
            F64 = 0,
            F32 = 1,
            S32 = 2,
            S24 = 3,
            S24_3 = 4,
            S16 = 5
        }

        public enum EventType : int
        {
            LogMessage = 0,
            SessionConnected = 1,
            SessionDisconnected = 2,
            AuthNeeded = 3,
            TrackChanged = 4,
            PlaybackPaused = 5,
            PlaybackResumed = 6,
            PlaybackStopped = 7,
            VolumeChanged = 8,
            Panic = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TrackMetadata
        {
            public IntPtr uri;
            public IntPtr name;
            public IntPtr artist;
            public IntPtr album;
            public IntPtr cover_url;
            public uint duration_ms;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct EventData
        {
            [FieldOffset(0)]
            public IntPtr log_msg;

            [FieldOffset(0)]
            public TrackMetadata track;

            [FieldOffset(0)]
            public ushort volume;

            [FieldOffset(0)]
            public IntPtr session_user;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LibrespotEvent
        {
            public EventType event_type;
            public EventData data;
        }

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr librespot_new(LibrespotConfig config, LibrespotCallback cb, IntPtr userData);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_free(IntPtr inst);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_load(IntPtr inst, IntPtr uri, bool play);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_play(IntPtr inst);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_pause(IntPtr inst);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_next(IntPtr inst);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_prev(IntPtr inst);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_seek(IntPtr inst, uint posMs);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_set_volume(IntPtr inst, ushort volume);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_set_shuffle(IntPtr inst, bool enabled);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_set_repeat(IntPtr inst, uint mode);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern uint librespot_get_position_ms(IntPtr inst);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern uint librespot_get_duration_ms(IntPtr inst);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern TrackMetadata librespot_get_current_track_info(IntPtr inst);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr librespot_audio_get_buffer();

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr librespot_audio_get_capacity();

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr librespot_audio_get_write_cursor();

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_audio_set_read_cursor(UIntPtr pos);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern uint librespot_audio_get_format();

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern uint librespot_audio_get_sample_rate();

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern uint librespot_audio_get_channels();
    }
}