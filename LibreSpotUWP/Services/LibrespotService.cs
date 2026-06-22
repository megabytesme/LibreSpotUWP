using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Interop;
using LibreSpotUWP.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.System.Profile;
using Windows.UI.Core;
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
        private readonly SemaphoreSlim _connectGate = new SemaphoreSlim(1, 1);

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
        private string _activeAccessToken;

        private string ts = DateTime.Now.ToString("HH:mm:ss");

        public AudioEncodingProperties EncodingProperties => _audioFormat?.EncodingProperties;
        public bool HasInstance => _instance != IntPtr.Zero;
        public string DeviceName => Environment.MachineName;
        public string DeviceId => ComputeDeviceId(DeviceName);
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
        public event EventHandler<uint> PositionChanged;
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

            await _connectGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_instance != IntPtr.Zero && string.Equals(_activeAccessToken, accessToken, StringComparison.Ordinal))
                {
                    LogService.Info("[LibrespotService.ConnectWithAccessTokenAsync] Existing librespot instance already uses this access token.");
                    return;
                }

                LogService.Info("[LibrespotService.ConnectWithAccessTokenAsync] Connecting with access token.");
                try
                {
                    await RecreateInstanceWithAccessTokenAsync(accessToken).ConfigureAwait(false);
                    _activeAccessToken = accessToken;
                }
                catch
                {
                    _activeAccessToken = null;
                    throw;
                }
            }
            finally
            {
                _connectGate.Release();
            }
        }

        public async Task ReconnectWithAccessTokenAsync(string accessToken)
        {
            ThrowIfDisposed();
            if (!_initialized)
                throw new InvalidOperationException("LibrespotService not initialized.");

            if (string.IsNullOrWhiteSpace(accessToken))
                throw new ArgumentException("Access token must not be null or empty.", nameof(accessToken));

            await _connectGate.WaitAsync().ConfigureAwait(false);
            try
            {
                LogService.Info("[LibrespotService.ReconnectWithAccessTokenAsync] Recreating librespot instance.");
                try
                {
                    await RecreateInstanceWithAccessTokenAsync(accessToken).ConfigureAwait(false);
                    _activeAccessToken = accessToken;
                }
                catch
                {
                    _activeAccessToken = null;
                    throw;
                }
            }
            finally
            {
                _connectGate.Release();
            }
        }

        public async Task DisconnectAsync()
        {
            ThrowIfDisposed();

            await _connectGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_instance != IntPtr.Zero)
                {
                    Librespot.librespot_free(_instance);
                    _instance = IntPtr.Zero;
                }

                _activeAccessToken = null;
            }
            finally
            {
                _connectGate.Release();
            }

            lock (_stateLock)
            {
                _session = new LibrespotSessionState
                {
                    IsConnected = false,
                    UserName = null,
                    AuthNeeded = false
                };
                _playbackState = LibrespotPlaybackState.Stopped;
                _currentTrack = null;
            }

            RaiseOnMainThread(() => SessionStateChanged?.Invoke(this, _session), nameof(SessionStateChanged));
            RaiseOnMainThread(() => PlaybackStateChanged?.Invoke(this, _playbackState), nameof(PlaybackStateChanged));
            RaiseOnMainThread(() => TrackChanged?.Invoke(this, null), nameof(TrackChanged));
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

        public Task<LibrespotLyricsData> GetLyricsAsync(string trackUri, string imageIdHex = null)
        {
            var kind = string.IsNullOrWhiteSpace(imageIdHex)
                ? LibrespotAppDataKind.Lyrics
                : LibrespotAppDataKind.LyricsForImage;

            var argument = string.IsNullOrWhiteSpace(imageIdHex)
                ? trackUri
                : JsonConvert.SerializeObject(new
                {
                    trackUri,
                    imageIdHex = imageIdHex
                });

            return GetAppDataPayloadAsync(argument, kind, payload =>
            {
                var wrapper = JsonConvert.DeserializeObject<LibrespotAppDataResponse<LibrespotLyricsData>>(payload);
                return wrapper?.Data;
            });
        }

        public Task<string> GetLyricsJsonAsync(string trackUri, string imageIdHex = null)
        {
            var kind = string.IsNullOrWhiteSpace(imageIdHex)
                ? LibrespotAppDataKind.Lyrics
                : LibrespotAppDataKind.LyricsForImage;

            var argument = string.IsNullOrWhiteSpace(imageIdHex)
                ? trackUri
                : JsonConvert.SerializeObject(new
                {
                    trackUri,
                    imageIdHex = imageIdHex
                });

            return GetAppDataPayloadAsync(argument, kind, payload => payload);
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
                return ReadString(errorPtr);
            }
            finally
            {
                Librespot.librespot_string_free(errorPtr);
            }
        }

        private Task<T> GetAppDataPayloadAsync<T>(
            string argument,
            LibrespotAppDataKind kind,
            Func<string, T> mapper)
        {
            ThrowIfDisposed();
            if (!_initialized)
                throw new InvalidOperationException("LibrespotService not initialized.");
            if (_instance == IntPtr.Zero)
                throw new InvalidOperationException("Not connected.");

            return Task.Run(() =>
            {
                IntPtr argumentPtr = IntPtr.Zero;
                IntPtr payloadPtr = IntPtr.Zero;

                try
                {
                    argumentPtr = AllocUtf8String(argument ?? string.Empty);
                    payloadPtr = Librespot.librespot_appdata_get(_instance, (int)kind, argumentPtr);

                    if (payloadPtr == IntPtr.Zero)
                    {
                        var lastError = GetLastNativeError();
                        if (IsLyricsKind(kind) && IsMissingAppData(lastError))
                        {
                            LogService.Warn($"[LibrespotService.GetAppDataPayloadAsync] Lyrics unavailable for {argument}: {lastError}");
                            return default(T);
                        }

                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(lastError)
                                ? $"librespot app data request returned null for {argument}."
                                : $"librespot app data request returned null for {argument}. Native error: {lastError}");
                    }

                    var json = ReadString(payloadPtr);
                    if (string.IsNullOrWhiteSpace(json))
                        throw new InvalidOperationException("App data payload was empty.");

                    if (kind == LibrespotAppDataKind.Lyrics || kind == LibrespotAppDataKind.LyricsForImage)
                        LogService.Info($"[LibrespotService.GetAppDataPayloadAsync] Lyrics payload received for kind={(int)kind}, argument={argument}, length={json.Length}, prefix={json.Substring(0, Math.Min(240, json.Length))}.");

                    return mapper(json);
                }
                finally
                {
                    if (payloadPtr != IntPtr.Zero)
                        Librespot.librespot_string_free(payloadPtr);

                    if (argumentPtr != IntPtr.Zero)
                        Marshal.FreeHGlobal(argumentPtr);
                }
            });
        }

        private static bool IsLyricsKind(LibrespotAppDataKind kind)
        {
            return kind == LibrespotAppDataKind.Lyrics || kind == LibrespotAppDataKind.LyricsForImage;
        }

        private static bool IsMissingAppData(string lastError)
        {
            return !string.IsNullOrWhiteSpace(lastError) &&
                (lastError.IndexOf("404", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 lastError.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 lastError.IndexOf("Requested entity was not found", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private sealed class LibrespotAppDataResponse<T>
        {
            public string Kind { get; set; }
            public T Data { get; set; }
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
                IntPtr argumentPtr = AllocUtf8String(argument);
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

        private static IntPtr AllocUtf8String(string value)
        {
            if (value == null)
                return IntPtr.Zero;

            var bytes = Encoding.UTF8.GetBytes(value);
            var ptr = Marshal.AllocHGlobal(bytes.Length + 1);

            if (bytes.Length > 0)
                Marshal.Copy(bytes, 0, ptr, bytes.Length);

            Marshal.WriteByte(ptr, bytes.Length, 0);
            return ptr;
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

            IntPtr contextPtr = AllocUtf8String(contextUri);
            IntPtr startPtr = startUri != null ? AllocUtf8String(startUri) : IntPtr.Zero;

            try
            {
                Librespot.librespot_load(_instance, contextPtr, startPtr, true);
                LogService.Info("[LibrespotService.LoadAndPlayAsync] librespot_load returned.");
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
                Librespot.librespot_stop(_instance);
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

            IntPtr fileIdPtr = AllocUtf8String(fileIdHex);

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

            IntPtr trackUriPtr = AllocUtf8String(trackUri);

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
            _activeAccessToken = null;

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
                    string msg = ReadString(evt.data.log_msg);
                    LogService.Info($"{ts} [LibreSpot Internal] {msg}");
                    RaiseOnMainThread(() => LogMessage?.Invoke(this, msg), nameof(LogMessage));
                    break;

                case EventType.TrackChanged:
                    var t = evt.data.track;
                    string trackUri = ReadString(t.uri);
                    string trackName = ReadString(t.name);
                    string artistName = ReadString(t.artist);

                    LogService.Info($"{logPrefix} Track: {trackName} by {artistName} ({trackUri})");

                    var track = new LibrespotTrackInfo
                    {
                        Uri = trackUri,
                        Name = trackName,
                        Artist = artistName,
                        Album = ReadString(t.album),
                        CoverUrl = ReadString(t.cover_url),
                        Duration = TimeSpan.FromMilliseconds(t.duration_ms)
                    };
                    UpdateTrack(track);
                    UpdatePosition(0);
                    break;

                case EventType.PlaybackPaused:
                    LogService.Info($"{logPrefix} State -> Paused at {evt.data.position_ms}ms");
                    UpdatePlaybackState(LibrespotPlaybackState.Paused);
                    break;

                case EventType.PlaybackResumed:
                    LogService.Info($"{logPrefix} State -> Playing from {evt.data.position_ms}ms");
                    UpdatePlaybackState(LibrespotPlaybackState.Playing);
                    break;

                case EventType.PlaybackLoading:
                    LogService.Info($"{logPrefix} Buffering/Loading track...");
                    UpdatePlaybackState(LibrespotPlaybackState.Loading);
                    break;

                case EventType.PlaybackStopped:
                case EventType.PlaybackUnavailable:
                    LogService.Info($"{logPrefix} Playback Stopped.");
                    UpdatePlaybackState(LibrespotPlaybackState.Stopped);
                    break;

                case EventType.EndOfTrack:
                    var endedTrackUri = ReadString(evt.data.track_uri);
                    LogService.Info($"{logPrefix} Reached end of track URI: {endedTrackUri}");
                    OnEndOfTrack(endedTrackUri);
                    break;

                case EventType.VolumeChanged:
                    LogService.Info($"{logPrefix} Volume: {evt.data.volume}");
                    UpdateVolume(evt.data.volume);
                    break;

                case EventType.ShuffleChanged:
                    LogService.Info($"{logPrefix} Shuffle: {evt.data.shuffle}");
                    UpdateShuffle(evt.data.shuffle);
                    break;

                case EventType.RepeatChanged:
                    LogService.Info($"{logPrefix} Repeat Mode: {evt.data.repeat_mode}");
                    UpdateRepeat(evt.data.repeat_mode);
                    break;

                case EventType.Seeked:
                case EventType.PositionCorrection:
                case EventType.PositionChanged:
                    if (evt.event_type != EventType.PositionChanged)
                        LogService.Info($"{logPrefix} Syncing position to {evt.data.position_ms}ms");

                    UpdatePosition(evt.data.position_ms);
                    break;

                case EventType.SessionConnected:
                    string user = ReadString(evt.data.session_user);
                    LogService.Info($"{logPrefix} Connected as user: {user}");
                    OnSessionChanged(true, user);
                    break;

                case EventType.SessionDisconnected:
                    LogService.Info($"{logPrefix} Session Disconnected");
                    OnSessionChanged(false, null);
                    break;

                case EventType.ClientChanged:
                    string client = ReadString(evt.data.client_name);
                    LogService.Info($"{logPrefix} Active Client switched to: {client}");
                    UpdateClientInfo(client);
                    break;

                case EventType.AutoPlayChanged:
                    LogService.Info($"{logPrefix} AutoPlay: {evt.data.auto_play}");
                    UpdateAutoPlay(evt.data.auto_play);
                    break;

                case EventType.ExplicitFilterChanged:
                    LogService.Info($"{logPrefix} Explicit Filter: {evt.data.filter_explicit}");
                    UpdateExplicitFilter(evt.data.filter_explicit);
                    break;

                case EventType.AddedToQueue:
                    LogService.Info($"{logPrefix} Track added to queue: {ReadString(evt.data.track_uri)}");
                    break;

                case EventType.Panic:
                    string panicMsg = ReadString(evt.data.log_msg);
                    LogService.Error($"{ts} [CRITICAL PANIC] {panicMsg}");
                    RaisePanic(panicMsg);
                    break;

                default:
                    LogService.Info($"{logPrefix} No specific handler for this event.");
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
            RaiseOnMainThread(() => SessionStateChanged?.Invoke(this, snapshot), nameof(SessionStateChanged));
        }

        private void UpdatePlaybackState(LibrespotPlaybackState state)
        {
            lock (_stateLock)
            {
                _playbackState = state;
            }
            RaiseOnMainThread(() => PlaybackStateChanged?.Invoke(this, state), nameof(PlaybackStateChanged));
        }

        private void UpdateTrack(LibrespotTrackInfo track)
        {
            lock (_stateLock)
            {
                _currentTrack = track;
            }
            RaiseOnMainThread(() => TrackChanged?.Invoke(this, track), nameof(TrackChanged));
        }

        private void UpdateVolume(ushort volume)
        {
            lock (_stateLock)
            {
                _volume = volume;
            }
            RaiseOnMainThread(() => VolumeChanged?.Invoke(this, volume), nameof(VolumeChanged));
        }

        private void OnEndOfTrack()
        {
            OnEndOfTrack(null);
        }

        private void OnEndOfTrack(string trackUri)
        {
            LogService.Info($"[LibreSpot] End of track reached. {trackUri}");
            RaiseOnMainThread(() => EndOfTrack?.Invoke(this, trackUri), nameof(EndOfTrack));
        }

        private void UpdateClientInfo(string clientName)
        {
            ActiveClientName = clientName;
            LogService.Info($"[LibreSpot] Active Client: {clientName}");
        }

        private void UpdateAutoPlay(bool enabled)
        {
            IsAutoPlayEnabled = enabled;
            LogService.Info($"[LibreSpot] AutoPlay updated: {enabled}");
        }

        private void UpdateExplicitFilter(bool enabled)
        {
            IsExplicitFilterEnabled = enabled;
            LogService.Info($"[LibreSpot] Explicit Filter updated: {enabled}");
        }

        private void UpdatePosition(uint positionMs)
        {
            RaiseOnMainThread(() => PositionChanged?.Invoke(this, positionMs), nameof(PositionChanged));
            RaiseOnMainThread(() => Seeked?.Invoke(this, positionMs), nameof(Seeked));
        }

        private void UpdateShuffle(bool enabled)
        {
            LogService.Info($"[LibreSpot] Shuffle updated: {enabled}");
            lock (_stateLock) { _shuffle = enabled; }
            RaiseOnMainThread(() => ShuffleChanged?.Invoke(this, enabled), nameof(ShuffleChanged));
        }

        private void UpdateRepeat(uint mode)
        {
            LogService.Info($"[LibreSpot] Repeat mode updated: {mode}");
            lock (_stateLock) { _repeatMode = mode; }
            RaiseOnMainThread(() => RepeatChanged?.Invoke(this, mode), nameof(RepeatChanged));
        }

        private void RaisePanic(string message)
        {
            if (message == null) return;
            RaiseOnMainThread(() => Panic?.Invoke(this, message), nameof(Panic));
        }

        private static void RaiseOnMainThread(Action action, string eventName)
        {
            try
            {
                var dispatcher = CoreApplication.MainView?.CoreWindow?.Dispatcher;
                if (dispatcher != null && !dispatcher.HasThreadAccess)
                {
                    var ignored = dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        try
                        {
                            action();
                        }
                        catch (Exception ex)
                        {
                            LogService.Error(ex, $"Librespot event handler failed for {eventName}");
                        }
                    });
                    return;
                }

                action();
            }
            catch (Exception ex)
            {
                LogService.Error(ex, $"Librespot event dispatch failed for {eventName}");
            }
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
            ushort initialVolume = GetInitialVolumeFromSettings();

            return new LibrespotConfig
            {
                device_name = AllocUtf8String(Environment.MachineName),
                device_type = AllocUtf8String(deviceType),
                cache_dir = AllocUtf8String(cacheDir),
                persisted_cache_dir = AllocUtf8String(persistedCacheDir),
                enable_discovery = false,
                enable_volume_normalisation = false,
                bitrate = Bitrate.B320,
                format = _audioFormat.LibrespotFormat,
                initial_volume = initialVolume,
                username = IntPtr.Zero,
                password = IntPtr.Zero,
                auth_blob = IntPtr.Zero,
                access_token = AllocUtf8String(accessToken),
                key_callback = _keyCallbackDelegate,
                key_save_callback = _keySaveDelegate,
                key_remove_callback = _keyRemoveDelegate,
            };
        }

        private static ushort GetInitialVolumeFromSettings()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("UserVolume", out object saved) && saved is ushort rawSaved && rawSaved > 0)
                return rawSaved;

            return 65535;
        }

        private static string ComputeDeviceId(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
                deviceName = "LibreSpotUWP";

            using (var sha1 = SHA1.Create())
            {
                var hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(deviceName));
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
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
