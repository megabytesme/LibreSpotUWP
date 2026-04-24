using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Interop;
using LibreSpotUWP.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.System.Profile;
using static LibreSpotUWP.Interop.Librespot;

namespace LibreSpotUWP.Services
{
    public sealed class LibrespotService : ILibrespotService
    {
        private readonly object _stateLock = new object();

        private IntPtr _dllHandle = IntPtr.Zero;
        private IntPtr _instance = IntPtr.Zero;
        private LibrespotCallback _callbackDelegate;
        private readonly Librespot.LibrespotKeyCallback _keyCallbackDelegate;
        private readonly Librespot.LibrespotKeySaveCallback _keySaveDelegate;
        private readonly Librespot.LibrespotKeyRemoveCallback _keyRemoveDelegate;

        private readonly AudioKeyCache _audioKeyCache;

        private AudioFormatProbeResult _audioFormat;

        private LibrespotSessionState _session = new LibrespotSessionState();
        private LibrespotPlaybackState _playbackState = LibrespotPlaybackState.Stopped;
        private LibrespotTrackInfo _currentTrack;
        private ushort _volume;

        private bool _initialized;
        private bool _disposed;
        private bool _shuffle;
        private uint _repeatMode;

        private string ts = DateTime.Now.ToString("HH:mm:ss");

        public AudioEncodingProperties EncodingProperties => _audioFormat?.EncodingProperties;
        public bool HasInstance => _instance != IntPtr.Zero;
        public LibrespotSessionState Session => _session;
        public LibrespotPlaybackState PlaybackState => _playbackState;
        public LibrespotTrackInfo CurrentTrack => _currentTrack;
        public ushort Volume => _volume;
        public bool Shuffle => _shuffle;
        public uint RepeatMode => _repeatMode;
        public string ConnectedUser { get; private set; }
        public string ActiveClientName { get; private set; }
        public bool IsAutoPlayEnabled { get; private set; }
        public bool IsExplicitFilterEnabled { get; private set; }

        public event EventHandler<LibrespotSessionState> SessionStateChanged;
        public event EventHandler<LibrespotTrackInfo> TrackChanged;
        public event EventHandler<LibrespotPlaybackState> PlaybackStateChanged;
        public event EventHandler<ushort> VolumeChanged;
        public event EventHandler<string> EndOfTrack;
        public event EventHandler<string> LogMessage;
        public event EventHandler<string> Panic;
        public event EventHandler<bool> ShuffleChanged;
        public event EventHandler<uint> RepeatChanged;
        public event EventHandler<uint> Seeked;

        public LibrespotService(AudioKeyCache keyCache)
        {
            _audioKeyCache = keyCache;

            _keyCallbackDelegate = OnKeyRequested;
            _keySaveDelegate = OnKeyReceived;
            _keyRemoveDelegate = OnKeyRemoved;
        }

        public async Task InitializeAsync()
        {
            ThrowIfDisposed();
            if (_initialized) return;

            _dllHandle = NativeProbe.TryLoadLibreSpot();
            if (_dllHandle == IntPtr.Zero)
                throw new InvalidOperationException("Failed to load librespot.dll");

            await _audioKeyCache.InitializeAsync().ConfigureAwait(false);

            _audioFormat = await AudioFormatProbe.ProbeAsync().ConfigureAwait(false);

            _callbackDelegate = OnLibrespotEvent;

            _initialized = true;
        }

        private bool OnKeyRequested(IntPtr trackIdPtr, IntPtr fileIdPtr, IntPtr keyOutPtr, IntPtr userData)
        {
            byte[] trackId = new byte[16];
            Marshal.Copy(trackIdPtr, trackId, 0, 16);
            string trackIdHex = BitConverter.ToString(trackId).Replace("-", "").ToLowerInvariant();

            byte[] key = _audioKeyCache.GetKeySync(trackIdHex);

            if (key != null)
            {
                Marshal.Copy(key, 0, keyOutPtr, 16);
                LogService.Info($"[LibrespotService.OnKeyRequested] Cache hit for trackId={trackIdHex}.");
                return true;
            }

            LogService.Warn($"[LibrespotService.OnKeyRequested] Cache miss for trackId={trackIdHex}.");
            return false;
        }

        private void OnKeyReceived(IntPtr trackIdPtr, IntPtr keyPtr, IntPtr userData)
        {
            byte[] trackIdBytes = new byte[16];
            byte[] keyBytes = new byte[16];

            Marshal.Copy(trackIdPtr, trackIdBytes, 0, 16);
            Marshal.Copy(keyPtr, keyBytes, 0, 16);

            string trackIdHex = BitConverter.ToString(trackIdBytes).Replace("-", "").ToLowerInvariant();

            LogService.Info($"[LibrespotService.OnKeyReceived] Saving key for trackId={trackIdHex}.");
            if (_audioKeyCache.IsPersisted(trackIdHex))
                _ = _audioKeyCache.AddPersistedKeyAsync(trackIdHex, keyBytes);
            else
                _ = _audioKeyCache.AddVolatileKeyAsync(trackIdHex, keyBytes);
        }

        private void OnKeyRemoved(IntPtr trackIdPtr, IntPtr userData)
        {
            byte[] trackId = new byte[16];
            Marshal.Copy(trackIdPtr, trackId, 0, 16);
            string trackIdHex = BitConverter.ToString(trackId).Replace("-", "").ToLowerInvariant();

            LogService.Info($"[LibrespotService.OnKeyRemoved] Removing volatile key for trackId={trackIdHex}.");
            _ = _audioKeyCache.RemoveVolatileKeyAsync(trackIdHex);
        }

        public async Task ConnectWithAccessTokenAsync(string accessToken)
        {
            ThrowIfDisposed();
            if (!_initialized)
                throw new InvalidOperationException("LibrespotService not initialized.");

            if (string.IsNullOrWhiteSpace(accessToken))
                throw new ArgumentException("Access token must not be null or empty.", nameof(accessToken));

            LogService.Info("[LibrespotService.ConnectWithAccessTokenAsync] Connecting with access token.");
            await RecreateInstanceWithAccessTokenAsync(accessToken).ConfigureAwait(false);
        }

        public Task<LibrespotTrackData> GetTrackAsync(string trackUri)
        {
            return GetTypedPayloadAsync(trackUri, Librespot.librespot_track_get, Librespot.librespot_track_free, ReadTrack);
        }

        public Task<LibrespotAlbumData> GetAlbumAsync(string albumUri)
        {
            return GetTypedPayloadAsync(albumUri, Librespot.librespot_album_get, Librespot.librespot_album_free, ReadAlbum);
        }

        public Task<LibrespotArtistData> GetArtistAsync(string artistUri)
        {
            return GetTypedPayloadAsync(artistUri, Librespot.librespot_artist_get, Librespot.librespot_artist_free, ReadArtist);
        }

        public Task<LibrespotPlaylistData> GetPlaylistAsync(string playlistUri)
        {
            return GetTypedPayloadAsync(playlistUri, Librespot.librespot_playlist_get, Librespot.librespot_playlist_free, ReadPlaylist);
        }

        public Task<LibrespotUserProfileData> GetUserProfileAsync(string userId)
        {
            return GetTypedPayloadAsync(userId, Librespot.librespot_user_profile_get, Librespot.librespot_user_profile_free, ReadUserProfile);
        }

        public Task<LibrespotPlaylistListData> GetUserPlaylistsAsync(string userId)
        {
            return GetTypedPayloadAsync(userId, Librespot.librespot_user_playlists_get, Librespot.librespot_playlist_list_free, ReadPlaylistList);
        }

        public Task<LibrespotTrackListData> GetSavedTracksAsync(string userId)
        {
            return GetTypedPayloadAsync(userId, Librespot.librespot_saved_tracks_get, Librespot.librespot_track_list_free, ReadTrackList);
        }

        public Task<LibrespotArtistListData> GetFollowedArtistsAsync(string userId)
        {
            return GetTypedPayloadAsync(userId, Librespot.librespot_followed_artists_get, Librespot.librespot_artist_list_free, ReadArtistList);
        }

        public Task<LibrespotSearchData> SearchAsync(string query)
        {
            return GetTypedPayloadAsync(query, Librespot.librespot_search_get, Librespot.librespot_search_free, ReadSearch);
        }

        private static string GetLastNativeError()
        {
            var errorPtr = Librespot.librespot_last_error_get();
            if (errorPtr == IntPtr.Zero)
                return null;

            try
            {
                return Marshal.PtrToStringAnsi(errorPtr);
            }
            finally
            {
                Librespot.librespot_string_free(errorPtr);
            }
        }

        private Task<T> GetTypedPayloadAsync<T>(
            string argument,
            Func<IntPtr, IntPtr, IntPtr> getter,
            Action<IntPtr> freer,
            Func<IntPtr, T> mapper)
        {
            ThrowIfDisposed();
            if (!_initialized)
                throw new InvalidOperationException("LibrespotService not initialized.");
            if (_instance == IntPtr.Zero)
                throw new InvalidOperationException("Not connected.");
            if (string.IsNullOrWhiteSpace(argument))
                throw new ArgumentException("Argument must not be null or empty.", nameof(argument));

            return Task.Run(() =>
            {
                IntPtr argumentPtr = Marshal.StringToHGlobalAnsi(argument);
                try
                {
                    var resultPtr = getter(_instance, argumentPtr);
                    if (resultPtr == IntPtr.Zero)
                    {
                        var lastError = GetLastNativeError();
                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(lastError)
                                ? string.Format("librespot typed request returned null for {0}.", argument)
                                : string.Format("librespot typed request returned null for {0}. Native error: {1}", argument, lastError));
                    }

                    try
                    {
                        return mapper(resultPtr);
                    }
                    finally
                    {
                        freer(resultPtr);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(argumentPtr);
                }
            });
        }

        private static string ReadString(IntPtr value)
        {
            if (value == IntPtr.Zero)
                return null;

            int length = 0;
            while (Marshal.ReadByte(value, length) != 0)
                length++;

            if (length == 0)
                return string.Empty;

            byte[] buffer = new byte[length];
            Marshal.Copy(value, buffer, 0, length);
            return Encoding.UTF8.GetString(buffer, 0, buffer.Length);
        }

        private static int ReadCount(UIntPtr count)
        {
            return checked((int)count.ToUInt64());
        }

        private static List<T> ReadList<TStruct, T>(IntPtr itemsPtr, UIntPtr count, Func<TStruct, T> mapper)
        {
            var result = new List<T>();
            if (itemsPtr == IntPtr.Zero)
                return result;

            int length = ReadCount(count);
            int size = Marshal.SizeOf(typeof(TStruct));
            for (int i = 0; i < length; i++)
            {
                var itemPtr = IntPtr.Add(itemsPtr, i * size);
                var item = (TStruct)Marshal.PtrToStructure(itemPtr, typeof(TStruct));
                result.Add(mapper(item));
            }

            return result;
        }

        private static LibrespotImageData ReadImage(FfiImage image)
        {
            return new LibrespotImageData
            {
                Url = ReadString(image.url),
                Width = image.width,
                Height = image.height
            };
        }

        private static LibrespotArtistSummaryData ReadArtistSummary(FfiArtistSummary artist)
        {
            return new LibrespotArtistSummaryData
            {
                Id = ReadString(artist.id),
                Uri = ReadString(artist.uri),
                Name = ReadString(artist.name)
            };
        }

        private static LibrespotAlbumSummaryData ReadAlbumSummary(FfiAlbumSummary album)
        {
            return new LibrespotAlbumSummaryData
            {
                Id = ReadString(album.id),
                Uri = ReadString(album.uri),
                Name = ReadString(album.name),
                AlbumType = ReadString(album.album_type),
                ReleaseDate = ReadString(album.release_date),
                TotalTracks = album.total_tracks,
                Images = ReadList<FfiImage, LibrespotImageData>(album.images, album.image_count, ReadImage),
                Artists = ReadList<FfiArtistSummary, LibrespotArtistSummaryData>(album.artists, album.artist_count, ReadArtistSummary)
            };
        }

        private static LibrespotSimpleTrackData ReadSimpleTrack(FfiSimpleTrack track)
        {
            return new LibrespotSimpleTrackData
            {
                Id = ReadString(track.id),
                Uri = ReadString(track.uri),
                Name = ReadString(track.name),
                DurationMs = track.duration_ms,
                DiscNumber = track.disc_number,
                TrackNumber = track.track_number,
                Artists = ReadList<FfiArtistSummary, LibrespotArtistSummaryData>(track.artists, track.artist_count, ReadArtistSummary)
            };
        }

        private static LibrespotTrackData ReadTrack(IntPtr trackPtr)
        {
            return ReadTrackValue((FfiTrack)Marshal.PtrToStructure(trackPtr, typeof(FfiTrack)));
        }

        private static LibrespotTrackData ReadTrackValue(FfiTrack track)
        {
            return new LibrespotTrackData
            {
                Id = ReadString(track.id),
                Uri = ReadString(track.uri),
                Name = ReadString(track.name),
                DurationMs = track.duration_ms,
                DiscNumber = track.disc_number,
                TrackNumber = track.track_number,
                Artists = ReadList<FfiArtistSummary, LibrespotArtistSummaryData>(track.artists, track.artist_count, ReadArtistSummary),
                Album = track.album == IntPtr.Zero
                    ? null
                    : ReadAlbumSummary((FfiAlbumSummary)Marshal.PtrToStructure(track.album, typeof(FfiAlbumSummary)))
            };
        }

        private static LibrespotAlbumData ReadAlbum(IntPtr albumPtr)
        {
            var album = (FfiAlbum)Marshal.PtrToStructure(albumPtr, typeof(FfiAlbum));
            return new LibrespotAlbumData
            {
                Id = ReadString(album.id),
                Uri = ReadString(album.uri),
                Name = ReadString(album.name),
                AlbumType = ReadString(album.album_type),
                ReleaseDate = ReadString(album.release_date),
                TotalTracks = album.total_tracks,
                Images = ReadList<FfiImage, LibrespotImageData>(album.images, album.image_count, ReadImage),
                Artists = ReadList<FfiArtistSummary, LibrespotArtistSummaryData>(album.artists, album.artist_count, ReadArtistSummary),
                Tracks = ReadList<FfiSimpleTrack, LibrespotSimpleTrackData>(album.tracks, album.track_count, ReadSimpleTrack)
            };
        }

        private static LibrespotArtistData ReadArtist(IntPtr artistPtr)
        {
            var artist = (FfiArtist)Marshal.PtrToStructure(artistPtr, typeof(FfiArtist));
            return new LibrespotArtistData
            {
                Id = ReadString(artist.id),
                Uri = ReadString(artist.uri),
                Name = ReadString(artist.name),
                Images = ReadList<FfiImage, LibrespotImageData>(artist.images, artist.image_count, ReadImage),
                Albums = ReadList<FfiAlbumSummary, LibrespotAlbumSummaryData>(artist.albums, artist.album_count, ReadAlbumSummary)
            };
        }

        private static LibrespotOwnerData ReadOwner(IntPtr ownerPtr)
        {
            if (ownerPtr == IntPtr.Zero)
                return null;

            var owner = (FfiOwner)Marshal.PtrToStructure(ownerPtr, typeof(FfiOwner));
            return new LibrespotOwnerData
            {
                Id = ReadString(owner.id),
                DisplayName = ReadString(owner.display_name)
            };
        }

        private static LibrespotPlaylistSummaryData ReadPlaylistSummary(FfiPlaylistSummary playlist)
        {
            return new LibrespotPlaylistSummaryData
            {
                Id = ReadString(playlist.id),
                Uri = ReadString(playlist.uri),
                Name = ReadString(playlist.name),
                Images = ReadList<FfiImage, LibrespotImageData>(playlist.images, playlist.image_count, ReadImage)
            };
        }

        private static LibrespotPlaylistData ReadPlaylist(IntPtr playlistPtr)
        {
            var playlist = (FfiPlaylist)Marshal.PtrToStructure(playlistPtr, typeof(FfiPlaylist));
            return new LibrespotPlaylistData
            {
                Id = ReadString(playlist.id),
                Uri = ReadString(playlist.uri),
                Name = ReadString(playlist.name),
                Images = ReadList<FfiImage, LibrespotImageData>(playlist.images, playlist.image_count, ReadImage),
                Owner = ReadOwner(playlist.owner),
                Tracks = ReadList<FfiTrack, LibrespotTrackData>(playlist.tracks, playlist.track_count, ReadTrackValue)
            };
        }

        private static LibrespotUserProfileData ReadUserProfile(IntPtr profilePtr)
        {
            var profile = (FfiUserProfile)Marshal.PtrToStructure(profilePtr, typeof(FfiUserProfile));
            return new LibrespotUserProfileData
            {
                Id = ReadString(profile.id),
                Uri = ReadString(profile.uri),
                DisplayName = ReadString(profile.display_name),
                Email = ReadString(profile.email),
                Country = ReadString(profile.country),
                Images = ReadList<FfiImage, LibrespotImageData>(profile.images, profile.image_count, ReadImage)
            };
        }

        private static LibrespotPlaylistListData ReadPlaylistList(IntPtr listPtr)
        {
            var list = (FfiPlaylistList)Marshal.PtrToStructure(listPtr, typeof(FfiPlaylistList));
            return new LibrespotPlaylistListData
            {
                Items = ReadList<FfiPlaylistSummary, LibrespotPlaylistSummaryData>(list.items, list.item_count, ReadPlaylistSummary)
            };
        }

        private static LibrespotTrackListData ReadTrackList(IntPtr listPtr)
        {
            var list = (FfiTrackList)Marshal.PtrToStructure(listPtr, typeof(FfiTrackList));
            return new LibrespotTrackListData
            {
                Items = ReadList<FfiTrack, LibrespotTrackData>(list.items, list.item_count, ReadTrackValue)
            };
        }

        private static LibrespotArtistListData ReadArtistList(IntPtr listPtr)
        {
            var list = (FfiArtistList)Marshal.PtrToStructure(listPtr, typeof(FfiArtistList));
            return new LibrespotArtistListData
            {
                Items = ReadList<FfiArtistSummary, LibrespotArtistSummaryData>(list.items, list.item_count, ReadArtistSummary)
            };
        }

        private static LibrespotSearchData ReadSearch(IntPtr searchPtr)
        {
            var search = (FfiSearch)Marshal.PtrToStructure(searchPtr, typeof(FfiSearch));
            return new LibrespotSearchData
            {
                Tracks = ReadList<FfiTrack, LibrespotTrackData>(search.tracks, search.track_count, ReadTrackValue),
                Albums = ReadList<FfiAlbumSummary, LibrespotAlbumSummaryData>(search.albums, search.album_count, ReadAlbumSummary),
                Artists = ReadList<FfiArtistSummary, LibrespotArtistSummaryData>(search.artists, search.artist_count, ReadArtistSummary),
                Playlists = ReadList<FfiPlaylistSummary, LibrespotPlaylistSummaryData>(search.playlists, search.playlist_count, ReadPlaylistSummary)
            };
        }

        public async Task LoadAndPlayAsync(string contextUri, string startUri = null)
        {
            ThrowIfDisposed();
            if (!_initialized) throw new InvalidOperationException("Not initialized.");
            if (_instance == IntPtr.Zero) throw new InvalidOperationException("Not connected.");

            LogService.Info($"[LibrespotService.LoadAndPlayAsync] context={contextUri}, start={startUri ?? "(null)"}.");

            IntPtr contextPtr = Marshal.StringToHGlobalAnsi(contextUri);
            IntPtr startPtr = startUri != null ? Marshal.StringToHGlobalAnsi(startUri) : IntPtr.Zero;

            try
            {
                Librespot.librespot_load(_instance, contextPtr, startPtr, true);
            }
            finally
            {
                Marshal.FreeHGlobal(contextPtr);
                if (startPtr != IntPtr.Zero) Marshal.FreeHGlobal(startPtr);
            }

            await Task.CompletedTask;
        }

        public uint GetPositionMs()
        {
            if (_instance == IntPtr.Zero) return 0;
            return Librespot.librespot_get_position_ms(_instance);
        }

        public Task PauseAsync()
        {
            ThrowIfDisposed();
            if (_instance != IntPtr.Zero)
            {
                Librespot.librespot_pause(_instance);
            }
            return Task.CompletedTask;
        }

        public Task ResumeAsync()
        {
            ThrowIfDisposed();
            if (_instance != IntPtr.Zero)
            {
                Librespot.librespot_play(_instance);
            }
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            ThrowIfDisposed();
            if (_instance != IntPtr.Zero)
            {
                Librespot.librespot_pause(_instance);
            }
            return Task.CompletedTask;
        }

        public Task SetVolumeAsync(ushort volume)
        {
            ThrowIfDisposed();
            if (_instance != IntPtr.Zero)
            {
                Librespot.librespot_set_volume(_instance, volume);
            }
            return Task.CompletedTask;
        }

        public Task SetShuffleAsync(bool enabled)
        {
            ThrowIfDisposed();
            if (_instance != IntPtr.Zero)
            {
                Librespot.librespot_set_shuffle(_instance, enabled);
            }
            return Task.CompletedTask;
        }

        public Task SetRepeatAsync(uint mode)
        {
            ThrowIfDisposed();
            if (_instance != IntPtr.Zero)
            {
                Librespot.librespot_set_repeat(_instance, mode);
            }
            return Task.CompletedTask;
        }

        public void Seek(uint posMs)
        {
            if (_instance != IntPtr.Zero)
                Librespot.librespot_seek(_instance, posMs);
        }

        public void Next()
        {
            if (_instance != IntPtr.Zero)
                Librespot.librespot_next(_instance);
        }

        public void Previous()
        {
            if (_instance != IntPtr.Zero)
                Librespot.librespot_prev(_instance);
        }

        public async Task SetCachedTrackPersistedAsync(string fileIdHex, string trackIdHex, bool persisted)
        {
            ThrowIfDisposed();
            if (!_initialized) throw new InvalidOperationException("Not initialized.");
            if (_instance == IntPtr.Zero) throw new InvalidOperationException("Not connected.");

            if (string.IsNullOrWhiteSpace(fileIdHex))
                throw new ArgumentException("fileIdHex must not be null or empty.", nameof(fileIdHex));

            if (string.IsNullOrWhiteSpace(trackIdHex))
                throw new ArgumentException("trackIdHex must not be null or empty.", nameof(trackIdHex));

            IntPtr fileIdPtr = Marshal.StringToHGlobalAnsi(fileIdHex);

            try
            {
                if (persisted)
                    _audioKeyCache.MarkPersisted(trackIdHex);
                else
                    _audioKeyCache.MarkVolatile(trackIdHex);

                bool ok = Librespot.librespot_cache_set_persisted(_instance, fileIdPtr, persisted);
                if (!ok)
                    throw new InvalidOperationException("librespot_cache_set_persisted returned false.");

                if (persisted)
                    await _audioKeyCache.MoveKeyToPersistedAsync(trackIdHex).ConfigureAwait(false);
                else
                    await _audioKeyCache.MoveKeyToVolatileAsync(trackIdHex).ConfigureAwait(false);
            }
            finally
            {
                Marshal.FreeHGlobal(fileIdPtr);
            }
        }

        public async Task SetTrackPersistedAsync(string trackUri, bool persisted)
        {
            ThrowIfDisposed();
            if (!_initialized) throw new InvalidOperationException("Not initialized.");
            if (_instance == IntPtr.Zero) throw new InvalidOperationException("Not connected.");
            if (string.IsNullOrWhiteSpace(trackUri))
                throw new ArgumentException("trackUri must not be null or empty.", nameof(trackUri));

            LogService.Info($"[LibrespotService.SetTrackPersistedAsync] Persisted={persisted} track={trackUri}.");

            var trackIdHex = SpotifyIdHelper.TrackUriToHexId(trackUri);
            if (string.IsNullOrWhiteSpace(trackIdHex))
                throw new InvalidOperationException("Unable to derive Spotify track ID from URI.");

            IntPtr trackUriPtr = Marshal.StringToHGlobalAnsi(trackUri);

            try
            {
                if (persisted)
                    _audioKeyCache.MarkPersisted(trackIdHex);
                else
                    _audioKeyCache.MarkVolatile(trackIdHex);

                bool ok = await Task.Run(() =>
                    Librespot.librespot_track_set_persisted(_instance, trackUriPtr, persisted)).ConfigureAwait(false);
                if (!ok)
                    throw new InvalidOperationException("librespot_track_set_persisted returned false.");

                // Give librespot a brief window to request/save the audio key before we move it.
                await Task.Delay(150).ConfigureAwait(false);

                if (persisted)
                    await _audioKeyCache.MoveKeyToPersistedAsync(trackIdHex).ConfigureAwait(false);
                else
                    await _audioKeyCache.MoveKeyToVolatileAsync(trackIdHex).ConfigureAwait(false);
            }
            finally
            {
                Marshal.FreeHGlobal(trackUriPtr);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_instance != IntPtr.Zero)
            {
                Librespot.librespot_free(_instance);
                _instance = IntPtr.Zero;
            }

            if (_dllHandle != IntPtr.Zero)
            {
                NativeProbe.TryFree(_dllHandle);
                _dllHandle = IntPtr.Zero;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LibrespotService));
        }

        private void OnLibrespotEvent(IntPtr evtPtr, IntPtr userData)
        {
            var evt = Marshal.PtrToStructure<LibrespotEvent>(evtPtr);
            HandleEvent(evt);
        }

        private void HandleEvent(LibrespotEvent evt)
        {
            string logPrefix = $"{ts} [LibreSpot Event:{evt.event_type}]";

            switch (evt.event_type)
            {
                case EventType.LogMessage:
                    string msg = Marshal.PtrToStringAnsi(evt.data.log_msg);
                    Debug.WriteLine($"{ts} [LibreSpot Internal] {msg}");
                    LogMessage?.Invoke(this, msg);
                    break;

                case EventType.TrackChanged:
                    var t = evt.data.track;
                    string trackUri = Marshal.PtrToStringAnsi(t.uri);
                    string trackName = Marshal.PtrToStringAnsi(t.name);
                    string artistName = Marshal.PtrToStringAnsi(t.artist);

                    Debug.WriteLine($"{logPrefix} Track: {trackName} by {artistName} ({trackUri})");

                    var track = new LibrespotTrackInfo
                    {
                        Uri = trackUri,
                        Name = trackName,
                        Artist = artistName,
                        Album = Marshal.PtrToStringAnsi(t.album),
                        CoverUrl = Marshal.PtrToStringAnsi(t.cover_url),
                        Duration = TimeSpan.FromMilliseconds(t.duration_ms)
                    };
                    UpdateTrack(track);
                    UpdatePosition(0);
                    break;

                case EventType.PlaybackPaused:
                    Debug.WriteLine($"{logPrefix} State -> Paused at {evt.data.position_ms}ms");
                    UpdatePlaybackState(LibrespotPlaybackState.Paused);
                    break;

                case EventType.PlaybackResumed:
                    Debug.WriteLine($"{logPrefix} State -> Playing from {evt.data.position_ms}ms");
                    UpdatePlaybackState(LibrespotPlaybackState.Playing);
                    break;

                case EventType.PlaybackLoading:
                    Debug.WriteLine($"{logPrefix} Buffering/Loading track...");
                    UpdatePlaybackState(LibrespotPlaybackState.Loading);
                    break;

                case EventType.PlaybackStopped:
                case EventType.PlaybackUnavailable:
                    Debug.WriteLine($"{logPrefix} Playback Stopped.");
                    UpdatePlaybackState(LibrespotPlaybackState.Stopped);
                    break;

                case EventType.EndOfTrack:
                    var endedTrackUri = Marshal.PtrToStringAnsi(evt.data.track_uri);
                    LogService.Info($"{logPrefix} Reached end of track URI: {endedTrackUri}");
                    OnEndOfTrack(endedTrackUri);
                    break;

                case EventType.VolumeChanged:
                    Debug.WriteLine($"{logPrefix} Volume: {evt.data.volume}");
                    UpdateVolume(evt.data.volume);
                    break;

                case EventType.ShuffleChanged:
                    Debug.WriteLine($"{logPrefix} Shuffle: {evt.data.shuffle}");
                    UpdateShuffle(evt.data.shuffle);
                    break;

                case EventType.RepeatChanged:
                    Debug.WriteLine($"{logPrefix} Repeat Mode: {evt.data.repeat_mode}");
                    UpdateRepeat(evt.data.repeat_mode);
                    break;

                case EventType.Seeked:
                case EventType.PositionCorrection:
                case EventType.PositionChanged:
                    if (evt.event_type != EventType.PositionChanged)
                        Debug.WriteLine($"{logPrefix} Syncing position to {evt.data.position_ms}ms");

                    UpdatePosition(evt.data.position_ms);
                    break;

                case EventType.SessionConnected:
                    string user = Marshal.PtrToStringAnsi(evt.data.session_user);
                    Debug.WriteLine($"{logPrefix} Connected as user: {user}");
                    OnSessionChanged(true, user);
                    break;

                case EventType.SessionDisconnected:
                    Debug.WriteLine($"{logPrefix} Session Disconnected");
                    OnSessionChanged(false, null);
                    break;

                case EventType.ClientChanged:
                    string client = Marshal.PtrToStringAnsi(evt.data.client_name);
                    Debug.WriteLine($"{logPrefix} Active Client switched to: {client}");
                    UpdateClientInfo(client);
                    break;

                case EventType.AutoPlayChanged:
                    Debug.WriteLine($"{logPrefix} AutoPlay: {evt.data.auto_play}");
                    UpdateAutoPlay(evt.data.auto_play);
                    break;

                case EventType.ExplicitFilterChanged:
                    Debug.WriteLine($"{logPrefix} Explicit Filter: {evt.data.filter_explicit}");
                    UpdateExplicitFilter(evt.data.filter_explicit);
                    break;

                case EventType.AddedToQueue:
                    Debug.WriteLine($"{logPrefix} Track added to queue: {Marshal.PtrToStringAnsi(evt.data.track_uri)}");
                    break;

                case EventType.Panic:
                    string panicMsg = Marshal.PtrToStringAnsi(evt.data.log_msg);
                    Debug.WriteLine($"{ts} [CRITICAL PANIC] {panicMsg}");
                    RaisePanic(panicMsg);
                    break;

                default:
                    Debug.WriteLine($"{logPrefix} No specific handler for this event.");
                    break;
            }
        }

        private void OnSessionChanged(bool connected, string username)
        {
            LibrespotSessionState snapshot;
            lock (_stateLock)
            {
                _session = new LibrespotSessionState
                {
                    IsConnected = connected,
                    UserName = username,
                    AuthNeeded = !connected
                };
                snapshot = _session;
            }
            SessionStateChanged?.Invoke(this, snapshot);
        }

        private void UpdatePlaybackState(LibrespotPlaybackState state)
        {
            lock (_stateLock)
            {
                _playbackState = state;
            }
            PlaybackStateChanged?.Invoke(this, state);
        }

        private void UpdateTrack(LibrespotTrackInfo track)
        {
            lock (_stateLock)
            {
                _currentTrack = track;
            }
            TrackChanged?.Invoke(this, track);
        }

        private void UpdateVolume(ushort volume)
        {
            lock (_stateLock)
            {
                _volume = volume;
            }
            VolumeChanged?.Invoke(this, volume);
        }

        private void OnEndOfTrack()
        {
            OnEndOfTrack(null);
        }

        private void OnEndOfTrack(string trackUri)
        {
            LogService.Info($"[LibreSpot] End of track reached. {trackUri}");
            EndOfTrack?.Invoke(this, trackUri);
        }

        private void UpdateClientInfo(string clientName)
        {
            ActiveClientName = clientName;
            Debug.WriteLine($"[LibreSpot] Active Client: {clientName}");
        }

        private void UpdateAutoPlay(bool enabled)
        {
            IsAutoPlayEnabled = enabled;
            Debug.WriteLine($"[LibreSpot] AutoPlay updated: {enabled}");
        }

        private void UpdateExplicitFilter(bool enabled)
        {
            IsExplicitFilterEnabled = enabled;
            Debug.WriteLine($"[LibreSpot] Explicit Filter updated: {enabled}");
        }

        private void UpdatePosition(uint positionMs)
        {
            Seeked?.Invoke(this, positionMs);
        }

        private void UpdateShuffle(bool enabled)
        {
            Debug.WriteLine($"[LibreSpot] Shuffle updated: {enabled}");
            lock (_stateLock) { _shuffle = enabled; }
            ShuffleChanged?.Invoke(this, enabled);
        }

        private void UpdateRepeat(uint mode)
        {
            Debug.WriteLine($"[LibreSpot] Repeat mode updated: {mode}");
            lock (_stateLock) { _repeatMode = mode; }
            RepeatChanged?.Invoke(this, mode);
        }

        private void RaisePanic(string message)
        {
            if (message == null) return;
            Panic?.Invoke(this, message);
        }

        private async Task RecreateInstanceWithAccessTokenAsync(string accessToken)
        {
            if (_instance != IntPtr.Zero)
            {
                Librespot.librespot_free(_instance);
                _instance = IntPtr.Zero;
            }

            var cfg = BuildConfig(accessToken);
            try
            {
                _instance = Librespot.librespot_new(cfg, _callbackDelegate, IntPtr.Zero);
                if (_instance == IntPtr.Zero)
                    throw new InvalidOperationException("librespot_new (with token) returned NULL.");
            }
            finally
            {
                FreeConfig(cfg);
            }

            await Task.CompletedTask;
        }

        private LibrespotConfig BuildConfig(string accessToken)
        {
            string deviceType;
            switch (AnalyticsInfo.VersionInfo.DeviceFamily)
            {
                case "Windows.Desktop":
                    deviceType = "computer";
                    break;
                case "Windows.Mobile":
                    deviceType = "smartphone";
                    break;
                case "Windows.Xbox":
                    deviceType = "gameconsole";
                    break;
                default:
                    deviceType = "speaker";
                    break;
            }

            string cacheDir = ApplicationData.Current.LocalCacheFolder.Path;
            string persistedCacheDir = ApplicationData.Current.LocalFolder.Path;

            return new LibrespotConfig
            {
                device_name = Marshal.StringToHGlobalAnsi(Environment.MachineName),
                device_type = Marshal.StringToHGlobalAnsi(deviceType),
                cache_dir = Marshal.StringToHGlobalAnsi(cacheDir),
                persisted_cache_dir = Marshal.StringToHGlobalAnsi(persistedCacheDir),
                enable_discovery = false,
                enable_volume_normalisation = false,
                bitrate = Bitrate.B320,
                format = _audioFormat.LibrespotFormat,
                username = IntPtr.Zero,
                password = IntPtr.Zero,
                auth_blob = IntPtr.Zero,
                access_token = Marshal.StringToHGlobalAnsi(accessToken),
                key_callback = _keyCallbackDelegate,
                key_save_callback = _keySaveDelegate,
                key_remove_callback = _keyRemoveDelegate,
            };
        }

        private static void FreeConfig(LibrespotConfig cfg)
        {
            FreeHGlobalIfNeeded(cfg.device_name);
            FreeHGlobalIfNeeded(cfg.device_type);
            FreeHGlobalIfNeeded(cfg.cache_dir);
            FreeHGlobalIfNeeded(cfg.persisted_cache_dir);
            FreeHGlobalIfNeeded(cfg.username);
            FreeHGlobalIfNeeded(cfg.password);
            FreeHGlobalIfNeeded(cfg.auth_blob);
            FreeHGlobalIfNeeded(cfg.access_token);
        }

        private static void FreeHGlobalIfNeeded(IntPtr ptr)
        {
            if (ptr != IntPtr.Zero)
                Marshal.FreeHGlobal(ptr);
        }
    }
}
