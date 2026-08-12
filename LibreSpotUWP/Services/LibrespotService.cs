using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Interop;
using LibreSpotUWP.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        private const int NativeQueueWindowSize = 50;
        private const int NativeQueueLookbehind = 5;
        private static readonly TimeSpan NativeSessionReadyTimeout = TimeSpan.FromSeconds(10);
        private readonly object _stateLock = new object();

        private IntPtr _dllHandle = IntPtr.Zero;
        private IntPtr _instance = IntPtr.Zero;
        private LibrespotCallback _callbackDelegate;
        private readonly List<LibrespotCallback> _sessionCallbacks = new List<LibrespotCallback>();
        private readonly Librespot.LibrespotKeyCallback _keyCallbackDelegate;
        private readonly Librespot.LibrespotKeySaveCallback _keySaveDelegate;
        private readonly Librespot.LibrespotKeyRemoveCallback _keyRemoveDelegate;
        private readonly SemaphoreSlim _connectGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _appDataGate = new SemaphoreSlim(2, 2);

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
        private string _activePlaybackAuthorization;
        private long _sessionGeneration;

        private string ts = DateTime.Now.ToString("HH:mm:ss");

        public AudioEncodingProperties EncodingProperties => _audioFormat?.EncodingProperties;
        public bool HasInstance => _instance != IntPtr.Zero;
        public string DeviceName => Environment.MachineName;
        public string DeviceId => ComputeDeviceId(DeviceName);
        public LibrespotSessionState Session => _session;
        public long SessionGeneration => Interlocked.Read(ref _sessionGeneration);
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
        public event EventHandler<LibrespotPlaybackEvent> PlaybackEvent;
        public event EventHandler<LibrespotPositionUpdate> PositionChanged;
        public event EventHandler<ushort> VolumeChanged;
        public event EventHandler<LibrespotTrackBoundaryInfo> EndOfTrack;
        public event EventHandler<LibrespotTrackBoundaryInfo> TimeToPreloadNextTrack;
        public event EventHandler<LibrespotTrackBoundaryInfo> TrackPreloading;
        public event EventHandler<string> LogMessage;
        public event EventHandler<string> Panic;
        public event EventHandler<bool> ShuffleChanged;
        public event EventHandler<uint> RepeatChanged;
        public event EventHandler<PlaybackCredentialsEventArgs> PlaybackCredentialsAvailable;
        public event EventHandler PlaybackAuthorizationRejected;

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

            await SelectStartupAudioBackendAsync().ConfigureAwait(false);
            NativeWindowsAudioPlayer.ApplyEffects();

            await _audioKeyCache.InitializeAsync().ConfigureAwait(false);

            _audioFormat = await AudioFormatProbe.ProbeAsync().ConfigureAwait(false);

            _initialized = true;
        }

        private static async Task SelectStartupAudioBackendAsync()
        {
            var requestedBackend = UserSettings.AudioBackend;
            var outputDeviceId = UserSettings.AudioOutputDeviceId;
            Exception lastError;

            try
            {
                await NativeWindowsAudioPlayer.SelectBackendAsync(requestedBackend, outputDeviceId)
                    .ConfigureAwait(false);
                LogService.Info(
                    $"[LibrespotService.SelectStartupAudioBackendAsync] Initialized requested backend {requestedBackend}.");
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                LogService.Warn(
                    $"[LibrespotService.SelectStartupAudioBackendAsync] Requested backend {requestedBackend} is unavailable: {ex.Message}");
            }

            var fallbacks = requestedBackend == AudioBackendKind.RustXAudio2
                ? new[] { AudioBackendKind.RustWasapi, AudioBackendKind.RingBuffer }
                : requestedBackend == AudioBackendKind.RustWasapi
                    ? new[] { AudioBackendKind.RingBuffer }
                    : new[] { AudioBackendKind.RustWasapi };

            foreach (var fallback in fallbacks)
            {
                try
                {
                    await NativeWindowsAudioPlayer.SelectBackendAsync(fallback, outputDeviceId)
                        .ConfigureAwait(false);
                    UserSettings.AudioBackend = fallback;
                    LogService.Warn(
                        $"[LibrespotService.SelectStartupAudioBackendAsync] Falling back from {requestedBackend} to {fallback}; the fallback has been saved.");
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    LogService.Warn(
                        $"[LibrespotService.SelectStartupAudioBackendAsync] Fallback backend {fallback} is unavailable: {ex.Message}");
                }
            }

            throw new InvalidOperationException(
                "None of the configured Windows audio backends could be initialized.",
                lastError);
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

        public async Task ConnectWithPlaybackAuthAsync(PlaybackConnectionMaterial authorization)
        {
            ThrowIfDisposed();
            if (!_initialized)
                throw new InvalidOperationException("LibrespotService not initialized.");

            if (authorization == null || authorization.IsEmpty)
                throw new ArgumentException("Playback authorization must not be empty.", nameof(authorization));

            await _connectGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_instance != IntPtr.Zero && string.Equals(_activePlaybackAuthorization, authorization.Identity, StringComparison.Ordinal))
                {
                    LogService.Info("[LibrespotService.ConnectWithPlaybackAuthAsync] Existing librespot instance already uses this playback authorization.");
                    return;
                }

                LogService.Info($"[LibrespotService.ConnectWithPlaybackAuthAsync] Connecting with playback authorization. activeSessionGeneration={SessionGeneration}.");
                try
                {
                    await RecreateInstanceWithPlaybackAuthAsync(authorization).ConfigureAwait(false);
                    _activePlaybackAuthorization = authorization.Identity;
                }
                catch
                {
                    _activePlaybackAuthorization = null;
                    throw;
                }
            }
            finally
            {
                _connectGate.Release();
            }
        }

        public async Task ReconnectWithPlaybackAuthAsync(PlaybackConnectionMaterial authorization)
        {
            ThrowIfDisposed();
            if (!_initialized)
                throw new InvalidOperationException("LibrespotService not initialized.");

            if (authorization == null || authorization.IsEmpty)
                throw new ArgumentException("Playback authorization must not be empty.", nameof(authorization));

            await _connectGate.WaitAsync().ConfigureAwait(false);
            try
            {
                LogService.Info($"[LibrespotService.ReconnectWithPlaybackAuthAsync] Recreating librespot instance. activeSessionGeneration={SessionGeneration}.");
                try
                {
                    await RecreateInstanceWithPlaybackAuthAsync(authorization).ConfigureAwait(false);
                    _activePlaybackAuthorization = authorization.Identity;
                }
                catch
                {
                    _activePlaybackAuthorization = null;
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

            long disconnectedGeneration;
            LibrespotSessionState disconnectedSession;
            await _connectGate.WaitAsync().ConfigureAwait(false);
            try
            {
                disconnectedGeneration = Interlocked.Increment(ref _sessionGeneration);
                if (_instance != IntPtr.Zero)
                {
                    var instance = _instance;
                    _instance = IntPtr.Zero;
                    await FreeNativeInstanceAsync(instance, "disconnect").ConfigureAwait(false);
                }

                _activePlaybackAuthorization = null;
                lock (_stateLock)
                {
                    _session = new LibrespotSessionState
                    {
                        IsConnected = false,
                        SessionGeneration = disconnectedGeneration,
                        UserName = null,
                        AuthNeeded = false
                    };
                    _playbackState = LibrespotPlaybackState.Stopped;
                    _currentTrack = null;
                    ActiveClientName = null;
                    IsAutoPlayEnabled = false;
                    IsExplicitFilterEnabled = false;
                    disconnectedSession = _session;
                }
                LogService.Info($"[LibrespotService.DisconnectAsync] Native session disposed. sessionGeneration={disconnectedGeneration}.");
            }
            finally
            {
                _connectGate.Release();
            }

            RaiseOnMainThread(() => SessionStateChanged?.Invoke(this, disconnectedSession), nameof(SessionStateChanged), disconnectedGeneration);
            RaiseOnMainThread(() => PlaybackStateChanged?.Invoke(this, LibrespotPlaybackState.Stopped), nameof(PlaybackStateChanged), disconnectedGeneration);
            PublishPlaybackEvent(new LibrespotPlaybackEvent { State = LibrespotPlaybackState.Stopped, SessionGeneration = disconnectedGeneration }, disconnectedGeneration);
            RaiseOnMainThread(() => TrackChanged?.Invoke(this, null), nameof(TrackChanged), disconnectedGeneration);
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

        private async Task<T> GetAppDataPayloadAsync<T>(
            string argument,
            LibrespotAppDataKind kind,
            Func<string, T> mapper)
        {
            ThrowIfDisposed();
            if (!_initialized)
                throw new InvalidOperationException("LibrespotService not initialized.");
            await _appDataGate.WaitAsync().ConfigureAwait(false);
            using (UiResponsivenessTelemetry.BeginOperation("Librespot.AppData." + kind))
            {
                try
                {
                    var nativeInstance = await WaitForConnectedNativeInstanceAsync().ConfigureAwait(false);
                    var stopwatch = Stopwatch.StartNew();
                    var result = await Task.Run(() =>
                    {
                        IntPtr argumentPtr = IntPtr.Zero;
                        IntPtr payloadPtr = IntPtr.Zero;

                        try
                        {
                            UiResponsivenessTelemetry.VerifyBackgroundThread("librespot app-data callback");
                            argumentPtr = AllocUtf8String(argument ?? string.Empty);
                            payloadPtr = Librespot.librespot_appdata_get(nativeInstance, (int)kind, argumentPtr);

                            if (payloadPtr == IntPtr.Zero)
                            {
                                var lastError = GetLastNativeError();
                                if (IsLyricsKind(kind) && IsMissingAppData(lastError))
                                {
                                    LogService.Telemetry(
                                        "lyrics-unavailable",
                                        $"Lyrics app-data unavailable: {lastError}.");
                                    return default(T);
                                }

                                throw new InvalidOperationException(
                                    string.IsNullOrWhiteSpace(lastError)
                                        ? $"librespot app data request returned null for kind {(int)kind}."
                                        : $"librespot app data request returned null for kind {(int)kind}. Native error: {lastError}");
                            }

                            var json = ReadString(payloadPtr);
                            if (string.IsNullOrWhiteSpace(json))
                                throw new InvalidOperationException("App data payload was empty.");

                            return mapper(json);
                        }
                        finally
                        {
                            if (payloadPtr != IntPtr.Zero)
                                Librespot.librespot_string_free(payloadPtr);

                            if (argumentPtr != IntPtr.Zero)
                                Marshal.FreeHGlobal(argumentPtr);
                        }
                    }).ConfigureAwait(false);

                    stopwatch.Stop();
                    if (stopwatch.Elapsed >= TimeSpan.FromSeconds(2))
                    {
                        LogService.Telemetry(
                            "slow-librespot-appdata:" + kind,
                            $"Slow librespot app-data request kind={(int)kind}, elapsedMs={stopwatch.ElapsedMilliseconds}.",
                            warning: stopwatch.Elapsed >= TimeSpan.FromSeconds(10));
                    }

                    return result;
                }
                finally
                {
                    _appDataGate.Release();
                }
            }
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

        private async Task<T> GetTypedPayloadAsync<T>(
            string argument,
            Func<IntPtr, IntPtr, IntPtr> getter,
            Action<IntPtr> freer,
            Func<IntPtr, T> mapper)
        {
            ThrowIfDisposed();
            if (!_initialized)
                throw new InvalidOperationException("LibrespotService not initialized.");
            if (string.IsNullOrWhiteSpace(argument))
                throw new ArgumentException("Argument must not be null or empty.", nameof(argument));

            var nativeInstance = await WaitForConnectedNativeInstanceAsync().ConfigureAwait(false);
            return await Task.Run(() =>
            {
                IntPtr argumentPtr = AllocUtf8String(argument);
                try
                {
                    var resultPtr = getter(nativeInstance, argumentPtr);
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

        public async Task LoadAndPlayAsync(
            string contextUri,
            string startUri = null,
            IReadOnlyList<string> orderedTrackUris = null,
            bool startPlaying = true)
        {
            ThrowIfDisposed();
            if (!_initialized) throw new InvalidOperationException("Not initialized.");
            if (_instance == IntPtr.Zero) throw new InvalidOperationException("Not connected.");

            LogService.Info($"[LibrespotService.LoadAndPlayAsync] context={contextUri}, start={startUri ?? "(null)"}.");

            IntPtr contextPtr = AllocUtf8String(contextUri);
            IntPtr startPtr = startUri != null ? AllocUtf8String(startUri) : IntPtr.Zero;
            IntPtr tracksPtr = IntPtr.Zero;

            try
            {
                var allTracks = orderedTrackUris?
                    .Where(uri => !string.IsNullOrWhiteSpace(uri))
                    .ToArray();
                var tracks = CreateNativeQueueWindow(allTracks, startUri);
                if (tracks != null && tracks.Length > 0)
                {
                    tracksPtr = AllocUtf8String(JsonConvert.SerializeObject(tracks));
                    Librespot.librespot_load_tracks(_instance, contextPtr, tracksPtr, startPtr, startPlaying);
                    LogService.Info($"[LibrespotService.LoadAndPlayAsync] librespot_load_tracks returned with {tracks.Length} of {allTracks.Length} tracks.");
                }
                else
                {
                    Librespot.librespot_load(_instance, contextPtr, startPtr, startPlaying);
                    LogService.Info("[LibrespotService.LoadAndPlayAsync] librespot_load returned.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(contextPtr);
                if (tracksPtr != IntPtr.Zero) Marshal.FreeHGlobal(tracksPtr);
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

            long disposedGeneration = Interlocked.Increment(ref _sessionGeneration);
            _ = Task.Run(async () =>
            {
                await _connectGate.WaitAsync().ConfigureAwait(false);
                await _appDataGate.WaitAsync().ConfigureAwait(false);
                await _appDataGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_instance != IntPtr.Zero)
                    {
                        var instance = _instance;
                        _instance = IntPtr.Zero;
                        await FreeNativeInstanceAsync(instance, "dispose").ConfigureAwait(false);
                    }
                    _activePlaybackAuthorization = null;
                }
                finally
                {
                    _appDataGate.Release(2);
                    _connectGate.Release();
                }
                LogService.Info($"[LibrespotService.Dispose] Native service disposed. sessionGeneration={disposedGeneration}.");

                if (_dllHandle != IntPtr.Zero)
                {
                    NativeProbe.TryFree(_dllHandle);
                    _dllHandle = IntPtr.Zero;
                }
            }).ConfigureAwait(false);
        }

        private async Task<IntPtr> WaitForConnectedNativeInstanceAsync()
        {
            await SessionReadinessWaiter.WaitAsync(
                () =>
                {
                    lock (_stateLock)
                    {
                        return _instance != IntPtr.Zero &&
                            _session != null &&
                            _session.IsConnected;
                    }
                },
                () =>
                {
                    lock (_stateLock)
                        return _initialized && !_disposed;
                },
                NativeSessionReadyTimeout,
                CancellationToken.None).ConfigureAwait(false);

            lock (_stateLock)
            {
                if (_instance == IntPtr.Zero || _session == null || !_session.IsConnected)
                    throw new InvalidOperationException("The native Spotify session disconnected before the request began.");

                return _instance;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LibrespotService));
        }

        private void OnLibrespotEvent(IntPtr evtPtr, IntPtr userData, long sessionGeneration)
        {
            if (sessionGeneration != SessionGeneration || _disposed)
            {
                LogService.Telemetry(
                    "stale-librespot-native-event",
                    $"Ignoring stale native events. eventSessionGeneration={sessionGeneration}, activeSessionGeneration={SessionGeneration}.");
                return;
            }

            var evt = Marshal.PtrToStructure<LibrespotEvent>(evtPtr);
            HandleEvent(evt, sessionGeneration);
        }

        private void HandleEvent(LibrespotEvent evt, long sessionGeneration)
        {
            string logPrefix = evt.event_type == EventType.PositionCorrection ||
                evt.event_type == EventType.PositionChanged
                    ? null
                    : $"{ts} [LibreSpot Event:{evt.event_type}] sessionGeneration={sessionGeneration}";

            switch (evt.event_type)
            {
                case EventType.LogMessage:
                    string msg = ReadString(evt.data.log_msg);
                    LogService.Info($"{ts} [LibreSpot Internal] sessionGeneration={sessionGeneration} {msg}");
                    RaiseOnMainThread(() => LogMessage?.Invoke(this, msg), nameof(LogMessage), sessionGeneration);
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
                        Duration = TimeSpan.FromMilliseconds(t.duration_ms),
                        PlayRequestId = evt.data.play_request_id,
                        AudioGeneration = evt.data.audio_generation,
                        SessionGeneration = sessionGeneration,
                        WasPreloaded = evt.data.was_preloaded
                    };
                    UpdateTrack(track, sessionGeneration);
                    PublishPositionUpdate(0, LibrespotPositionUpdateOrigin.Progress, sessionGeneration);
                    break;

                case EventType.PlaybackPaused:
                    LogService.Info($"{logPrefix} State -> Paused at {evt.data.position_ms}ms");
                    UpdatePlaybackState(LibrespotPlaybackState.Paused, evt.data, sessionGeneration);
                    break;

                case EventType.PlaybackResumed:
                    LogService.Info($"{logPrefix} State -> Playing from {evt.data.position_ms}ms");
                    UpdatePlaybackState(LibrespotPlaybackState.Playing, evt.data, sessionGeneration);
                    break;

                case EventType.PlaybackLoading:
                    LogService.Info($"{logPrefix} Buffering/Loading track...");
                    UpdatePlaybackState(LibrespotPlaybackState.Loading, evt.data, sessionGeneration);
                    break;

                case EventType.PlaybackStopped:
                case EventType.PlaybackUnavailable:
                    LogService.Info($"{logPrefix} Playback Stopped.");
                    UpdatePlaybackState(
                        LibrespotPlaybackState.Stopped,
                        evt.data,
                        sessionGeneration,
                        evt.event_type == EventType.PlaybackUnavailable);
                    break;

                case EventType.PlaybackKeyUnavailable:
                    LogService.Error(
                        new InvalidOperationException("Spotify rejected the required audio key."),
                        $"{logPrefix} Playback cannot start because Spotify rejected this account's audio-key request. Known upstream issue: https://github.com/librespot-org/librespot/issues/1649");
                    UpdatePlaybackState(
                        LibrespotPlaybackState.Stopped,
                        evt.data,
                        sessionGeneration,
                        isUnavailable: false,
                        isAudioKeyUnavailable: true);
                    break;

                case EventType.EndOfTrack:
                    var endedTrackUri = ReadString(evt.data.track_uri);
                    LogService.Info($"{logPrefix} Reached end of track URI: {endedTrackUri}");
                    OnEndOfTrack(endedTrackUri, evt.data.play_request_id, sessionGeneration);
                    break;

                case EventType.TimeToPreloadNextTrack:
                    var preloadSource = CreateTrackBoundaryInfo(evt.data, sessionGeneration);
                    LogService.Info($"{logPrefix} Preload requested near end of {preloadSource.TrackUri}.");
                    RaiseOnMainThread(
                        () => TimeToPreloadNextTrack?.Invoke(this, preloadSource),
                        nameof(TimeToPreloadNextTrack),
                        sessionGeneration);
                    break;

                case EventType.Preloading:
                    var preloadingTrack = CreateTrackBoundaryInfo(evt.data, sessionGeneration);
                    LogService.Info($"{logPrefix} Preloading {preloadingTrack.TrackUri}.");
                    RaiseOnMainThread(
                        () => TrackPreloading?.Invoke(this, preloadingTrack),
                        nameof(TrackPreloading),
                        sessionGeneration);
                    break;

                case EventType.VolumeChanged:
                    LogService.Info($"{logPrefix} Volume: {evt.data.volume}");
                    UpdateVolume(evt.data.volume, sessionGeneration);
                    break;

                case EventType.ShuffleChanged:
                    LogService.Info($"{logPrefix} Shuffle: {evt.data.shuffle}");
                    UpdateShuffle(evt.data.shuffle, sessionGeneration);
                    break;

                case EventType.RepeatChanged:
                    LogService.Info($"{logPrefix} Repeat Mode: {evt.data.repeat_mode}");
                    UpdateRepeat(evt.data.repeat_mode, sessionGeneration);
                    break;

                case EventType.Seeked:
                    PublishPlaybackEvent(new LibrespotPlaybackEvent
                    {
                        State = _playbackState,
                        PlayRequestId = evt.data.play_request_id,
                        AudioGeneration = evt.data.audio_generation,
                        SessionGeneration = sessionGeneration,
                        TrackUri = ReadString(evt.data.track_uri),
                        PositionMs = evt.data.position_ms,
                        IsSeek = true
                    }, sessionGeneration);
                    LogService.Info($"{logPrefix} Seek acknowledged at {evt.data.position_ms}ms");
                    PublishPositionUpdate(
                        evt.data.position_ms,
                        LibrespotPositionUpdateOrigin.SeekAcknowledgement,
                        sessionGeneration);
                    break;
                case EventType.PositionCorrection:
                    PublishPositionUpdate(
                        evt.data.position_ms,
                        LibrespotPositionUpdateOrigin.PositionCorrection,
                        sessionGeneration);
                    break;
                case EventType.PositionChanged:
                    PublishPositionUpdate(
                        evt.data.position_ms,
                        LibrespotPositionUpdateOrigin.Progress,
                        sessionGeneration);
                    break;

                case EventType.SessionConnected:
                    string user = ReadString(evt.data.session_user);
                    LogService.Info($"{logPrefix} Connected as user: {user}");
                    var reusableCredentials = ReadReusablePlaybackCredentials();
                    if (!string.IsNullOrWhiteSpace(reusableCredentials))
                    {
                        var credentialArgs = new PlaybackCredentialsEventArgs
                        {
                            CredentialsJson = reusableCredentials,
                            SessionUser = user
                        };
                        RaiseOnMainThread(
                            () => PlaybackCredentialsAvailable?.Invoke(this, credentialArgs),
                            nameof(PlaybackCredentialsAvailable),
                            sessionGeneration);
                    }
                    OnSessionChanged(true, user, sessionGeneration);
                    break;

                case EventType.SessionDisconnected:
                    LogService.Info($"{logPrefix} Session Disconnected");
                    OnSessionChanged(false, null, sessionGeneration);
                    break;

                case EventType.ClientChanged:
                    string client = ReadString(evt.data.client_name);
                    LogService.Info($"{logPrefix} Active Client switched to: {client}");
                    UpdateClientInfo(client, sessionGeneration);
                    break;

                case EventType.PlaybackAuthorizationRejected:
                    LogService.Warn($"{logPrefix} Spotify rejected the playback authorization.");
                    _activePlaybackAuthorization = null;
                    RaiseOnMainThread(
                        () => PlaybackAuthorizationRejected?.Invoke(this, EventArgs.Empty),
                        nameof(PlaybackAuthorizationRejected),
                        sessionGeneration);
                    break;

                case EventType.AutoPlayChanged:
                    LogService.Info($"{logPrefix} AutoPlay: {evt.data.auto_play}");
                    UpdateAutoPlay(evt.data.auto_play, sessionGeneration);
                    break;

                case EventType.ExplicitFilterChanged:
                    LogService.Info($"{logPrefix} Explicit Filter: {evt.data.filter_explicit}");
                    UpdateExplicitFilter(evt.data.filter_explicit, sessionGeneration);
                    break;

                case EventType.AddedToQueue:
                    LogService.Info($"{logPrefix} Track added to queue: {ReadString(evt.data.track_uri)}");
                    break;

                case EventType.Panic:
                    string panicMsg = ReadString(evt.data.log_msg);
                    LogService.Error($"{ts} [CRITICAL PANIC] {panicMsg}");
                    RaisePanic(panicMsg, sessionGeneration);
                    break;

                default:
                    LogService.Info($"{logPrefix} No specific handler for this event.");
                    break;
            }
        }

        private void OnSessionChanged(bool connected, string username, long sessionGeneration)
        {
            LibrespotSessionState snapshot;
            lock (_stateLock)
            {
                if (sessionGeneration != SessionGeneration || _disposed)
                    return;

                _session = new LibrespotSessionState
                {
                    IsConnected = connected,
                    SessionGeneration = sessionGeneration,
                    UserName = username,
                    AuthNeeded = !connected
                };
                snapshot = _session;
            }
            RaiseOnMainThread(() => SessionStateChanged?.Invoke(this, snapshot), nameof(SessionStateChanged), sessionGeneration);
        }

        private string ReadReusablePlaybackCredentials()
        {
            var instance = _instance;
            if (instance == IntPtr.Zero)
                return null;

            var value = Librespot.librespot_get_playback_credentials(instance);
            if (value == IntPtr.Zero)
                return null;

            try
            {
                return ReadString(value);
            }
            finally
            {
                Librespot.librespot_string_free(value);
            }
        }

        private void UpdatePlaybackState(LibrespotPlaybackState state)
        {
            UpdatePlaybackState(state, default(EventData), SessionGeneration);
        }

        private void UpdatePlaybackState(
            LibrespotPlaybackState state,
            EventData data,
            long sessionGeneration,
            bool isUnavailable = false,
            bool isAudioKeyUnavailable = false)
        {
            lock (_stateLock)
            {
                if (sessionGeneration != SessionGeneration || _disposed)
                    return;

                _playbackState = state;
            }
            var playbackEvent = new LibrespotPlaybackEvent
            {
                State = state,
                PlayRequestId = data.play_request_id,
                AudioGeneration = data.audio_generation,
                SessionGeneration = sessionGeneration,
                TrackUri = ReadString(data.track_uri),
                PositionMs = data.position_ms,
                IsUnavailable = isUnavailable,
                IsAudioKeyUnavailable = isAudioKeyUnavailable
            };
            RaiseOnMainThread(() => PlaybackStateChanged?.Invoke(this, state), nameof(PlaybackStateChanged), sessionGeneration);
            PublishPlaybackEvent(playbackEvent, sessionGeneration);
        }

        private void PublishPlaybackEvent(LibrespotPlaybackEvent playbackEvent, long sessionGeneration)
        {
            RaiseOnMainThread(() => PlaybackEvent?.Invoke(this, playbackEvent), nameof(PlaybackEvent), sessionGeneration);
        }

        private void UpdateTrack(LibrespotTrackInfo track, long sessionGeneration)
        {
            lock (_stateLock)
            {
                if (sessionGeneration != SessionGeneration || _disposed)
                    return;

                _currentTrack = track;
            }
            RaiseOnMainThread(() => TrackChanged?.Invoke(this, track), nameof(TrackChanged), sessionGeneration);
        }

        private void UpdateVolume(ushort volume, long sessionGeneration)
        {
            lock (_stateLock)
            {
                if (sessionGeneration != SessionGeneration || _disposed)
                    return;

                _volume = volume;
            }
            RaiseOnMainThread(() => VolumeChanged?.Invoke(this, volume), nameof(VolumeChanged), sessionGeneration);
        }

        private void OnEndOfTrack()
        {
            OnEndOfTrack(null, 0, SessionGeneration);
        }

        private void OnEndOfTrack(string trackUri, ulong playRequestId, long sessionGeneration)
        {
            LogService.Info($"[LibreSpot] End of track reached. {trackUri}");
            var boundary = new LibrespotTrackBoundaryInfo
            {
                TrackUri = trackUri,
                PlayRequestId = playRequestId,
                SessionGeneration = sessionGeneration
            };
            RaiseOnMainThread(() => EndOfTrack?.Invoke(this, boundary), nameof(EndOfTrack), sessionGeneration);
        }

        private static LibrespotTrackBoundaryInfo CreateTrackBoundaryInfo(EventData data, long sessionGeneration)
        {
            return new LibrespotTrackBoundaryInfo
            {
                TrackUri = ReadString(data.track_uri),
                PlayRequestId = data.play_request_id,
                SessionGeneration = sessionGeneration
            };
        }

        private void UpdateClientInfo(string clientName, long sessionGeneration)
        {
            lock (_stateLock)
            {
                if (sessionGeneration != SessionGeneration || _disposed)
                    return;
                ActiveClientName = clientName;
            }
            LogService.Info($"[LibreSpot] Active Client: {clientName}");
        }

        private void UpdateAutoPlay(bool enabled, long sessionGeneration)
        {
            lock (_stateLock)
            {
                if (sessionGeneration != SessionGeneration || _disposed)
                    return;
                IsAutoPlayEnabled = enabled;
            }
            LogService.Info($"[LibreSpot] AutoPlay updated: {enabled}");
        }

        private void UpdateExplicitFilter(bool enabled, long sessionGeneration)
        {
            lock (_stateLock)
            {
                if (sessionGeneration != SessionGeneration || _disposed)
                    return;
                IsExplicitFilterEnabled = enabled;
            }
            LogService.Info($"[LibreSpot] Explicit Filter updated: {enabled}");
        }

        private void PublishPositionUpdate(uint positionMs, LibrespotPositionUpdateOrigin origin, long sessionGeneration)
        {
            // This callback can be emitted by the decoder for every packet. Keep
            // it off the UI dispatcher; MediaService coalesces it on its bounded
            // display timer. No position update is ever translated into a seek.
            try
            {
                if (sessionGeneration != SessionGeneration || _disposed)
                    return;

                PositionChanged?.Invoke(this, new LibrespotPositionUpdate
                {
                    PositionMs = positionMs,
                    Origin = origin,
                    SessionGeneration = sessionGeneration
                });
            }
            catch (Exception ex)
            {
                LogService.Error(ex, "Librespot position observer failed");
            }
        }

        private void UpdateShuffle(bool enabled, long sessionGeneration)
        {
            LogService.Info($"[LibreSpot] Shuffle updated: {enabled}");
            lock (_stateLock)
            {
                if (sessionGeneration != SessionGeneration || _disposed)
                    return;
                _shuffle = enabled;
            }
            RaiseOnMainThread(() => ShuffleChanged?.Invoke(this, enabled), nameof(ShuffleChanged), sessionGeneration);
        }

        private void UpdateRepeat(uint mode, long sessionGeneration)
        {
            LogService.Info($"[LibreSpot] Repeat mode updated: {mode}");
            lock (_stateLock)
            {
                if (sessionGeneration != SessionGeneration || _disposed)
                    return;
                _repeatMode = mode;
            }
            RaiseOnMainThread(() => RepeatChanged?.Invoke(this, mode), nameof(RepeatChanged), sessionGeneration);
        }

        private void RaisePanic(string message, long sessionGeneration)
        {
            if (message == null) return;
            RaiseOnMainThread(() => Panic?.Invoke(this, message), nameof(Panic), sessionGeneration);
        }

        private void RaiseOnMainThread(Action action, string eventName, long sessionGeneration)
        {
            try
            {
                var dispatcher = CoreApplication.MainView?.CoreWindow?.Dispatcher;
                if (dispatcher != null && !dispatcher.HasThreadAccess)
                {
                    UiResponsivenessTelemetry.DispatcherWorkQueued();
                    try
                    {
                        var ignored = dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                        {
                            try
                            {
                                if (sessionGeneration != SessionGeneration || _disposed)
                                    return;
                                action();
                            }
                            catch (Exception ex)
                            {
                                LogService.Error(ex, $"Librespot event handler failed for {eventName}");
                            }
                            finally
                            {
                                UiResponsivenessTelemetry.DispatcherWorkCompleted();
                            }
                        });
                    }
                    catch
                    {
                        UiResponsivenessTelemetry.DispatcherWorkCompleted();
                        throw;
                    }
                    return;
                }

                if (sessionGeneration == SessionGeneration && !_disposed)
                    action();
            }
            catch (Exception ex)
            {
                LogService.Error(ex, $"Librespot event dispatch failed for {eventName}");
            }
        }

        private async Task RecreateInstanceWithPlaybackAuthAsync(PlaybackConnectionMaterial authorization)
        {
            long generation = Interlocked.Increment(ref _sessionGeneration);
            if (_instance != IntPtr.Zero)
            {
                var instance = _instance;
                _instance = IntPtr.Zero;
                await FreeNativeInstanceAsync(instance, "access-token-recreation").ConfigureAwait(false);
            }

            lock (_stateLock)
            {
                _session = new LibrespotSessionState
                {
                    IsConnected = false,
                    SessionGeneration = generation,
                    AuthNeeded = false
                };
                _playbackState = LibrespotPlaybackState.Stopped;
                _currentTrack = null;
                ActiveClientName = null;
                IsAutoPlayEnabled = false;
                IsExplicitFilterEnabled = false;
            }

            LibrespotCallback callback = (evt, userData) => OnLibrespotEvent(evt, userData, generation);
            _callbackDelegate = callback;
            _sessionCallbacks.Add(callback);

            var cfg = BuildConfig(authorization);
            try
            {
                _instance = await Task.Run(
                    () => Librespot.librespot_new(cfg, _callbackDelegate, IntPtr.Zero))
                    .ConfigureAwait(false);
                if (_instance == IntPtr.Zero)
                    throw new InvalidOperationException("librespot_new (with playback authorization) returned NULL.");
            }
            finally
            {
                FreeConfig(cfg);
            }

            LogService.Info($"[LibrespotService.RecreateInstanceWithPlaybackAuthAsync] Native session created. sessionGeneration={generation}.");
        }

        private static async Task FreeNativeInstanceAsync(IntPtr instance, string reason)
        {
            if (instance == IntPtr.Zero)
                return;

            var stopwatch = Stopwatch.StartNew();
            await Task.Run(() => Librespot.librespot_free(instance)).ConfigureAwait(false);
            stopwatch.Stop();
            LogService.Info(
                $"[LibrespotService.FreeNativeInstanceAsync] Native runner stopped off-dispatcher. reason={reason}, elapsedMs={stopwatch.ElapsedMilliseconds}.");
        }

        private static string[] CreateNativeQueueWindow(string[] tracks, string startUri)
        {
            if (tracks == null || tracks.Length <= NativeQueueWindowSize)
                return tracks;

            var startIndex = string.IsNullOrWhiteSpace(startUri)
                ? 0
                : Array.FindIndex(
                    tracks,
                    uri => string.Equals(uri, startUri, StringComparison.OrdinalIgnoreCase));
            if (startIndex < 0)
                startIndex = 0;

            var windowStart = Math.Max(0, startIndex - NativeQueueLookbehind);
            if (windowStart + NativeQueueWindowSize > tracks.Length)
                windowStart = tracks.Length - NativeQueueWindowSize;

            return tracks
                .Skip(windowStart)
                .Take(NativeQueueWindowSize)
                .ToArray();
        }

        private LibrespotConfig BuildConfig(PlaybackConnectionMaterial authorization)
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
                access_token = string.IsNullOrWhiteSpace(authorization?.BootstrapAccessToken)
                    ? IntPtr.Zero
                    : AllocUtf8String(authorization.BootstrapAccessToken),
                playback_credentials = string.IsNullOrWhiteSpace(authorization?.StoredCredentials)
                    ? IntPtr.Zero
                    : AllocUtf8String(authorization.StoredCredentials),
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
            FreeHGlobalIfNeeded(cfg.playback_credentials);
        }

        private static void FreeHGlobalIfNeeded(IntPtr ptr)
        {
            if (ptr != IntPtr.Zero)
                Marshal.FreeHGlobal(ptr);
        }
    }
}
