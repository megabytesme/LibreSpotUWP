using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using LibreSpotUWP.Exceptions;
using SpotifyAPI.Web;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Devices;
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
        private readonly SemaphoreSlim _playbackGate = new SemaphoreSlim(1, 1);

        private MediaState _state = new MediaState();
        private MediaPlayer _mediaPlayer;
        private SystemMediaTransportControls _smtc;

        private LibrespotRingBufferPlayer _ringPlayer;

        private DispatcherTimer _positionTimer;
        private DispatcherTimer _volumeDebounceTimer;
        private DispatcherTimer _spotifyConnectTimer;
        private ushort _pendingVolume;
        private bool _volumeDirty = false;
        private bool _refreshingSpotifyConnectPlayback;
        private uint _remotePositionBaseMs;
        private DateTimeOffset _remotePositionUpdatedAt = DateTimeOffset.MinValue;
        private string[] _offlineQueue = Array.Empty<string>();
        private int _offlineQueueIndex = -1;
        private int _contextResolutionVersion;
        private int _playbackContinuationVersion;
        private int _pendingEndOfTrackContinuationVersion;
        private PlaybackSnapshot _lastPlaybackSnapshot;
        private uint _lastPersistedSnapshotPositionMs = uint.MaxValue;
        private uint _pendingRestoreSeekMs = uint.MaxValue;
        private DateTimeOffset _lastSnapshotWriteAt = DateTimeOffset.MinValue;

        public MediaState Current => _state;
        public event EventHandler<MediaState> MediaStateChanged;

        private const string VolumeKey = "UserVolume";
        private const string PlaybackSnapshotKey = "LastPlaybackSnapshot";
        private const int PositionTimerIntervalMs = 500;
        private const int SnapshotWriteIntervalMs = 5000;

        public string CurrentAudioOutputDeviceId => UserSettings.AudioOutputDeviceId;
        public string CurrentSpotifyConnectDeviceId => GetSelectedSpotifyConnectDeviceId();

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
            _librespot.PositionChanged += OnPositionChanged;
            _librespot.SessionStateChanged += OnSessionStateChanged;
            _librespot.VolumeChanged += OnVolumeChanged;
            _librespot.ShuffleChanged += OnShuffleChanged;
            _librespot.RepeatChanged += OnRepeatChanged;
            _librespot.EndOfTrack += OnEndOfTrack;

            _auth.AuthStateChanged += OnAuthChanged;

            _mediaPlayer.Source = CreateSilentMediaSource();
            await RestorePlaybackSnapshotAsync();
            await PrewarmRingPlayerAsync();

            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PositionTimerIntervalMs) };
            _positionTimer.Tick += PositionTimer_Tick;
            _positionTimer.Start();

            _volumeDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _volumeDebounceTimer.Tick += VolumeDebounceTimer_Tick;
            _volumeDebounceTimer.Start();

            _spotifyConnectTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _spotifyConnectTimer.Tick += SpotifyConnectTimer_Tick;
            _spotifyConnectTimer.Start();

            NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
            UpdateConnectivityState();
            UpdateSelectedSpotifyConnectDeviceState(null);
            _ = RefreshSpotifyConnectPlaybackAsync();

            await Task.CompletedTask;
        }

        private void PositionTimer_Tick(object sender, object e)
        {
            if (!IsSelectedSpotifyConnectDeviceLocal)
            {
                UpdateEstimatedRemotePosition();
                return;
            }

            if (_state.PlaybackState != LibrespotPlaybackState.Playing)
                return;

            ApplyPlaybackPosition(_librespot.GetPositionMs(), persistSnapshot: true);
        }

        private void SpotifyConnectTimer_Tick(object sender, object e)
        {
            _ = RefreshSpotifyConnectPlaybackAsync();
        }

        private void UpdateSmtcTimeline(uint positionMs)
        {
            if (_smtc == null)
                return;

            positionMs = ClampPlaybackPosition(positionMs);
            var durationMs = Math.Max(_state.DurationMs, positionMs);

            var timelineProperties = new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                MaxSeekTime = TimeSpan.FromMilliseconds(durationMs),
                EndTime = TimeSpan.FromMilliseconds(durationMs),
                Position = TimeSpan.FromMilliseconds(positionMs)
            };

            _smtc.UpdateTimelineProperties(timelineProperties);
        }

        private void ApplyPlaybackPosition(uint positionMs, bool persistSnapshot)
        {
            var clampedPosition = ClampPlaybackPosition(positionMs);
            var changed = false;

            UpdateState(s =>
            {
                if (s.PositionMs != clampedPosition)
                {
                    s.PositionMs = clampedPosition;
                    changed = true;
                }
            });

            if (!changed && !persistSnapshot)
                return;

            UpdateSmtcTimeline(clampedPosition);
            if (persistSnapshot && clampedPosition != _lastPersistedSnapshotPositionMs)
            {
                _lastPersistedSnapshotPositionMs = clampedPosition;
                PersistPlaybackSnapshot();
            }
        }

        private uint ClampPlaybackPosition(uint positionMs)
        {
            var durationMs = _state?.DurationMs ?? 0;
            return durationMs > 0 && positionMs > durationMs
                ? durationMs
                : positionMs;
        }

        private string LocalSpotifyConnectDeviceId => _librespot?.DeviceId ?? string.Empty;

        private bool IsSelectedSpotifyConnectDeviceLocal => IsLocalSpotifyConnectDeviceId(GetSelectedSpotifyConnectDeviceId());

        private string GetSelectedSpotifyConnectDeviceId()
        {
            var selected = UserSettings.SpotifyConnectDeviceId;
            return string.IsNullOrWhiteSpace(selected) ? LocalSpotifyConnectDeviceId : selected;
        }

        private bool IsLocalSpotifyConnectDeviceId(string deviceId)
        {
            return string.IsNullOrWhiteSpace(deviceId) ||
                string.Equals(deviceId, LocalSpotifyConnectDeviceId, StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateSelectedSpotifyConnectDeviceState(SpotifyConnectDeviceInfo device)
        {
            var selectedId = GetSelectedSpotifyConnectDeviceId();
            var isLocal = IsLocalSpotifyConnectDeviceId(selectedId);
            var name = device?.DisplayName ?? (isLocal ? "This device" : device?.Name);

            UpdateState(s =>
            {
                s.SpotifyConnectDeviceId = selectedId;
                s.SpotifyConnectDeviceName = string.IsNullOrWhiteSpace(name) ? "Spotify device" : name;
                s.IsSpotifyConnectRemote = !isLocal;
            });
        }

        private static bool IsSpotifyConnectDeviceNotFound(Exception ex)
        {
            if (ex is SpotifyWebException webEx && webEx.StatusCode == 404)
                return true;

            if (ex?.InnerException is APIException apiEx && (int?)apiEx.Response?.StatusCode == 404)
                return true;

            return ex?.Message?.IndexOf("Device not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                ex?.InnerException?.Message?.IndexOf("Device not found", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task<bool> TryRecoverMissingSpotifyConnectDeviceAsync(Exception ex, string operation)
        {
            if (!IsSpotifyConnectDeviceNotFound(ex))
                return false;

            var missingDeviceId = GetSelectedSpotifyConnectDeviceId();
            if (IsLocalSpotifyConnectDeviceId(missingDeviceId))
                return true;

            LogService.Warn($"[MediaService.{operation}] Spotify Connect device '{missingDeviceId}' is no longer available. Falling back to this device.");

            UserSettings.SpotifyConnectDeviceId = LocalSpotifyConnectDeviceId;
            _remotePositionUpdatedAt = DateTimeOffset.MinValue;

            UpdateSelectedSpotifyConnectDeviceState(new SpotifyConnectDeviceInfo
            {
                Id = LocalSpotifyConnectDeviceId,
                Name = "This device",
                Type = "Computer",
                IsThisDevice = true,
                SupportsVolume = true,
                VolumePercent = (int)Math.Round(Current.Volume * 100.0 / 65535.0)
            });

            UpdateState(s =>
            {
                s.StatusMessage = "Spotify Connect device is no longer available. Switched back to this device.";
                if (s.IsSpotifyConnectRemote)
                    s.PlaybackState = LibrespotPlaybackState.Paused;
            });

            try
            {
                await EnsureLocalLibrespotConnectedAsync(interactive: false).ConfigureAwait(false);
            }
            catch (Exception connectEx)
            {
                LogService.Warn($"[MediaService.{operation}] Unable to prepare local playback after Connect fallback: {connectEx.Message}");
            }

            return true;
        }

        public async Task<SpotifyConnectDeviceInfo[]> GetSpotifyConnectDevicesAsync()
        {
            var localId = LocalSpotifyConnectDeviceId;
            var local = new SpotifyConnectDeviceInfo
            {
                Id = localId,
                Name = "This device",
                Type = "Computer",
                IsThisDevice = true,
                SupportsVolume = true,
                VolumePercent = (int)Math.Round(Current.Volume * 100.0 / 65535.0),
                IsActive = IsSelectedSpotifyConnectDeviceLocal && Current.PlaybackState == LibrespotPlaybackState.Playing
            };

            var devices = new System.Collections.Generic.List<SpotifyConnectDeviceInfo> { local };

            if (!ConnectivityHelper.HasInternetAccess())
                return devices.ToArray();

            try
            {
                var response = await _web.GetAvailableDevicesAsync().ConfigureAwait(false);
                foreach (var device in response?.Devices ?? new System.Collections.Generic.List<Device>())
                {
                    if (string.IsNullOrWhiteSpace(device.Id))
                        continue;

                    if (string.Equals(device.Id, localId, StringComparison.OrdinalIgnoreCase))
                    {
                        local.IsActive = device.IsActive;
                        local.IsRestricted = device.IsRestricted;
                        local.SupportsVolume = device.SupportsVolume;
                        local.VolumePercent = device.VolumePercent;
                        continue;
                    }

                    devices.Add(new SpotifyConnectDeviceInfo
                    {
                        Id = device.Id,
                        Name = device.Name,
                        Type = device.Type,
                        IsActive = device.IsActive,
                        IsRestricted = device.IsRestricted,
                        SupportsVolume = device.SupportsVolume,
                        VolumePercent = device.VolumePercent,
                        IsThisDevice = false
                    });
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MediaService.GetSpotifyConnectDevicesAsync] Unable to enumerate Spotify Connect devices: {ex.Message}");
            }

            return devices
                .GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(device => device.IsThisDevice)
                .ThenBy(device => device.Name)
                .ToArray();
        }

        public async Task SetSpotifyConnectDeviceAsync(string deviceId)
        {
            deviceId = string.IsNullOrWhiteSpace(deviceId) ? LocalSpotifyConnectDeviceId : deviceId;
            if (string.Equals(GetSelectedSpotifyConnectDeviceId(), deviceId, StringComparison.OrdinalIgnoreCase))
                return;

            var devices = await GetSpotifyConnectDevicesAsync().ConfigureAwait(false);
            var selected = devices.FirstOrDefault(device => string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            var wasPlaying = Current.PlaybackState == LibrespotPlaybackState.Playing;
            var hadPlayback = Current.Track != null || wasPlaying || Current.PlaybackState == LibrespotPlaybackState.Paused;
            var transferPositionMs = ClampPlaybackPosition(Current.PositionMs);
            UserSettings.SpotifyConnectDeviceId = deviceId;
            UpdateSelectedSpotifyConnectDeviceState(selected);

            if (!ConnectivityHelper.HasInternetAccess())
                return;

            try
            {
                if (IsLocalSpotifyConnectDeviceId(deviceId))
                    await EnsureLocalLibrespotConnectedAsync(interactive: false).ConfigureAwait(false);

                if (hadPlayback)
                {
                    await _web.TransferPlaybackAsync(deviceId, wasPlaying).ConfigureAwait(false);

                    if (!IsLocalSpotifyConnectDeviceId(deviceId) && transferPositionMs > 0)
                    {
                        try
                        {
                            await Task.Delay(250).ConfigureAwait(false);
                            await _web.SeekToAsync(deviceId, transferPositionMs).ConfigureAwait(false);
                        }
                        catch (Exception seekEx)
                        {
                            LogService.Warn($"[MediaService.SetSpotifyConnectDeviceAsync] Unable to restore Connect playback position: {seekEx.Message}");
                        }
                    }
                }

                if (!IsLocalSpotifyConnectDeviceId(deviceId))
                    await RefreshSpotifyConnectPlaybackAsync(force: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (await TryRecoverMissingSpotifyConnectDeviceAsync(ex, nameof(SetSpotifyConnectDeviceAsync)).ConfigureAwait(false))
                    return;

                LogService.Warn($"[MediaService.SetSpotifyConnectDeviceAsync] Unable to transfer playback to {selected?.Name ?? deviceId}: {ex.Message}");
            }
        }

        public async Task PlayAsync(string contextUri, string startUri = null)
        {
            CancelPlaybackContinuationWatchdog();
            await _playbackGate.WaitAsync();
            try
            {
                await PlayCoreAsync(contextUri, startUri);
            }
            finally
            {
                _playbackGate.Release();
            }
        }

        private async Task PlayCoreAsync(string contextUri, string startUri = null)
        {
            if (!IsSelectedSpotifyConnectDeviceLocal)
            {
                await PlayRemoteAsync(contextUri, startUri).ConfigureAwait(false);
                return;
            }

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
                if (isDirectTrack && !App.OfflineCatalog.IsTrackPersisted(directTrackUri))
                {
                    UpdateState(s =>
                    {
                        s.IsOffline = true;
                        s.StatusMessage = "Offline. This track has not finished downloading yet.";
                    });
                    return;
                }

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

            if (!await EnsureLocalLibrespotConnectedAsync(interactive: true, allowOfflineToken: isOffline).ConfigureAwait(false))
                return;

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
            _ringPlayer?.Stop();
            if (_mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                _mediaPlayer.Pause();
            ApplyPlaybackPosition(0, persistSnapshot: false);

            await _librespot.LoadAndPlayAsync(contextUri, startUri);

            await EnsureRingPlayerAsync();

            _ringPlayer.Start();
        }

        private async Task<bool> EnsureLocalLibrespotConnectedAsync(bool interactive, bool allowOfflineToken = false)
        {
            var isOffline = !ConnectivityHelper.HasInternetAccess();
            var librespotReady = (_librespot as LibrespotService)?.HasInstance == true;
            var requiresOnlineReconnect = !isOffline && !_librespot.Session.IsConnected;
            if (librespotReady && !requiresOnlineReconnect)
                return true;

            var accessToken = allowOfflineToken || isOffline
                ? await _auth.GetAccessToken().ConfigureAwait(false)
                : await _auth.EnsureValidAccessTokenAsync(interactive: interactive).ConfigureAwait(false);

            if (string.IsNullOrEmpty(accessToken) && !isOffline)
                return false;

            if (string.IsNullOrEmpty(accessToken) && isOffline)
            {
                UpdateState(s =>
                {
                    s.IsOffline = true;
                    s.StatusMessage = "Offline. Sign in once while online before cached playback can start.";
                });
                return false;
            }

            await _librespot.ConnectWithAccessTokenAsync(accessToken).ConfigureAwait(false);
            return true;
        }

        private async Task PlayRemoteAsync(string contextUri, string startUri)
        {
            if (!ConnectivityHelper.HasInternetAccess())
            {
                UpdateState(s =>
                {
                    s.IsOffline = true;
                    s.StatusMessage = "Connect playback needs an internet connection.";
                });
                return;
            }

            var deviceId = GetSelectedSpotifyConnectDeviceId();
            var playbackContextUri = GetPlaybackContextUri(contextUri, startUri);

            UpdateState(s =>
            {
                s.IsOffline = false;
                s.PlaybackState = LibrespotPlaybackState.Loading;
                s.ContextUri = playbackContextUri;
                s.StatusMessage = null;
            });

            try
            {
                await _web.ResumePlaybackAsync(deviceId, contextUri, startUri).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (await TryRecoverMissingSpotifyConnectDeviceAsync(ex, nameof(PlayRemoteAsync)).ConfigureAwait(false))
                {
                    await PlayCoreAsync(contextUri, startUri).ConfigureAwait(false);
                    return;
                }

                throw;
            }

            await RefreshSpotifyConnectPlaybackAsync(force: true).ConfigureAwait(false);
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
            CancelPlaybackContinuationWatchdog();
            if (!IsSelectedSpotifyConnectDeviceLocal)
            {
                try
                {
                    await _web.PausePlaybackAsync(GetSelectedSpotifyConnectDeviceId()).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (!await TryRecoverMissingSpotifyConnectDeviceAsync(ex, nameof(PauseAsync)).ConfigureAwait(false))
                        throw;
                }

                UpdateState(s => s.PlaybackState = LibrespotPlaybackState.Paused);
                await RefreshSpotifyConnectPlaybackAsync(force: true).ConfigureAwait(false);
                return;
            }

            await _librespot.PauseAsync();
        }

        public async Task ResumeAsync()
        {
            if (!IsSelectedSpotifyConnectDeviceLocal)
            {
                try
                {
                    await _web.ResumePlaybackAsync(GetSelectedSpotifyConnectDeviceId()).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (!await TryRecoverMissingSpotifyConnectDeviceAsync(ex, nameof(ResumeAsync)).ConfigureAwait(false))
                        throw;

                    if (!string.IsNullOrWhiteSpace(Current.Track?.Uri))
                    {
                        _pendingRestoreSeekMs = ClampPlaybackPosition(Current.PositionMs);
                        await PlayAsync(
                            string.IsNullOrWhiteSpace(Current.ContextUri) ? Current.Track.Uri : Current.ContextUri,
                            Current.Track.Uri).ConfigureAwait(false);
                    }

                    return;
                }

                UpdateState(s => s.PlaybackState = LibrespotPlaybackState.Playing);
                await RefreshSpotifyConnectPlaybackAsync(force: true).ConfigureAwait(false);
                return;
            }

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
            CancelPlaybackContinuationWatchdog();
            if (!IsSelectedSpotifyConnectDeviceLocal)
            {
                try
                {
                    await _web.PausePlaybackAsync(GetSelectedSpotifyConnectDeviceId()).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (!await TryRecoverMissingSpotifyConnectDeviceAsync(ex, nameof(StopAsync)).ConfigureAwait(false))
                        throw;
                }

                UpdateState(s => s.PlaybackState = LibrespotPlaybackState.Stopped);
                await RefreshSpotifyConnectPlaybackAsync(force: true).ConfigureAwait(false);
                return;
            }

            await _librespot.StopAsync();
        }

        private void VolumeDebounceTimer_Tick(object sender, object e)
        {
            if (!_volumeDirty)
                return;

            _volumeDirty = false;
            _ = SetVolumeAsync(_pendingVolume);
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
            if (!IsSelectedSpotifyConnectDeviceLocal)
            {
                try
                {
                    await _web.SetShuffleAsync(GetSelectedSpotifyConnectDeviceId(), enabled).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (!await TryRecoverMissingSpotifyConnectDeviceAsync(ex, nameof(SetShuffleAsync)).ConfigureAwait(false))
                        throw;

                    await _librespot.SetShuffleAsync(enabled).ConfigureAwait(false);
                }

                UpdateState(s => s.Shuffle = enabled);
                await RefreshSpotifyConnectPlaybackAsync(force: true).ConfigureAwait(false);
                return;
            }

            await _librespot.SetShuffleAsync(enabled);

            UpdateState(s => s.Shuffle = enabled);
        }

        public async Task SetRepeatAsync(int mode)
        {
            if (!IsSelectedSpotifyConnectDeviceLocal)
            {
                try
                {
                    await _web.SetRepeatAsync(GetSelectedSpotifyConnectDeviceId(), mode).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (!await TryRecoverMissingSpotifyConnectDeviceAsync(ex, nameof(SetRepeatAsync)).ConfigureAwait(false))
                        throw;

                    await _librespot.SetRepeatAsync((uint)mode).ConfigureAwait(false);
                }

                UpdateState(s => s.RepeatMode = mode);
                await RefreshSpotifyConnectPlaybackAsync(force: true).ConfigureAwait(false);
                return;
            }

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

        public async Task SetVolumeAsync(ushort v)
        {
            if (!IsSelectedSpotifyConnectDeviceLocal)
            {
                try
                {
                    await _web.SetVolumeAsync(GetSelectedSpotifyConnectDeviceId(), (int)Math.Round(v * 100.0 / 65535.0)).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    if (!await TryRecoverMissingSpotifyConnectDeviceAsync(ex, nameof(SetVolumeAsync)).ConfigureAwait(false))
                        throw;
                }
            }

            await _librespot.SetVolumeAsync(v).ConfigureAwait(false);
        }

        public Task SetAudioEffectsPresetAsync(string preset)
        {
            UserSettings.AudioEffectsPreset = string.IsNullOrWhiteSpace(preset) ? "None" : preset;
            _ringPlayer?.SetAudioEffectsPreset(UserSettings.AudioEffectsPreset);
            return Task.CompletedTask;
        }

        public EqualizerBandRange[] GetEqualizerBandRanges()
        {
            return _ringPlayer?.GetEqualizerBandRanges()
                ?? Enumerable.Range(0, 5)
                    .Select(_ => new EqualizerBandRange
                    {
                        MinimumGain = UserSettings.EqualizerMinGainDb,
                        MaximumGain = UserSettings.EqualizerMaxGainDb
                    })
                    .ToArray();
        }

        public async Task<AudioOutputDeviceInfo[]> GetAudioOutputDevicesAsync()
        {
            var items = new[]
            {
                new AudioOutputDeviceInfo
                {
                    Id = string.Empty,
                    Name = "System default",
                    IsDefault = true
                }
            }.ToList();

            try
            {
                var selector = MediaDevice.GetAudioRenderSelector();
                var devices = await DeviceInformation.FindAllAsync(selector);
                var defaultId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);

                foreach (var device in devices.OrderBy(device => device.Name))
                {
                    items.Add(new AudioOutputDeviceInfo
                    {
                        Id = device.Id,
                        Name = device.Name,
                        IsDefault = string.Equals(device.Id, defaultId, StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MediaService.GetAudioOutputDevicesAsync] Unable to enumerate audio output devices: {ex.Message}");
            }

            return items.ToArray();
        }

        public async Task SetAudioOutputDeviceAsync(string deviceId)
        {
            deviceId = deviceId ?? string.Empty;
            if (string.Equals(UserSettings.AudioOutputDeviceId, deviceId, StringComparison.Ordinal))
                return;

            UserSettings.AudioOutputDeviceId = deviceId;
            var wasPlaying = Current.PlaybackState == LibrespotPlaybackState.Playing;

            _ringPlayer?.Stop();
            _ringPlayer?.Dispose();
            _ringPlayer = null;

            if ((_librespot as LibrespotService)?.HasInstance != true)
                return;

            await EnsureRingPlayerAsync();
            if (wasPlaying)
            {
                if (_mediaPlayer.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
                    _mediaPlayer.Play();

                _ringPlayer.Start();
            }
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
            if (!IsSelectedSpotifyConnectDeviceLocal)
            {
                _ = SkipRemoteAsync(1);
                return;
            }

            var shouldContinuePlaying = Current.PlaybackState == LibrespotPlaybackState.Playing
                || Current.PlaybackState == LibrespotPlaybackState.Loading;

            if (TryPlayOfflineRelativeTrack(1))
            {
                SchedulePlaybackContinuationWatchdog(shouldContinuePlaying, "next", allowStopped: false);
                return;
            }

            _librespot.Next();
            SchedulePlaybackContinuationWatchdog(shouldContinuePlaying, "next", allowStopped: false);
        }

        public void Previous()
        {
            if (!IsSelectedSpotifyConnectDeviceLocal)
            {
                _ = SkipRemoteAsync(-1);
                return;
            }

            var shouldContinuePlaying = Current.PlaybackState == LibrespotPlaybackState.Playing
                || Current.PlaybackState == LibrespotPlaybackState.Loading;

            if (TryPlayOfflineRelativeTrack(-1))
            {
                SchedulePlaybackContinuationWatchdog(shouldContinuePlaying, "previous", allowStopped: false);
                return;
            }

            _librespot.Previous();
            SchedulePlaybackContinuationWatchdog(shouldContinuePlaying, "previous", allowStopped: false);
        }

        private void SchedulePlaybackContinuationWatchdog(bool shouldContinuePlaying, string reason, bool allowStopped)
        {
            var version = Interlocked.Increment(ref _playbackContinuationVersion);
            if (shouldContinuePlaying)
                _ = EnsurePlaybackContinuesAfterTransitionAsync(version, reason, allowStopped);
        }

        private void CancelPlaybackContinuationWatchdog()
        {
            Interlocked.Increment(ref _playbackContinuationVersion);
            Interlocked.Exchange(ref _pendingEndOfTrackContinuationVersion, 0);
        }

        private async Task EnsurePlaybackContinuesAfterTransitionAsync(int version, string reason, bool allowStopped)
        {
            await Task.Delay(1500);
            if (version != _playbackContinuationVersion)
                return;

            var state = Current.PlaybackState;
            if (state != LibrespotPlaybackState.Paused &&
                state != LibrespotPlaybackState.Loading &&
                (!allowStopped || state != LibrespotPlaybackState.Stopped))
                return;

            try
            {
                LogService.Warn($"[MediaService.EnsurePlaybackContinuesAfterTransitionAsync] Playback was {state} after {reason}; requesting resume.");
                await _librespot.ResumeAsync();
                await EnsureRingPlayerAsync();

                if (_mediaPlayer.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
                    _mediaPlayer.Play();

                _ringPlayer?.Start();
                _smtc.PlaybackStatus = MediaPlaybackStatus.Playing;
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MediaService.EnsurePlaybackContinuesAfterTransitionAsync] Unable to resume after {reason}: {ex.Message}");
            }
        }

        private void ArmEndOfTrackContinuation()
        {
            var version = Interlocked.Increment(ref _pendingEndOfTrackContinuationVersion);
            _ = ClearEndOfTrackContinuationIfUnusedAsync(version);
        }

        private bool TryConsumeEndOfTrackContinuation()
        {
            return Interlocked.Exchange(ref _pendingEndOfTrackContinuationVersion, 0) != 0;
        }

        private async Task ClearEndOfTrackContinuationIfUnusedAsync(int version)
        {
            await Task.Delay(10000);
            Interlocked.CompareExchange(ref _pendingEndOfTrackContinuationVersion, 0, version);
        }

        public void Seek(uint posMs)
        {
            if (!IsSelectedSpotifyConnectDeviceLocal)
            {
                _ = SeekRemoteAsync(posMs);
                return;
            }

            _librespot.Seek(posMs);
        }

        private async Task SkipRemoteAsync(int delta)
        {
            try
            {
                var deviceId = GetSelectedSpotifyConnectDeviceId();
                if (delta > 0)
                    await _web.SkipNextAsync(deviceId).ConfigureAwait(false);
                else
                    await _web.SkipPreviousAsync(deviceId).ConfigureAwait(false);

                await RefreshSpotifyConnectPlaybackAsync(force: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (await TryRecoverMissingSpotifyConnectDeviceAsync(ex, nameof(SkipRemoteAsync)).ConfigureAwait(false))
                    return;

                LogService.Warn($"[MediaService.SkipRemoteAsync] Unable to skip remote playback: {ex.Message}");
            }
        }

        private async Task SeekRemoteAsync(uint posMs)
        {
            try
            {
                await _web.SeekToAsync(GetSelectedSpotifyConnectDeviceId(), posMs).ConfigureAwait(false);
                ApplyPlaybackPosition(posMs, persistSnapshot: false);
                await RefreshSpotifyConnectPlaybackAsync(force: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (await TryRecoverMissingSpotifyConnectDeviceAsync(ex, nameof(SeekRemoteAsync)).ConfigureAwait(false))
                    return;

                LogService.Warn($"[MediaService.SeekRemoteAsync] Unable to seek remote playback: {ex.Message}");
            }
        }

        private async Task EnsureRingPlayerAsync()
        {
            if (_ringPlayer != null)
                return;

            var props = (_librespot as LibrespotService)?.EncodingProperties
                        ?? AudioEncodingProperties.CreatePcm(44100, 2, 16);

            _ringPlayer = new LibrespotRingBufferPlayer(props, UserSettings.AudioOutputDeviceId);
            await _ringPlayer.InitializeAsync();
            _ringPlayer.SetAudioEffectsPreset(UserSettings.AudioEffectsPreset);
        }

        private async Task PrewarmRingPlayerAsync()
        {
            if ((_librespot as LibrespotService)?.HasInstance != true)
                return;

            try
            {
                await EnsureRingPlayerAsync();
                _ringPlayer?.Stop();
                LogService.Info("[MediaService.PrewarmRingPlayerAsync] Ring buffer player initialized during startup.");
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MediaService.PrewarmRingPlayerAsync] Unable to prewarm ring buffer player: {ex.Message}");
            }
        }

        private async Task RefreshSpotifyConnectPlaybackAsync(bool force = false)
        {
            if (!ConnectivityHelper.HasInternetAccess())
                return;

            if (_refreshingSpotifyConnectPlayback && !force)
                return;

            _refreshingSpotifyConnectPlayback = true;
            try
            {
                var playback = await _web.GetCurrentPlaybackAsync().ConfigureAwait(false);
                ApplyRemotePlayback(playback);
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MediaService.RefreshSpotifyConnectPlaybackAsync] Unable to refresh Connect playback: {ex.Message}");
            }
            finally
            {
                _refreshingSpotifyConnectPlayback = false;
            }
        }

        private void ApplyRemotePlayback(CurrentlyPlayingContext playback)
        {
            var selectedId = GetSelectedSpotifyConnectDeviceId();
            var wasSelectedLocal = IsLocalSpotifyConnectDeviceId(selectedId);
            if (playback == null)
            {
                if (wasSelectedLocal)
                    return;

                UpdateState(s =>
                {
                    s.SpotifyConnectDeviceId = selectedId;
                    s.IsSpotifyConnectRemote = true;
                    s.PlaybackState = LibrespotPlaybackState.Stopped;
                });
                return;
            }

            var device = playback.Device;
            var deviceName = device?.Name;
            var activeDeviceId = device?.Id;
            var activeIsLocal = !string.IsNullOrWhiteSpace(activeDeviceId) && IsLocalSpotifyConnectDeviceId(activeDeviceId);
            if (!string.IsNullOrWhiteSpace(activeDeviceId))
            {
                selectedId = activeIsLocal ? LocalSpotifyConnectDeviceId : activeDeviceId;
                if (!string.Equals(GetSelectedSpotifyConnectDeviceId(), selectedId, StringComparison.OrdinalIgnoreCase))
                    UserSettings.SpotifyConnectDeviceId = selectedId;

                if (activeIsLocal)
                {
                    if (!wasSelectedLocal || Current.IsSpotifyConnectRemote)
                    {
                        UpdateSelectedSpotifyConnectDeviceState(new SpotifyConnectDeviceInfo
                        {
                            Id = LocalSpotifyConnectDeviceId,
                            Name = "This device",
                            Type = device?.Type ?? "Computer",
                            IsActive = device?.IsActive ?? true,
                            IsRestricted = device?.IsRestricted ?? false,
                            SupportsVolume = device?.SupportsVolume ?? true,
                            VolumePercent = device?.VolumePercent,
                            IsThisDevice = true
                        });
                    }
                    return;
                }
            }

            var trackInfo = CreateTrackInfo(playback.Item);
            var fullTrack = playback.Item as FullTrack;
            var progress = playback.ProgressMs < 0 ? 0 : (uint)playback.ProgressMs;

            _remotePositionBaseMs = progress;
            _remotePositionUpdatedAt = DateTimeOffset.UtcNow;

            UpdateState(s =>
            {
                s.SpotifyConnectDeviceId = selectedId;
                s.SpotifyConnectDeviceName = string.IsNullOrWhiteSpace(deviceName) ? s.SpotifyConnectDeviceName : deviceName;
                s.IsSpotifyConnectRemote = true;
                s.Track = trackInfo;
                s.Metadata = fullTrack;
                s.PlaybackState = playback.IsPlaying ? LibrespotPlaybackState.Playing : LibrespotPlaybackState.Paused;
                s.PositionMs = progress;
                s.DurationMs = trackInfo != null ? (uint)trackInfo.Duration.TotalMilliseconds : s.DurationMs;
                s.ContextUri = playback.Context?.Uri ?? s.ContextUri;
                s.ContextName = ResolveImmediateContextName(s.ContextUri, fullTrack);
                s.ArtworkUri = ResolveRemoteArtwork(playback.Item);
                s.IsOffline = false;
                s.IsTrackMetadataFromCache = false;
                s.IsCurrentTrackPersisted = trackInfo != null && App.OfflineCatalog.IsTrackPersisted(trackInfo.Uri);
                s.StatusMessage = null;
                s.Shuffle = playback.ShuffleState;
                s.RepeatMode = MapRepeatMode(playback.RepeatState);
                if (device?.VolumePercent.HasValue == true)
                    s.Volume = (ushort)Math.Max(0, Math.Min(65535, device.VolumePercent.Value * 65535 / 100));
            });

            UpdateSmtcDisplay();
            UpdateSmtcTimeline(progress);
            if (_smtc != null)
                _smtc.PlaybackStatus = playback.IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        }

        private void UpdateEstimatedRemotePosition()
        {
            if (_state.PlaybackState != LibrespotPlaybackState.Playing || _remotePositionUpdatedAt == DateTimeOffset.MinValue)
                return;

            var elapsed = DateTimeOffset.UtcNow - _remotePositionUpdatedAt;
            var estimated = _remotePositionBaseMs + (uint)Math.Max(0, elapsed.TotalMilliseconds);
            ApplyPlaybackPosition(estimated, persistSnapshot: false);
        }

        private static LibrespotTrackInfo CreateTrackInfo(IPlayableItem item)
        {
            var track = item as FullTrack;
            if (track != null)
            {
                return new LibrespotTrackInfo
                {
                    Uri = track.Uri,
                    Name = track.Name,
                    Artist = string.Join(", ", track.Artists?.Select(artist => artist.Name) ?? Enumerable.Empty<string>()),
                    Album = track.Album?.Name,
                    CoverUrl = track.Album?.Images?.FirstOrDefault()?.Url,
                    Duration = TimeSpan.FromMilliseconds(track.DurationMs)
                };
            }

            var episode = item as FullEpisode;
            if (episode != null)
            {
                return new LibrespotTrackInfo
                {
                    Uri = episode.Uri,
                    Name = episode.Name,
                    Artist = episode.Show?.Name,
                    Album = episode.Show?.Name,
                    CoverUrl = episode.Images?.FirstOrDefault()?.Url,
                    Duration = TimeSpan.FromMilliseconds(episode.DurationMs)
                };
            }

            return null;
        }

        private static string ResolveRemoteArtwork(IPlayableItem item)
        {
            var track = item as FullTrack;
            if (track != null)
                return track.Album?.Images?.FirstOrDefault()?.Url;

            var episode = item as FullEpisode;
            return episode?.Images?.FirstOrDefault()?.Url;
        }

        private static int MapRepeatMode(string repeatState)
        {
            if (string.Equals(repeatState, "context", StringComparison.OrdinalIgnoreCase))
                return 1;

            if (string.Equals(repeatState, "track", StringComparison.OrdinalIgnoreCase))
                return 2;

            return 0;
        }

        private async void OnTrackChanged(object sender, LibrespotTrackInfo track)
        {
            try
            {
                if (!IsSelectedSpotifyConnectDeviceLocal)
                    return;

                if (track == null)
                {
                    UpdateState(state =>
                    {
                        state.Track = null;
                        state.Metadata = null;
                        state.PositionMs = 0;
                        state.DurationMs = 0;
                        state.IsCurrentTrackPersisted = false;
                        state.ArtworkUri = null;
                    });

                    UpdateSmtcDisplay();
                    UpdateSmtcTimeline(0);
                    PersistPlaybackSnapshot(forceWrite: true);
                    return;
                }

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

                    if (_pendingRestoreSeekMs != uint.MaxValue &&
                        _lastPlaybackSnapshot != null &&
                        string.Equals(_lastPlaybackSnapshot.TrackUri, track.Uri, StringComparison.OrdinalIgnoreCase))
                    {
                        state.PositionMs = _lastPlaybackSnapshot.PositionMs;
                        state.DurationMs = _lastPlaybackSnapshot.DurationMs > 0 ? _lastPlaybackSnapshot.DurationMs : state.DurationMs;
                        if (!string.IsNullOrWhiteSpace(_lastPlaybackSnapshot.ArtworkUri))
                            state.ArtworkUri = _lastPlaybackSnapshot.ArtworkUri;
                    }
                });

                UpdateSmtcDisplay();
                PersistPlaybackSnapshot(forceWrite: true);

                await EnsureRingPlayerAsync();
                _ringPlayer?.PrepareForPlaybackStartLog(track.Uri);
                LogService.Info($"[MediaService.OnTrackChanged] Prepared first audio frame marker for {track.Uri}.");

                if (TryConsumeEndOfTrackContinuation())
                    SchedulePlaybackContinuationWatchdog(true, "end-of-track", allowStopped: true);

                if (_pendingRestoreSeekMs != uint.MaxValue &&
                    _lastPlaybackSnapshot != null &&
                    string.Equals(_lastPlaybackSnapshot.TrackUri, track.Uri, StringComparison.OrdinalIgnoreCase))
                {
                    var seekPosition = _pendingRestoreSeekMs;
                    _pendingRestoreSeekMs = uint.MaxValue;
                    if (seekPosition > 0)
                        _librespot.Seek(seekPosition);

                    ApplyPlaybackPosition(seekPosition, persistSnapshot: true);
                    PersistPlaybackSnapshot(forceWrite: true);
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
                if (!IsSelectedSpotifyConnectDeviceLocal)
                    return;

                var previousState = _state.PlaybackState;
                UpdateState(s => s.PlaybackState = state);

                uint currentPos = _librespot.GetPositionMs();
                ApplyPlaybackPosition(currentPos, persistSnapshot: state == LibrespotPlaybackState.Playing);

                switch (state)
                {
                    case LibrespotPlaybackState.Playing:
                        if (_pendingRestoreSeekMs != uint.MaxValue)
                        {
                            var seekPosition = _pendingRestoreSeekMs;
                            _pendingRestoreSeekMs = uint.MaxValue;
                            if (seekPosition > 0)
                                _librespot.Seek(seekPosition);

                            ApplyPlaybackPosition(seekPosition, persistSnapshot: true);
                            PersistPlaybackSnapshot(forceWrite: true);
                        }

                        await EnsureRingPlayerAsync();
                        if (previousState != LibrespotPlaybackState.Playing)
                        {
                            _ringPlayer.PrepareForPlaybackStartLog(_state.Track?.Uri);
                            LogService.Info($"[MediaService.OnPlaybackChanged] Playback entered Playing for {_state.Track?.Uri ?? "(unknown)"} at {currentPos}ms.");
                        }

                        if (_mediaPlayer.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
                            _mediaPlayer.Play();

                        _ringPlayer.Start();
                        LogService.Info($"[MediaService.OnPlaybackChanged] MediaPlayer and AudioGraph started for {_state.Track?.Uri ?? "(unknown)"}.");
                        _smtc.PlaybackStatus = MediaPlaybackStatus.Playing;
                        break;

                    case LibrespotPlaybackState.Paused:
                        _ringPlayer?.Stop();

                        if (_mediaPlayer.PlaybackSession.PlaybackState != MediaPlaybackState.Paused)
                            _mediaPlayer.Pause();

                        _smtc.PlaybackStatus = MediaPlaybackStatus.Paused;
                        PersistPlaybackSnapshot(forceWrite: true);
                        break;

                    case LibrespotPlaybackState.Stopped:
                        _ringPlayer?.Stop();
                        _mediaPlayer.Pause();
                        _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
                        PersistPlaybackSnapshot(forceWrite: true);
                        break;
                }
            }
            catch (Exception ex)
            {
                LogService.Error(ex, $"[MediaService.OnPlaybackChanged] Unhandled error while processing playback state {state}");
            }
        }

        private void OnPositionChanged(object sender, uint positionMs)
        {
            try
            {
                if (!IsSelectedSpotifyConnectDeviceLocal)
                    return;

                ApplyPlaybackPosition(positionMs, persistSnapshot: _state.PlaybackState == LibrespotPlaybackState.Playing);
            }
            catch (Exception ex)
            {
                LogService.Error(ex, $"[MediaService.OnPositionChanged] Unhandled error while processing position {positionMs}");
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
            if (!IsSelectedSpotifyConnectDeviceLocal)
                return;

            _pendingVolume = volume;
            UpdateState(s => s.Volume = volume);

            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            settings.Values[VolumeKey] = volume;
            PersistPlaybackSnapshot();
        }

        private void OnShuffleChanged(object sender, bool enabled)
        {
            if (!IsSelectedSpotifyConnectDeviceLocal)
                return;

            UpdateState(s => s.Shuffle = enabled);
        }

        private void OnRepeatChanged(object sender, uint mode)
        {
            if (!IsSelectedSpotifyConnectDeviceLocal)
                return;

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
            var shouldContinuePlaying = Current.PlaybackState == LibrespotPlaybackState.Playing
                || Current.PlaybackState == LibrespotPlaybackState.Loading;

            if (TryPlayOfflineRelativeTrack(1))
            {
                SchedulePlaybackContinuationWatchdog(shouldContinuePlaying, "offline end-of-track", allowStopped: true);
                return;
            }

            if (Current.IsOffline && _offlineQueue.Length > 0)
            {
                LogService.Info("[MediaService.OnEndOfTrack] Offline queue reached the end.");
                UpdateState(s =>
                {
                    s.StatusMessage = "Offline queue finished.";
                });
            }
            else if (shouldContinuePlaying)
            {
                ArmEndOfTrackContinuation();
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

        private void PersistPlaybackSnapshot(bool forceWrite = false)
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

                _lastPlaybackSnapshot = snapshot;

                var now = DateTimeOffset.UtcNow;
                if (!forceWrite && now - _lastSnapshotWriteAt < TimeSpan.FromMilliseconds(SnapshotWriteIntervalMs))
                    return;

                Windows.Storage.ApplicationData.Current.LocalSettings.Values[PlaybackSnapshotKey] =
                    JsonConvert.SerializeObject(snapshot);
                _lastSnapshotWriteAt = now;
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
                    if (App.OfflineCatalog != null)
                        await App.OfflineCatalog.RemoveExpiredPersistedTracksAsync();
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
