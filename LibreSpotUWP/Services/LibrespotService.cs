using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Interop;
using LibreSpotUWP.Models;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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

        private LibrespotRingBufferPlayer _player;
        private AudioFormatProbeResult _audioFormat;

        private LibrespotSessionState _session = new LibrespotSessionState();
        private LibrespotPlaybackState _playbackState = LibrespotPlaybackState.Stopped;
        private LibrespotTrackInfo _currentTrack;
        private ushort _volume;

        private bool _initialized;
        private bool _disposed;
        private bool _shuffle;
        private uint _repeatMode;

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
        public event EventHandler<string> LogMessage;
        public event EventHandler<string> Panic;
        public event EventHandler<bool> ShuffleChanged;
        public event EventHandler<uint> RepeatChanged;
        public event EventHandler<uint> Seeked;

        public async Task InitializeAsync()
        {
            ThrowIfDisposed();
            if (_initialized) return;

            _dllHandle = NativeProbe.TryLoadLibreSpot();
            if (_dllHandle == IntPtr.Zero)
                throw new InvalidOperationException("Failed to load librespot.dll");

            _audioFormat = await AudioFormatProbe.ProbeAsync().ConfigureAwait(false);

            _callbackDelegate = OnLibrespotEvent;

            _initialized = true;
        }

        public async Task ConnectWithAccessTokenAsync(string accessToken)
        {
            ThrowIfDisposed();
            if (!_initialized)
                throw new InvalidOperationException("LibrespotService not initialized.");

            if (string.IsNullOrWhiteSpace(accessToken))
                throw new ArgumentException("Access token must not be null or empty.", nameof(accessToken));

            await RecreateInstanceWithAccessTokenAsync(accessToken).ConfigureAwait(false);
        }

        public async Task LoadAndPlayAsync(string spotifyUri)
        {
            ThrowIfDisposed();
            if (!_initialized)
                throw new InvalidOperationException("LibrespotService not initialized.");
            if (_instance == IntPtr.Zero)
                throw new InvalidOperationException("Librespot instance not connected (no access token).");

            if (string.IsNullOrWhiteSpace(spotifyUri))
                throw new ArgumentException("Spotify URI must not be null or empty.", nameof(spotifyUri));

            IntPtr uriPtr = Marshal.StringToHGlobalAnsi(spotifyUri);
            try
            {
                Librespot.librespot_load(_instance, uriPtr, true);
            }
            finally
            {
                Marshal.FreeHGlobal(uriPtr);
            }

            if (_player == null)
            {
                _player = new LibrespotRingBufferPlayer(_audioFormat.EncodingProperties);
                await _player.InitializeAsync().ConfigureAwait(false);
            }
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

        public void Seek(uint posMs)
        {
            if (_instance != IntPtr.Zero) Librespot.librespot_seek(_instance, posMs);
        }

        public void Next()
        {
            if (_instance != IntPtr.Zero) Librespot.librespot_next(_instance);
        }

        public void Previous()
        {
            if (_instance != IntPtr.Zero) Librespot.librespot_prev(_instance);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _player?.Dispose();
            _player = null;

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
            string ts = DateTime.Now.ToString("HH:mm:ss");
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
                    Debug.WriteLine($"{logPrefix} Playback Stopped.");
                    UpdatePlaybackState(LibrespotPlaybackState.Stopped);
                    break;

                case EventType.EndOfTrack:
                    Debug.WriteLine($"{logPrefix} Reached end of track URI: {Marshal.PtrToStringAnsi(evt.data.track_uri)}");
                    OnEndOfTrack();
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
            Debug.WriteLine("[LibreSpot] End of track reached.");
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
            _instance = Librespot.librespot_new(cfg, _callbackDelegate, IntPtr.Zero);
            if (_instance == IntPtr.Zero)
                throw new InvalidOperationException("librespot_new (with token) returned NULL.");

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

            string cacheDir = ApplicationData.Current.LocalFolder.Path;

            return new LibrespotConfig
            {
                device_name = Marshal.StringToHGlobalAnsi(Environment.MachineName),
                device_type = Marshal.StringToHGlobalAnsi(deviceType),
                cache_dir = Marshal.StringToHGlobalAnsi(cacheDir),
                enable_discovery = false,
                enable_volume_normalisation = false,
                bitrate = Bitrate.B320,
                format = _audioFormat.LibrespotFormat,
                username = IntPtr.Zero,
                password = IntPtr.Zero,
                auth_blob = IntPtr.Zero,
                access_token = Marshal.StringToHGlobalAnsi(accessToken)
            };
        }
    }
}