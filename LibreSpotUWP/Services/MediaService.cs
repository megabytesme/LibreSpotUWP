using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using SpotifyAPI.Web;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Networking.Connectivity;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using static LibreSpotUWP.Interop.Librespot;

namespace LibreSpotUWP.Services
{
    public sealed class MediaService : IMediaService
    {
        private readonly ILibrespotService _librespot;
        private readonly ISpotifyAuthService _auth;
        private readonly ISpotifyWebService _web;

        private readonly object _lock = new object();

        private MediaState _state = new MediaState();
        private MediaPlayer _mediaPlayer;
        private SystemMediaTransportControls _smtc;

        private LibrespotRingBufferPlayer _ringPlayer;

        private DispatcherTimer _positionTimer;
        private DispatcherTimer _volumeDebounceTimer;
        private ushort _pendingVolume;
        private bool _volumeDirty = false;
        private string[] _offlineQueue = Array.Empty<string>();
        private int _offlineQueueIndex = -1;
        private int _contextResolutionVersion;
        private PlaybackSnapshot _lastPlaybackSnapshot;
        private uint _lastPersistedSnapshotPositionMs = uint.MaxValue;
        private uint _pendingRestoreSeekMs = uint.MaxValue;

        public MediaState Current => _state;
        public event EventHandler<MediaState> MediaStateChanged;

        private const string VolumeKey = "UserVolume";
        private const string PlaybackSnapshotKey = "LastPlaybackSnapshot";

        private sealed class PlaybackSnapshot
        {
            public string TrackUri { get; set; }
            public string TrackName { get; set; }
            public string TrackArtist { get; set; }
            public string TrackAlbum { get; set; }
            public string TrackCoverUrl { get; set; }
            public string ContextUri { get; set; }
            public string ContextName { get; set; }
            public uint PositionMs { get; set; }
            public uint DurationMs { get; set; }
            public bool WasPlaying { get; set; }
            public ushort Volume { get; set; }
            public string ArtworkUri { get; set; }
        }

        public MediaService(
            ILibrespotService librespot,
            ISpotifyAuthService auth,
            ISpotifyWebService web)
        {
            _librespot = librespot;
            _auth = auth;
            _web = web;
        }

        public async Task InitializeAsync()
        {
            _mediaPlayer = new MediaPlayer();
            _mediaPlayer.AutoPlay = false;
            _mediaPlayer.AudioCategory = MediaPlayerAudioCategory.Media;

            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            var hasSavedVolume = settings.Values.TryGetValue(VolumeKey, out object saved);
            var savedVolume = hasSavedVolume && saved is ushort rawSaved ? rawSaved : (ushort?)null;
            var currentVolume = _librespot.Volume;
            var initialVolume = savedVolume ?? (currentVolume > 0 ? currentVolume : (ushort)65535);

            if (!savedVolume.HasValue)
            {
                settings.Values[VolumeKey] = initialVolume;
            }

            if (_librespot.Volume != initialVolume)
                await _librespot.SetVolumeAsync(initialVolume);

            _pendingVolume = initialVolume;
            UpdateState(s => s.Volume = initialVolume);

            var commandManager = _mediaPlayer.CommandManager;
            commandManager.IsEnabled = true;
            commandManager.PlayBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            commandManager.PauseBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            commandManager.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            commandManager.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;

            _smtc = _mediaPlayer.SystemMediaTransportControls;
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsStopEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;

            _smtc.ButtonPressed += OnSmtcButtonPressed;

            _librespot.TrackChanged += OnTrackChanged;
            _librespot.PlaybackStateChanged += OnPlaybackChanged;
            _librespot.SessionStateChanged += OnSessionStateChanged;
            _librespot.VolumeChanged += OnVolumeChanged;
            _librespot.ShuffleChanged += OnShuffleChanged;
            _librespot.RepeatChanged += OnRepeatChanged;
            _librespot.EndOfTrack += OnEndOfTrack;

            _auth.AuthStateChanged += OnAuthChanged;

            _mediaPlayer.Source = CreateSilentMediaSource();
            await RestorePlaybackSnapshotAsync();

            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _positionTimer.Tick += PositionTimer_Tick;
            _positionTimer.Start();

            _volumeDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _volumeDebounceTimer.Tick += VolumeDebounceTimer_Tick;
            _volumeDebounceTimer.Start();

            NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
            UpdateConnectivityState();

            await Task.CompletedTask;
        }

        private void PositionTimer_Tick(object sender, object e)
        {
            if (_state.PlaybackState != LibrespotPlaybackState.Playing)
                return;

            uint pos = _librespot.GetPositionMs();

            UpdateState(s =>
            {
                s.PositionMs = pos;
            });

            UpdateSmtcTimeline(pos);
            if (pos != _lastPersistedSnapshotPositionMs)
            {
                _lastPersistedSnapshotPositionMs = pos;
                PersistPlaybackSnapshot();
            }
        }

        private void UpdateSmtcTimeline(uint positionMs)
        {
            if (_smtc == null)
                return;

            var timelineProperties = new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                MaxSeekTime = TimeSpan.FromMilliseconds(_state.DurationMs),
                EndTime = TimeSpan.FromMilliseconds(_state.DurationMs),
                Position = TimeSpan.FromMilliseconds(positionMs)
            };

            _smtc.UpdateTimelineProperties(timelineProperties);
        }

        public async Task PlayAsync(string contextUri, string startUri = null)
        {
            var isOffline = !ConnectivityHelper.HasInternetAccess();
            var wasOffline = Current.IsOffline;
            var originalContextUri = contextUri;
            var playbackContextUri = GetPlaybackContextUri(originalContextUri, startUri);
            var directTrackUri = !string.IsNullOrWhiteSpace(startUri) ? startUri : contextUri;
            var isDirectTrack = !string.IsNullOrWhiteSpace(directTrackUri) &&
                directTrackUri.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase);

            if (isOffline && string.IsNullOrWhiteSpace(startUri) && !isDirectTrack)
            {
                var offlineQueue = await App.OfflineCatalog.GetTrackUrisForContextAsync(contextUri);
                if (offlineQueue.Count == 0)
                {
                    UpdateState(s =>
                    {
                        s.IsOffline = true;
                        s.StatusMessage = "Offline. This album or playlist has not been downloaded yet.";
                    });
                    return;
                }

                _offlineQueue = offlineQueue.ToArray();
                _offlineQueueIndex = 0;
                contextUri = _offlineQueue[0];
                startUri = null;
                LogService.Info($"[MediaService.PlayAsync] Offline context playback for {originalContextUri} starting at {_offlineQueue[0]}.");
            }
            else if (isOffline)
            {
                var queueSeed = await App.OfflineCatalog.GetTrackUrisForContextAsync(originalContextUri);
                _offlineQueue = queueSeed.ToArray();
                _offlineQueueIndex = Array.IndexOf(_offlineQueue, directTrackUri);
                LogService.Info($"[MediaService.PlayAsync] Offline direct playback for {directTrackUri}. Queue size={_offlineQueue.Length}, index={_offlineQueueIndex}.");
            }
            else
            {
                _offlineQueue = Array.Empty<string>();
                _offlineQueueIndex = -1;
                _lastPersistedSnapshotPositionMs = uint.MaxValue;
            }

            if (isOffline && string.IsNullOrWhiteSpace(contextUri))
            {
                UpdateState(s =>
                {
                    s.IsOffline = true;
                    s.StatusMessage = "Offline. Select a downloaded track to continue.";
                });
                return;
            }

            if (!isOffline && wasOffline)
                await StopOfflinePlaybackForOnlineTransitionAsync();

            var librespotReady = (_librespot as LibrespotService)?.HasInstance == true;
            var requiresOnlineReconnect = !isOffline && !_librespot.Session.IsConnected;
            if (!librespotReady || requiresOnlineReconnect)
            {
                var accessToken = isOffline
                    ? await _auth.GetAccessToken()
                    : await _auth.EnsureValidAccessTokenAsync(interactive: true);

                if (string.IsNullOrEmpty(accessToken) && !isOffline)
                    return;

                if (string.IsNullOrEmpty(accessToken) && isOffline)
                {
                    UpdateState(s =>
                    {
                        s.IsOffline = true;
                        s.StatusMessage = "Offline. Sign in once while online before cached playback can start.";
                    });
                    return;
                }

                await _librespot.ConnectWithAccessTokenAsync(accessToken);
            }

            if (isOffline && !string.IsNullOrWhiteSpace(startUri))
            {
                contextUri = startUri;
                startUri = null;
            }

            UpdateState(s =>
            {
                s.IsOffline = isOffline;
                s.ContextUri = playbackContextUri;
                s.ContextName = null;
                s.StatusMessage = isOffline
                    ? "Offline. Trying to play the selected cached track."
                    : null;
            });

            _ = ResolveAndApplyContextNameAsync(playbackContextUri, Interlocked.Increment(ref _contextResolutionVersion));

            LogService.Info($"[MediaService.PlayAsync] Loading context={contextUri}, start={startUri ?? "(null)"}, offline={isOffline}.");
            await _librespot.LoadAndPlayAsync(contextUri, startUri);

            await EnsureRingPlayerAsync();

            _ringPlayer.Start();
        }

        private async Task StopOfflinePlaybackForOnlineTransitionAsync()
        {
            LogService.Info("[MediaService.StopOfflinePlaybackForOnlineTransitionAsync] Stopping offline playback before switching back online.");

            _offlineQueue = Array.Empty<string>();
            _offlineQueueIndex = -1;

            _ringPlayer?.Stop();
            _mediaPlayer?.Pause();

            await _librespot.StopAsync();

            UpdateState(s =>
            {
                s.PlaybackState = LibrespotPlaybackState.Stopped;
                s.PositionMs = 0;
            });
        }

        public async Task PauseAsync()
        {
            await _librespot.PauseAsync();
        }

        public async Task ResumeAsync()
        {
            if (_lastPlaybackSnapshot != null && _state.PlaybackState == LibrespotPlaybackState.Paused)
            {
                _pendingRestoreSeekMs = _lastPlaybackSnapshot.PositionMs;
                await PlayAsync(
                    string.IsNullOrWhiteSpace(_lastPlaybackSnapshot.ContextUri) ? _lastPlaybackSnapshot.TrackUri : _lastPlaybackSnapshot.ContextUri,
                    _lastPlaybackSnapshot.TrackUri);

                return;
            }

            await _librespot.ResumeAsync();
        }

        public async Task StopAsync()
        {
            await _librespot.StopAsync();
        }

        private void VolumeDebounceTimer_Tick(object sender, object e)
        {
            if (!_volumeDirty)
                return;

            _volumeDirty = false;
            _ = _librespot.SetVolumeAsync(_pendingVolume);
        }

        public void SetVolumeDebounced(double percent)
        {
            ushort raw = (ushort)(percent * 65535 / 100);
            _pendingVolume = raw;
            _volumeDirty = true;

            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            settings.Values[VolumeKey] = raw;

            UpdateState(s => s.Volume = raw);
        }

        public async Task SetShuffleAsync(bool enabled)
        {
            await _librespot.SetShuffleAsync(enabled);

            UpdateState(s => s.Shuffle = enabled);
        }

        public async Task SetRepeatAsync(int mode)
        {
            await _librespot.SetRepeatAsync((uint)mode);

            UpdateState(s => s.RepeatMode = mode);
        }

        public async Task SetCurrentTrackPersistedAsync(bool persisted)
        {
            var track = Current?.Metadata;
            if ((track == null || string.IsNullOrWhiteSpace(track.Uri)) && !string.IsNullOrWhiteSpace(Current?.Track?.Uri))
            {
                var offlineTrack = await App.OfflineCatalog.GetDownloadedTrackAsync(Current.Track.Uri);
                if (offlineTrack != null)
                {
                    track = new FullTrack
                    {
                        Uri = offlineTrack.TrackUri,
                        Id = offlineTrack.TrackId,
                        Name = offlineTrack.Name,
                        DurationMs = offlineTrack.DurationMs,
                        Album = new SimpleAlbum
                        {
                            Id = offlineTrack.AlbumId,
                            Name = offlineTrack.AlbumName,
                            Images = string.IsNullOrWhiteSpace(offlineTrack.ImageUrl)
                                ? null
                                : new System.Collections.Generic.List<Image> { new Image { Url = offlineTrack.ImageUrl } }
                        },
                        Artists = offlineTrack.ArtistNames?
                            .Select(name => new SimpleArtist { Name = name })
                            .ToList()
                    };
                }
            }

            if (track == null || string.IsNullOrWhiteSpace(track.Uri))
            {
                LogService.Warn("[MediaService.SetCurrentTrackPersistedAsync] No current track metadata is available.");
                return;
            }

            LogService.Info($"[MediaService.SetCurrentTrackPersistedAsync] Setting persisted={persisted} for {track.Uri}.");
            await App.OfflineCatalog.SetTrackPersistedAsync(track, persisted);
            UpdateState(s => s.IsCurrentTrackPersisted = App.OfflineCatalog.IsTrackPersisted(track.Uri));
        }

        public Task SetVolumeAsync(ushort v) => _librespot.SetVolumeAsync(v);

        public Task SetAudioEffectsPresetAsync(string preset)
        {
            UserSettings.AudioEffectsPreset = string.IsNullOrWhiteSpace(preset) ? "None" : preset;
            _ringPlayer?.SetAudioEffectsPreset(UserSettings.AudioEffectsPreset);
            return Task.CompletedTask;
        }

        public async Task RefreshCurrentTrackMetadataAsync()
        {
            var trackUri = Current?.Track?.Uri;
            if (string.IsNullOrWhiteSpace(trackUri) || !trackUri.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase))
                return;

            var id = trackUri.Replace("spotify:track:", "");
            var metadata = await _web.GetTrackAsync(id, true);

            UpdateState(state =>
            {
                state.Metadata = metadata?.Value;
                state.IsTrackMetadataFromCache = metadata?.IsFromCache == true;
                state.ArtworkUri = ResolveArtworkUri(metadata?.Value, state.Track, null);
                state.StatusMessage = metadata?.IsFromCache == true
                    ? "Showing refreshed track details from cache."
                    : null;
            });

            UpdateSmtcDisplay();
        }
        public void Next()
        {
            if (TryPlayOfflineRelativeTrack(1))
                return;

            _librespot.Next();
        }

        public void Previous()
        {
            if (TryPlayOfflineRelativeTrack(-1))
                return;

            _librespot.Previous();
        }
        public void Seek(uint posMs) => _librespot.Seek(posMs);

        private async Task EnsureRingPlayerAsync()
        {
            if (_ringPlayer != null)
                return;

            var props = (_librespot as LibrespotService)?.EncodingProperties
                        ?? AudioEncodingProperties.CreatePcm(44100, 2, 16);

            _ringPlayer = new LibrespotRingBufferPlayer(props);
            await _ringPlayer.InitializeAsync();
            _ringPlayer.SetAudioEffectsPreset(UserSettings.AudioEffectsPreset);
        }

        private async void OnTrackChanged(object sender, LibrespotTrackInfo track)
        {
            try
            {
                FullTrack metadata = null;
                CacheResponse<FullTrack> trackResponse = null;
                OfflineTrackEntry offlineTrack = null;

                if (!string.IsNullOrWhiteSpace(track.Uri))
                {
                    offlineTrack = await App.OfflineCatalog.GetDownloadedTrackAsync(track.Uri);
                    var id = track.Uri.Replace("spotify:track:", "");
                    try
                    {
                        trackResponse = await _web.GetTrackAsync(id, false);
                        metadata = trackResponse.Value;
                    }
                    catch (Exception ex)
                    {
                        LogService.Warn($"[MediaService.OnTrackChanged] Unable to load track metadata for {track.Uri}: {ex.Message}");
                    }
                }

                UpdateState(state =>
                {
                    state.Track = track;
                    state.Metadata = metadata;
                    state.DurationMs = (uint)track.Duration.TotalMilliseconds;
                    state.IsTrackMetadataFromCache = trackResponse?.IsFromCache == true;
                    state.IsCurrentTrackPersisted = App.OfflineCatalog.IsTrackPersisted(track.Uri);
                    state.IsOffline = !ConnectivityHelper.HasInternetAccess();
                    state.StatusMessage = BuildPlaybackStatusMessage(trackResponse);
                    state.ArtworkUri = ResolveArtworkUri(metadata, track, offlineTrack);

                    if (string.IsNullOrWhiteSpace(state.ContextUri) ||
                        state.ContextUri.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase))
                    {
                        state.ContextUri = metadata?.Album?.Uri ?? state.ContextUri;
                    }

                    if (string.IsNullOrWhiteSpace(state.ContextName))
                        state.ContextName = ResolveImmediateContextName(state.ContextUri, metadata);

                    if (_lastPlaybackSnapshot != null && string.Equals(_lastPlaybackSnapshot.TrackUri, track.Uri, StringComparison.OrdinalIgnoreCase))
                    {
                        state.PositionMs = _lastPlaybackSnapshot.PositionMs;
                        state.DurationMs = _lastPlaybackSnapshot.DurationMs > 0 ? _lastPlaybackSnapshot.DurationMs : state.DurationMs;
                        if (!string.IsNullOrWhiteSpace(_lastPlaybackSnapshot.ArtworkUri))
                            state.ArtworkUri = _lastPlaybackSnapshot.ArtworkUri;
                    }
                });

                UpdateSmtcDisplay();
                PersistPlaybackSnapshot();

                await EnsureRingPlayerAsync();

                if (_pendingRestoreSeekMs != uint.MaxValue &&
                    _lastPlaybackSnapshot != null &&
                    string.Equals(_lastPlaybackSnapshot.TrackUri, track.Uri, StringComparison.OrdinalIgnoreCase))
                {
                    var seekPosition = _pendingRestoreSeekMs;
                    _pendingRestoreSeekMs = uint.MaxValue;
                    if (seekPosition > 0)
                        _librespot.Seek(seekPosition);

                    UpdateState(state => state.PositionMs = seekPosition);
                    UpdateSmtcTimeline(seekPosition);
                    PersistPlaybackSnapshot();
                }

                if (_state.PlaybackState == LibrespotPlaybackState.Playing)
                {
                    _mediaPlayer.Play();
                    _ringPlayer.Start();
                }
            }
            catch (Exception ex)
            {
                LogService.Error(ex, $"[MediaService.OnTrackChanged] Unhandled error while processing track change for {track?.Uri ?? "(null)"}");
            }
        }

        private async void OnPlaybackChanged(object sender, LibrespotPlaybackState state)
        {
            try
            {
                UpdateState(s => s.PlaybackState = state);

                uint currentPos = _librespot.GetPositionMs();
                UpdateState(s => s.PositionMs = currentPos);
                UpdateSmtcTimeline(currentPos);
                PersistPlaybackSnapshot();

                switch (state)
                {
                    case LibrespotPlaybackState.Playing:
                        if (_pendingRestoreSeekMs != uint.MaxValue)
                        {
                            var seekPosition = _pendingRestoreSeekMs;
                            _pendingRestoreSeekMs = uint.MaxValue;
                            if (seekPosition > 0)
                                _librespot.Seek(seekPosition);

                            currentPos = seekPosition;
                            UpdateState(s => s.PositionMs = seekPosition);
                            UpdateSmtcTimeline(seekPosition);
                            PersistPlaybackSnapshot();
                        }

                        await EnsureRingPlayerAsync();

                        if (_mediaPlayer.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
                            _mediaPlayer.Play();

                        _ringPlayer.Start();
                        _smtc.PlaybackStatus = MediaPlaybackStatus.Playing;
                        break;

                    case LibrespotPlaybackState.Paused:
                        _ringPlayer?.Stop();

                        if (_mediaPlayer.PlaybackSession.PlaybackState != MediaPlaybackState.Paused)
                            _mediaPlayer.Pause();

                        _smtc.PlaybackStatus = MediaPlaybackStatus.Paused;
                        break;

                    case LibrespotPlaybackState.Stopped:
                        _ringPlayer?.Stop();
                        _mediaPlayer.Pause();
                        _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
                        break;
                }
            }
            catch (Exception ex)
            {
                LogService.Error(ex, $"[MediaService.OnPlaybackChanged] Unhandled error while processing playback state {state}");
            }
        }

        private async void OnSessionStateChanged(object sender, LibrespotSessionState ev)
        {
            try
            {
                UpdateState(s =>
                {
                    s.IsSessionConnected = ev.IsConnected;
                    s.IsOffline = !ConnectivityHelper.HasInternetAccess();

                    if (!ev.IsConnected && s.IsOffline)
                        s.StatusMessage = "Offline. Cached tracks can still play when you select them directly.";
                    else if (ev.IsConnected && !s.IsTrackMetadataFromCache)
                        s.StatusMessage = null;
                });

                switch (ev.IsConnected)
                {
                    case true:
                        await App.BackgroundExecution.RequestKeepAliveAsync();
                        break;

                    case false:
                        App.BackgroundExecution.StopKeepAlive();
                        break;
                }
            }
            catch (Exception ex)
            {
                LogService.Error(ex, $"[MediaService.OnSessionStateChanged] Unhandled error while processing session connected={ev?.IsConnected}");
            }
        }

        private void OnVolumeChanged(object sender, ushort volume)
        {
            _pendingVolume = volume;
            UpdateState(s => s.Volume = volume);

            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            settings.Values[VolumeKey] = volume;
            PersistPlaybackSnapshot();
        }

        private void OnShuffleChanged(object sender, bool enabled)
        {
            UpdateState(s => s.Shuffle = enabled);
        }

        private void OnRepeatChanged(object sender, uint mode)
        {
            UpdateState(s => s.RepeatMode = (int)mode);
        }

        private void OnAuthChanged(object sender, AuthState auth)
        {
            if (!ConnectivityHelper.HasInternetAccess())
                return;

            if (auth == null || auth.IsExpired || string.IsNullOrEmpty(auth.AccessToken))
                return;

            if ((_librespot as LibrespotService)?.HasInstance == true)
                return;

            _ = ConnectAfterAuthChangedAsync(auth.AccessToken);
        }

        private async Task ConnectAfterAuthChangedAsync(string accessToken)
        {
            try
            {
                await _librespot.ConnectWithAccessTokenAsync(accessToken);
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MediaService.OnAuthChanged] Unable to reconnect librespot after auth changed: {ex.Message}");
            }
        }

        private void OnNetworkStatusChanged(object sender)
        {
            _ = HandleNetworkStatusChangedAsync();
        }

        private void OnEndOfTrack(object sender, string trackUri)
        {
            LogService.Info($"[MediaService.OnEndOfTrack] Received end-of-track for {trackUri ?? "(unknown)"}.");

            if (TryPlayOfflineRelativeTrack(1))
                return;

            if (Current.IsOffline && _offlineQueue.Length > 0)
            {
                LogService.Info("[MediaService.OnEndOfTrack] Offline queue reached the end.");
                UpdateState(s =>
                {
                    s.StatusMessage = "Offline queue finished.";
                });
            }
        }

        private void UpdateConnectivityState()
        {
            var isOffline = !ConnectivityHelper.HasInternetAccess();
            UpdateState(s => s.IsOffline = isOffline);
        }

        private static string BuildPlaybackStatusMessage(CacheResponse<FullTrack> response)
        {
            if (response == null)
                return null;

            if (response.IsOfflineFallback)
                return "Offline. Track details are coming from cache.";

            if (response.IsFromCache)
                return "Showing cached track details.";

            return null;
        }

        private bool TryPlayOfflineRelativeTrack(int delta)
        {
            if (!Current.IsOffline || _offlineQueue.Length == 0)
                return false;

            var nextIndex = _offlineQueueIndex + delta;
            if (nextIndex < 0 || nextIndex >= _offlineQueue.Length)
                return false;

            _offlineQueueIndex = nextIndex;
            LogService.Info($"[MediaService.TryPlayOfflineRelativeTrack] Advancing offline queue to index {_offlineQueueIndex} ({_offlineQueue[_offlineQueueIndex]}).");
            _ = PlayAsync(_offlineQueue[_offlineQueueIndex], null);
            return true;
        }

        private void UpdateState(Action<MediaState> mutator)
        {
            MediaState snapshot;
            lock (_lock)
            {
                var clone = _state.Clone();
                mutator(clone);
                _state = clone;
                snapshot = clone;
            }

            MediaStateChanged?.Invoke(this, snapshot);
        }

        private async Task RestorePlaybackSnapshotAsync()
        {
            if (!UserSettings.RememberLastPlaybackState)
                return;

            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            if (!settings.Values.TryGetValue(PlaybackSnapshotKey, out object raw) || !(raw is string json) || string.IsNullOrWhiteSpace(json))
                return;

            PlaybackSnapshot snapshot;
            try
            {
                snapshot = JsonConvert.DeserializeObject<PlaybackSnapshot>(json);
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MediaService.RestorePlaybackSnapshotAsync] Unable to parse playback snapshot: {ex.Message}");
                return;
            }

                if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.TrackUri))
                return;

            _lastPlaybackSnapshot = snapshot;
            _lastPersistedSnapshotPositionMs = snapshot.PositionMs;

            var trackInfo = new LibrespotTrackInfo
            {
                Uri = snapshot.TrackUri,
                Name = snapshot.TrackName,
                Artist = snapshot.TrackArtist,
                Album = snapshot.TrackAlbum,
                CoverUrl = snapshot.TrackCoverUrl,
                Duration = TimeSpan.FromMilliseconds(snapshot.DurationMs)
            };

            UpdateState(s =>
            {
                s.Track = trackInfo;
                s.ContextUri = snapshot.ContextUri;
                s.ContextName = snapshot.ContextName;
                s.PositionMs = snapshot.PositionMs;
                s.DurationMs = snapshot.DurationMs;
                s.PlaybackState = LibrespotPlaybackState.Paused;
                s.Volume = snapshot.Volume > 0 ? snapshot.Volume : s.Volume;
                s.ArtworkUri = snapshot.ArtworkUri ?? s.ArtworkUri;
            });

            UpdateSmtcDisplay();
            UpdateSmtcTimeline(snapshot.PositionMs);
            if (_smtc != null)
                _smtc.PlaybackStatus = MediaPlaybackStatus.Paused;

            if (UserSettings.ResumeLastPlaybackIfWasPlaying && snapshot.WasPlaying)
            {
                _pendingRestoreSeekMs = snapshot.PositionMs;

                await PlayAsync(
                    string.IsNullOrWhiteSpace(snapshot.ContextUri) ? snapshot.TrackUri : snapshot.ContextUri,
                    snapshot.TrackUri);
            }
        }

        private void PersistPlaybackSnapshot()
        {
            try
            {
                var state = Current;
                if (state == null || string.IsNullOrWhiteSpace(state.Track?.Uri))
                    return;

                var snapshot = new PlaybackSnapshot
                {
                    TrackUri = state.Track.Uri,
                    TrackName = state.Track.Name,
                    TrackArtist = state.Track.Artist,
                    TrackAlbum = state.Track.Album,
                    TrackCoverUrl = state.Track.CoverUrl,
                    ContextUri = state.ContextUri,
                    ContextName = state.ContextName,
                    PositionMs = state.PositionMs,
                    DurationMs = state.DurationMs,
                    WasPlaying = state.PlaybackState == LibrespotPlaybackState.Playing,
                    Volume = state.Volume,
                    ArtworkUri = state.ArtworkUri
                };

                Windows.Storage.ApplicationData.Current.LocalSettings.Values[PlaybackSnapshotKey] =
                    JsonConvert.SerializeObject(snapshot);
                _lastPlaybackSnapshot = snapshot;
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MediaService.PersistPlaybackSnapshot] Unable to save playback snapshot: {ex.Message}");
            }
        }

        private void UpdateSmtcDisplay()
        {
            if (_smtc == null)
                return;

            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;

            var t = _state.Metadata;
            updater.MusicProperties.Title = t?.Name ?? _state.Track?.Name ?? string.Empty;
            updater.MusicProperties.Artist = t != null
                ? string.Join(", ", t.Artists?.Select(a => a.Name))
                : _state.Track?.Artist ?? string.Empty;
            updater.MusicProperties.AlbumTitle = t?.Album?.Name ?? _state.Track?.Album ?? string.Empty;

            updater.Thumbnail = null;
            if (TryCreateArtworkUri(_state.ArtworkUri, out var artworkUri))
                updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(artworkUri);

            updater.Update();
        }

        private async void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    await ResumeAsync();
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    await PauseAsync();
                    break;
                case SystemMediaTransportControlsButton.Stop:
                    await StopAsync();
                    break;
                case SystemMediaTransportControlsButton.Next:
                    Next();
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    Previous();
                    break;
            }
        }

        private MediaSource CreateSilentMediaSource()
        {
            var props = AudioEncodingProperties.CreatePcm(44100, 2, 16);
            var descriptor = new AudioStreamDescriptor(props);
            var mss = new MediaStreamSource(descriptor);

            mss.CanSeek = false;
            mss.Duration = TimeSpan.FromHours(24);
            mss.BufferTime = TimeSpan.FromSeconds(0);

            TimeSpan currentTime = TimeSpan.Zero;

            mss.Starting += (s, e) =>
            {
                e.Request.SetActualStartPosition(TimeSpan.Zero);
                currentTime = TimeSpan.Zero;
            };

            byte[] silentBuffer = null;
            IBuffer silentIBuffer = null;
            TimeSpan silentDuration = TimeSpan.FromMilliseconds(500);

            mss.SampleRequested += (s, e) =>
            {
                if (silentBuffer == null)
                {
                    int frameSize = (int)props.ChannelCount * ((int)props.BitsPerSample / 8);
                    int samples = (int)(props.SampleRate * (silentDuration.TotalMilliseconds / 1000.0));
                    int bytes = samples * frameSize;

                    silentBuffer = new byte[bytes];
                    silentIBuffer = silentBuffer.AsBuffer();
                }

                var sample = MediaStreamSample.CreateFromBuffer(
                    silentIBuffer,
                    currentTime
                );

                sample.Duration = silentDuration;
                e.Request.Sample = sample;

                currentTime += silentDuration;
            };

            return MediaSource.CreateFromMediaStreamSource(mss);
        }

        private async Task HandleNetworkStatusChangedAsync()
        {
            var wasOffline = _state.IsOffline;
            UpdateConnectivityState();

            if (!ConnectivityHelper.HasInternetAccess())
            {
                if (_librespot.Session.IsConnected)
                {
                    LogService.Info("[MediaService.HandleNetworkStatusChangedAsync] Connectivity lost or offline mode enabled, disconnecting librespot.");
                    await _librespot.DisconnectAsync();
                }
                return;
            }

            try
            {
                if (wasOffline && _state.PlaybackState == LibrespotPlaybackState.Playing && _offlineQueue.Length > 0)
                    return;

                var accessToken = await _auth.EnsureValidAccessTokenAsync(interactive: false);
                if (!string.IsNullOrWhiteSpace(accessToken) && !_librespot.Session.IsConnected)
                {
                    LogService.Info("[MediaService.HandleNetworkStatusChangedAsync] Connectivity restored, reconnecting librespot.");
                    await _librespot.ConnectWithAccessTokenAsync(accessToken);
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MediaService.HandleNetworkStatusChangedAsync] Unable to reconnect after connectivity restored: {ex.Message}");
            }
        }

        private static string ResolveArtworkUri(FullTrack metadata, LibrespotTrackInfo track, OfflineTrackEntry offlineTrack)
        {
            var imageUrl = metadata?.Album?.Images?.FirstOrDefault()?.Url;
            if (!string.IsNullOrWhiteSpace(imageUrl))
                return imageUrl;

            if (!string.IsNullOrWhiteSpace(offlineTrack?.ImageLocalUri))
                return offlineTrack.ImageLocalUri;

            if (!string.IsNullOrWhiteSpace(offlineTrack?.ImageUrl))
                return offlineTrack.ImageUrl;

            return track?.CoverUrl;
        }

        private static bool TryCreateArtworkUri(string value, out Uri uri)
        {
            uri = null;
            return ImageUriHelper.TryCreateImageUri(value, out uri);
        }

        private static string GetPlaybackContextUri(string originalContextUri, string startUri)
        {
            if (!string.IsNullOrWhiteSpace(originalContextUri) &&
                !originalContextUri.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase))
            {
                return originalContextUri;
            }

            if (!string.IsNullOrWhiteSpace(startUri) &&
                !startUri.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase))
            {
                return startUri;
            }

            return originalContextUri;
        }

        private static string ResolveImmediateContextName(string contextUri, FullTrack metadata)
        {
            if (string.IsNullOrWhiteSpace(contextUri))
                return metadata?.Album?.Name;

            if (contextUri.StartsWith("spotify:album:", StringComparison.OrdinalIgnoreCase))
                return metadata?.Album?.Name;

            if (contextUri.StartsWith("spotify:artist:", StringComparison.OrdinalIgnoreCase))
                return metadata?.Artists?.FirstOrDefault()?.Name;

            return null;
        }

        private async Task ResolveAndApplyContextNameAsync(string contextUri, int version)
        {
            if (string.IsNullOrWhiteSpace(contextUri) || !ConnectivityHelper.HasInternetAccess())
                return;

            string contextName = null;

            try
            {
                if (contextUri.StartsWith("spotify:playlist:", StringComparison.OrdinalIgnoreCase))
                {
                    var playlistId = contextUri.Substring("spotify:playlist:".Length);
                    var playlist = await _web.GetPlaylistAsync(playlistId, false).ConfigureAwait(false);
                    contextName = playlist.Value?.Name;
                }
                else if (contextUri.StartsWith("spotify:album:", StringComparison.OrdinalIgnoreCase))
                {
                    var albumId = contextUri.Substring("spotify:album:".Length);
                    var album = await _web.GetAlbumAsync(albumId, false).ConfigureAwait(false);
                    contextName = album.Value?.Name;
                }
                else if (contextUri.StartsWith("spotify:artist:", StringComparison.OrdinalIgnoreCase))
                {
                    var artistId = contextUri.Substring("spotify:artist:".Length);
                    var artist = await _web.GetArtistAsync(artistId, false).ConfigureAwait(false);
                    contextName = artist.Value?.Name;
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MediaService.ResolveAndApplyContextNameAsync] Unable to resolve context name for {contextUri}: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(contextName) || version != _contextResolutionVersion)
                return;

            UpdateState(state =>
            {
                if (string.Equals(state.ContextUri, contextUri, StringComparison.OrdinalIgnoreCase))
                    state.ContextName = contextName;
            });
        }
    }
}
