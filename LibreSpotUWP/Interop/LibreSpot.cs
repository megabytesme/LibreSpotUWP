using System;
using System.Runtime.InteropServices;

namespace LibreSpotUWP.Interop
{
    public static class Librespot
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        public delegate bool LibrespotKeyCallback(
            IntPtr trackIdPtr,
            IntPtr fileIdPtr,
            IntPtr keyOutPtr,
            IntPtr userData
        );

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void LibrespotKeySaveCallback(
            IntPtr trackIdPtr,
            IntPtr keyPtr,
            IntPtr userData
        );

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void LibrespotKeyRemoveCallback(IntPtr trackIdPtr, IntPtr userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void LibrespotCallback(IntPtr evt, IntPtr userData);

        [StructLayout(LayoutKind.Sequential)]
        public struct LibrespotConfig
        {
            public IntPtr device_name;
            public IntPtr device_type;
            public IntPtr cache_dir;
            public IntPtr persisted_cache_dir;

            [MarshalAs(UnmanagedType.U1)]
            public bool enable_discovery;

            [MarshalAs(UnmanagedType.U1)]
            public bool enable_volume_normalisation;

            public Bitrate bitrate;
            public AudioFormat format;
            public ushort initial_volume;

            public IntPtr username;
            public IntPtr password;
            public IntPtr auth_blob;
            public IntPtr access_token;

            public LibrespotKeyCallback key_callback;
            public LibrespotKeySaveCallback key_save_callback;
            public LibrespotKeyRemoveCallback key_remove_callback;
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
            Panic = 9,
            ShuffleChanged = 10,
            RepeatChanged = 11,
            AutoPlayChanged = 12,
            Seeked = 13,
            PositionCorrection = 14,
            PlaybackLoading = 15,
            PlaybackUnavailable = 16,
            EndOfTrack = 17,
            ClientChanged = 18,
            ExplicitFilterChanged = 19,
            PlayRequestIdChanged = 20,
            AddedToQueue = 21,
            Preloading = 22,
            TimeToPreloadNextTrack = 23,
            PositionChanged = 24,
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

        [StructLayout(LayoutKind.Sequential)]
        public struct EventData
        {
            public ulong play_request_id;
            public IntPtr track_uri;
            public uint position_ms;
            public uint duration_ms;
            public ushort volume;

            [MarshalAs(UnmanagedType.U1)]
            public bool is_playing;

            [MarshalAs(UnmanagedType.U1)]
            public bool shuffle;

            public uint repeat_mode;

            [MarshalAs(UnmanagedType.U1)]
            public bool auto_play;

            [MarshalAs(UnmanagedType.U1)]
            public bool filter_explicit;

            public TrackMetadata track;

            public IntPtr session_user;
            public IntPtr client_name;
            public IntPtr log_msg;
            public ulong audio_generation;

            [MarshalAs(UnmanagedType.U1)]
            public bool was_preloaded;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LibrespotEvent
        {
            public EventType event_type;
            public EventData data;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FfiImage
        {
            public IntPtr url;
            public int width;
            public int height;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FfiArtistSummary
        {
            public IntPtr id;
            public IntPtr uri;
            public IntPtr name;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FfiAlbumSummary
        {
            public IntPtr id;
            public IntPtr uri;
            public IntPtr name;
            public IntPtr album_type;
            public IntPtr release_date;
            public int total_tracks;
            public IntPtr images;
            public UIntPtr image_count;
            public IntPtr artists;
            public UIntPtr artist_count;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FfiSimpleTrack
        {
            public IntPtr id;
            public IntPtr uri;
            public IntPtr name;
            public int duration_ms;
            public int disc_number;
            public int track_number;
            public IntPtr artists;
            public UIntPtr artist_count;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FfiTrack
        {
            public IntPtr id;
            public IntPtr uri;
            public IntPtr name;
            public int duration_ms;
            public int disc_number;
            public int track_number;
            public IntPtr artists;
            public UIntPtr artist_count;
            public IntPtr album;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FfiAlbum
        {
            public IntPtr id;
            public IntPtr uri;
            public IntPtr name;
            public IntPtr album_type;
            public IntPtr release_date;
            public int total_tracks;
            public IntPtr images;
            public UIntPtr image_count;
            public IntPtr artists;
            public UIntPtr artist_count;
            public IntPtr tracks;
            public UIntPtr track_count;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FfiArtist
        {
            public IntPtr id;
            public IntPtr uri;
            public IntPtr name;
            public IntPtr images;
            public UIntPtr image_count;
            public IntPtr albums;
            public UIntPtr album_count;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FfiOwner
        {
            public IntPtr id;
            public IntPtr display_name;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FfiPlaylistSummary
        {
            public IntPtr id;
            public IntPtr uri;
            public IntPtr name;
            public IntPtr images;
            public UIntPtr image_count;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FfiPlaylist
        {
            public IntPtr id;
            public IntPtr uri;
            public IntPtr name;
            public IntPtr images;
            public UIntPtr image_count;
            public IntPtr owner;
            public IntPtr tracks;
            public UIntPtr track_count;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FfiUserProfile
        {
            public IntPtr id;
            public IntPtr uri;
            public IntPtr display_name;
            public IntPtr email;
            public IntPtr country;
            public IntPtr images;
            public UIntPtr image_count;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FfiPlaylistList
        {
            public IntPtr items;
            public UIntPtr item_count;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FfiTrackList
        {
            public IntPtr items;
            public UIntPtr item_count;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FfiArtistList
        {
            public IntPtr items;
            public UIntPtr item_count;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FfiSearch
        {
            public IntPtr tracks;
            public UIntPtr track_count;
            public IntPtr albums;
            public UIntPtr album_count;
            public IntPtr artists;
            public UIntPtr artist_count;
            public IntPtr playlists;
            public UIntPtr playlist_count;
        }

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr librespot_new(LibrespotConfig config, LibrespotCallback cb, IntPtr userData);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_free(IntPtr inst);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_load(IntPtr inst, IntPtr contextUri, IntPtr startUri, bool play);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_load_tracks(IntPtr inst, IntPtr contextUri, IntPtr tracksJson, IntPtr startUri, bool play);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_play(IntPtr inst);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_pause(IntPtr inst);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_stop(IntPtr inst);

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
        public static extern void librespot_audio_set_read_sequence(ulong sequence);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_audio_get_state(
            out ulong generation,
            out ulong generationStartSequence,
            out ulong writeSequence);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool librespot_cache_set_persisted(
            IntPtr inst,
            IntPtr fileIdHex,
            [MarshalAs(UnmanagedType.U1)] bool persisted
        );

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool librespot_track_set_persisted(
            IntPtr inst,
            IntPtr trackUri,
            [MarshalAs(UnmanagedType.U1)] bool persisted
        );

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr librespot_appdata_get(
            IntPtr inst,
            int kind,
            IntPtr argument
        );

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr librespot_last_error_get();

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_string_free(IntPtr value);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr librespot_track_get(IntPtr inst, IntPtr argument);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr librespot_album_get(IntPtr inst, IntPtr argument);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr librespot_artist_get(IntPtr inst, IntPtr argument);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr librespot_playlist_get(IntPtr inst, IntPtr argument);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr librespot_user_profile_get(IntPtr inst, IntPtr argument);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr librespot_user_playlists_get(IntPtr inst, IntPtr argument);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr librespot_saved_tracks_get(IntPtr inst, IntPtr argument);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr librespot_followed_artists_get(IntPtr inst, IntPtr argument);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr librespot_search_get(IntPtr inst, IntPtr argument);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_track_free(IntPtr value);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_album_free(IntPtr value);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_artist_free(IntPtr value);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_playlist_free(IntPtr value);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_user_profile_free(IntPtr value);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_playlist_list_free(IntPtr value);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_track_list_free(IntPtr value);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_artist_list_free(IntPtr value);

        [DllImport("librespot.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void librespot_search_free(IntPtr value);
    }
}
