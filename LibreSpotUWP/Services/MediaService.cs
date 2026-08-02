using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using LibreSpotUWP.Exceptions;
using SpotifyAPI.Web;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
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
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using static LibreSpotUWP.Interop.Librespot;

namespace LibreSpotUWP.Services
{
    public sealed class MediaService : IMediaService, IDisposable
    {
        private readonly ILibrespotService _librespot;
        private readonly ISpotifyAuthService _auth;
        private readonly ISpotifyWebService _web;

        private readonly object _lock = new object();
        private readonly object _spotifyConnectRefreshScheduleLock = new object();
        private readonly SemaphoreSlim _playbackGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _ringPlayerGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _audioConfigurationGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _networkStatusGate = new SemaphoreSlim(1, 1);
        private readonly AsyncOperationOnce _initialization = new AsyncOperationOnce();
        private readonly RecoveryOperationGate _producerRecovery = new RecoveryOperationGate();
        private readonly ApplicationPlaybackQueue _applicationQueue = new ApplicationPlaybackQueue();
        private readonly SpotifyConnectTransferTracker _spotifyConnectTransfer =
            new SpotifyConnectTransferTracker(TimeSpan.FromSeconds(15));
        private readonly CancellationTokenSource _lifetimeCancellation = new CancellationTokenSource();
        private readonly Random _random = new Random();
        private readonly HashSet<string> _offlinePlaybackFailedTrackUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private enum PlaybackTransportMode
        {
            None,
            OnlineContext,
            OfflineContextSubstitute,
            OfflineRandomSubstitute,
            WaitingForOnline
        }

        private sealed class PlaybackIntent
        {
            public string ContextUri { get; set; }
            public string StartUri { get; set; }
            public string PlaybackContextUri { get; set; }
            public string ResumeContextUri { get; set; }
            public string ResumeStartUri { get; set; }
        }

        private MediaState _state = new MediaState();
        private MediaPlayer _mediaPlayer;
        private SystemMediaTransportControls _smtc;

        private ILibrespotAudioPlayer _ringPlayer;

        private DispatcherTimer _positionTimer;
        private readonly PlaybackPositionSynchronizer _positionSynchronizer = new PlaybackPositionSynchronizer();
        private DispatcherTimer _volumeDebounceTimer;
        private DispatcherTimer _spotifyConnectTimer;
        private int _pendingVolume;
        private int _volumeDirty;
        private int _volumeVersion;
        private int _volumeAppliedVersion;
        private int _volumeUpdateRunning;
        private bool _refreshingSpotifyConnectPlayback;
        private int _spotifyConnectRefreshFailureCount;
        private DateTimeOffset _nextSpotifyConnectRefreshAt = DateTimeOffset.MinValue;
        private bool _hasRemotePositionAnchor;
        private string[] _offlineQueue = Array.Empty<string>();
        private int _offlineQueueIndex = -1;
        private string _offlineQueueContextUri;
        private string _onlineQueueResumeContextUri;
        private string _onlineQueueResumeStartUri;
        private bool _currentOfflineFallbackIsRandom;
        private int _contextResolutionVersion;
        private int _trackChangeVersion;
        private int _metadataRefreshVersion;
        private int _offlineStopRecoveryVersion;
        private int _connectivityRecheckVersion;
        private int _offlineLoadVersion;
        private int _downloadedPlaybackRecoveryInProgress;
        private bool _waitingForOnlineQueueContinuation;
        private bool _onlineQueueRecoveryPending;
        private bool _onlineQueueRecoveryActive;
        private bool _onlineQueueRecoveryRetryScheduled;
        private int _onlineQueueRecoveryAttempt;
        private int _onlineQueueRecoveryVersion;
        private string _onlineQueueRecoveryContextUri;
        private string _onlineQueueRecoveryStartUri;
        private bool _librespotTransportUnhealthy;
        private int _producerRecoveryBlocked;
        private int _suspended;
        private int _disposed;
        private int _playbackRequested;
        private long _applicationQueueSessionGeneration;
        private bool _resumeAfterSuspension;
        private string _pendingOfflineLoadTrackUri;
        private PlaybackTransportMode _transportMode = PlaybackTransportMode.None;
        private PlaybackIntent _playbackIntent;
        private PlaybackSnapshot _lastPlaybackSnapshot;
        private bool _restoredSnapshotPendingResume;
        private uint _lastPersistedSnapshotPositionMs = uint.MaxValue;
        private uint _pendingRestoreSeekMs = uint.MaxValue;
        private string _pendingRestoreTrackUri;
        private DateTimeOffset _lastSnapshotWriteAt = DateTimeOffset.MinValue;
        private readonly LatestWorkCoalescer _snapshotWriteQueue = new LatestWorkCoalescer(
            action => { var ignored = Task.Run(action); });

        public MediaState Current => _state;
        public event EventHandler<MediaState> MediaStateChanged;

        private const string VolumeKey = "UserVolume";
        private const string PlaybackSnapshotKey = "LastPlaybackSnapshot";
        private const int PositionTimerIntervalMs = PlaybackPositionSynchronizer.UiUpdateIntervalMs;
        private const int SnapshotWriteIntervalMs = 5000;
        private const int MaxOnlineQueueRecoveryAttempts = 2;
        private const int MaxProducerRecoveryAttempts = 3;
        private static readonly TimeSpan OnlineQueueRecoveryWatchdogDelay = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan ProducerRecoveryVerificationTimeout = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan EndOfQueueGracePeriod = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan LocalSessionConnectTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan OfflineLoadStopValidationDelay = TimeSpan.FromMilliseconds(1200);
        private static readonly TimeSpan MediaFailureInternetBackoff = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan SpotifyConnectRefreshMaxFailureBackoff = TimeSpan.FromSeconds(30);
        private const string AppDataLocalUriPrefix = "ms-appdata:///local/";

        public string CurrentAudioOutputDeviceId => UserSettings.AudioOutputDeviceId;
        public AudioBackendKind CurrentAudioBackend => UserSettings.AudioBackend;
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

        public Task InitializeAsync()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(MediaService));

            return _initialization.Run(InitializeCoreAsync);
        }

        private async Task InitializeCoreAsync()
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
            _librespot.PlaybackEvent += OnPlaybackChanged;
            _librespot.PositionChanged += OnPositionChanged;
            _librespot.SessionStateChanged += OnSessionStateChanged;
            _librespot.VolumeChanged += OnVolumeChanged;
            _librespot.ShuffleChanged += OnShuffleChanged;
            _librespot.RepeatChanged += OnRepeatChanged;
            _librespot.EndOfTrack += OnEndOfTrack;
            _librespot.TimeToPreloadNextTrack += OnTimeToPreloadNextTrack;
            _librespot.TrackPreloading += OnTrackPreloading;
            _librespot.LogMessage += OnLibrespotLogMessage;

            _auth.AuthStateChanged += OnAuthChanged;

            _mediaPlayer.Source = CreateSilentMediaSource();
            await RestorePlaybackSnapshotAsync();
            _ = PrewarmRingPlayerAsync();

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
            ConnectivityHelper.InternetAccessFailureReported += OnInternetAccessFailureReported;
            ConnectivityHelper.ConnectivityStatusChanged += OnConnectivityStatusChanged;
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
            }
            else
            {
                var isPlaying = _state.PlaybackState == LibrespotPlaybackState.Playing;
                if (isPlaying)
                {
                    _positionSynchronizer.ObserveAuthoritative(
                        _librespot.GetPositionMs(),
                        PlaybackPositionOrigin.PeriodicPoll,
                        isPlaying: true);
                }

                PublishSynchronizedPosition(persistSnapshot: isPlaying);
            }

            FlushPositionCorrectionBurstSummary();
        }

        private void PublishSynchronizedPosition(bool persistSnapshot)
        {
            uint visiblePosition;
            if (_positionSynchronizer.TryGetVisiblePosition(
                _state.DurationMs,
                _state.PlaybackState == LibrespotPlaybackState.Playing,
                force: false,
                positionMs: out visiblePosition))
            {
                ApplyPlaybackPosition(visiblePosition, persistSnapshot);
            }
        }

        private void FlushPositionCorrectionBurstSummary()
        {
            PositionCorrectionBurstSummary summary;
            if (!_positionSynchronizer.TryTakeCorrectionBurstSummary(out summary))
                return;

            LogService.Info(
                $"[MediaService.PositionSync] Correction burst count={summary.Count}, " +
                $"durationMs={summary.DurationMs}, maxDriftMs={summary.MaximumDriftMs}, " +
                $"hardCorrections={summary.HardCorrections}, actualSeeks={summary.ActualSeeksIssued}.");
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

        private void SetPendingRestoreSeek(uint positionMs, string trackUri)
        {
            lock (_lock)
            {
                _pendingRestoreSeekMs = positionMs;
                _pendingRestoreTrackUri = trackUri;
            }
        }

        private bool HasPendingRestoreSeekFor(string trackUri)
        {
            lock (_lock)
            {
                return _pendingRestoreSeekMs != uint.MaxValue &&
                    (string.IsNullOrWhiteSpace(_pendingRestoreTrackUri) ||
                     string.Equals(_pendingRestoreTrackUri, trackUri, StringComparison.OrdinalIgnoreCase));
            }
        }

        private bool TryTakePendingRestoreSeek(string trackUri, out uint positionMs)
        {
            lock (_lock)
            {
                if (_pendingRestoreSeekMs == uint.MaxValue ||
                    (!string.IsNullOrWhiteSpace(_pendingRestoreTrackUri) &&
                     !string.Equals(_pendingRestoreTrackUri, trackUri, StringComparison.OrdinalIgnoreCase)))
                {
                    positionMs = 0;
                    return false;
                }

                positionMs = _pendingRestoreSeekMs;
                _pendingRestoreSeekMs = uint.MaxValue;
                _pendingRestoreTrackUri = null;
                return true;
            }
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

        private void AdoptLocalSpotifyConnectSession()
        {
            var localDeviceId = LocalSpotifyConnectDeviceId;
            if (string.IsNullOrWhiteSpace(localDeviceId))
                return;

            _spotifyConnectTransfer.Cancel();
            _hasRemotePositionAnchor = false;
            UserSettings.SpotifyConnectDeviceId = localDeviceId;
            UpdateSelectedSpotifyConnectDeviceState(new SpotifyConnectDeviceInfo
            {
                Id = localDeviceId,
                Name = "This device",
                Type = "Computer",
                IsActive = true,
                IsThisDevice = true,
                SupportsVolume = true,
                VolumePercent = (int)Math.Round(Current.Volume * 100.0 / 65535.0)
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

            _spotifyConnectTransfer.Cancel();
            UserSettings.SpotifyConnectDeviceId = LocalSpotifyConnectDeviceId;
            _hasRemotePositionAnchor = false;

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
            var previousDeviceId = GetSelectedSpotifyConnectDeviceId();
            if (string.Equals(previousDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                return;

            CancelPlaybackContinuationWatchdog();
            var devices = await GetSpotifyConnectDevicesAsync().ConfigureAwait(false);
            var selected = devices.FirstOrDefault(device => string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            var wasPlaying = Current.PlaybackState == LibrespotPlaybackState.Playing;
            var hadPlayback = Current.Track != null || wasPlaying || Current.PlaybackState == LibrespotPlaybackState.Paused;
            var hasInternetAccess = ConnectivityHelper.HasInternetAccess();
            if (hasInternetAccess && hadPlayback)
                _spotifyConnectTransfer.Begin(deviceId, DateTimeOffset.UtcNow);

            UserSettings.SpotifyConnectDeviceId = deviceId;
            UpdateSelectedSpotifyConnectDeviceState(selected);

            if (!hasInternetAccess)
                return;

            await _playbackGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (IsLocalSpotifyConnectDeviceId(deviceId))
                    await EnsureLocalLibrespotConnectedAsync(interactive: false).ConfigureAwait(false);

                if (hadPlayback)
                    await _web.TransferPlaybackAsync(deviceId, wasPlaying).ConfigureAwait(false);

                if (hadPlayback)
                    await RefreshSpotifyConnectPlaybackAsync(force: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _spotifyConnectTransfer.Cancel();
                if (await TryRecoverMissingSpotifyConnectDeviceAsync(ex, nameof(SetSpotifyConnectDeviceAsync)).ConfigureAwait(false))
                    return;

                UserSettings.SpotifyConnectDeviceId = previousDeviceId;
                UpdateSelectedSpotifyConnectDeviceState(
                    devices.FirstOrDefault(device => string.Equals(device.Id, previousDeviceId, StringComparison.OrdinalIgnoreCase)));
                LogService.Warn($"[MediaService.SetSpotifyConnectDeviceAsync] Unable to transfer playback to {selected?.Name ?? deviceId}: {ex.Message}");
            }
            finally
            {
                _playbackGate.Release();
            }
        }

        public Task PlayAsync(string contextUri, string startUri = null)
        {
            return PlayAsync(contextUri, startUri, null, -1);
        }

        public async Task PlayAsync(
            string contextUri,
            string startUri,
            IReadOnlyList<string> orderedTrackUris,
            int startIndex)
        {
            Volatile.Write(ref _playbackRequested, 1);
            CancelProducerRecovery(resetAttemptBudget: true);
            CancelPlaybackContinuationWatchdog();
            await _playbackGate.WaitAsync();
            try
            {
                // Any explicit selection supersedes the startup-only snapshot
                // resume path, even if the new native load has not reported its
                // TrackChanged marker yet.
                _restoredSnapshotPendingResume = false;

                var initialTrackUri = FirstNonBlank(
                    startUri,
                    IsSpotifyTrackUri(contextUri) ? contextUri : null,
                    orderedTrackUris?.FirstOrDefault());
                IReadOnlyList<string> queueUris = orderedTrackUris;
                if ((queueUris == null || queueUris.Count == 0) &&
                    !string.IsNullOrWhiteSpace(initialTrackUri))
                {
                    queueUris = new[] { initialTrackUri };
                    startIndex = 0;
                }

                var generation = _applicationQueue.Replace(
                    GetPlaybackContextUri(contextUri, startUri),
                    queueUris,
                    initialTrackUri,
                    startIndex,
                    Current.Shuffle,
                    Current.RepeatMode);
                LogService.Info($"[MediaService.PlayAsync] Application queue generation={generation}, context={contextUri ?? "(null)"}, tracks={queueUris?.Count ?? 0}, startIndex={_applicationQueue.Snapshot.CurrentIndex}.");
                await PlayCoreAsync(contextUri, startUri, orderedTrackUris: queueUris);
                ScheduleApplicationQueueHydration(
                    generation,
                    contextUri,
                    initialTrackUri,
                    startIndex,
                    queueUris?.Count ?? 0);
            }
            finally
            {
                _playbackGate.Release();
            }
        }

        private async Task PlayOnlineQueueRecoveryAsync(string contextUri, string startUri)
        {
            CancelPlaybackContinuationWatchdog();
            await _playbackGate.WaitAsync();
            try
            {
                await PlayCoreAsync(contextUri, startUri, isOnlineQueueRecovery: true);
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MediaService.PlayOnlineQueueRecoveryAsync] Online queue recovery failed: {ex.Message}");
                ReportMediaConnectivityFailure("online queue recovery load failed");
                ScheduleOnlineQueueRecoveryRetry("online queue recovery load failed");
            }
            finally
            {
                _playbackGate.Release();
            }
        }

        private void SetPlaybackIntent(string contextUri, string startUri, string playbackContextUri, string reason)
        {
            _playbackIntent = new PlaybackIntent
            {
                ContextUri = contextUri,
                StartUri = startUri,
                PlaybackContextUri = playbackContextUri,
                ResumeContextUri = playbackContextUri,
                ResumeStartUri = IsSpotifyTrackUri(startUri)
                    ? startUri
                    : IsSpotifyTrackUri(contextUri) ? contextUri : null
            };

            LogService.Info($"[MediaService.SetPlaybackIntent] context={contextUri ?? "(null)"}, start={startUri ?? "(null)"}, playbackContext={playbackContextUri ?? "(null)"}, reason={reason}.");
        }

        private void SetTransportMode(PlaybackTransportMode mode, string reason)
        {
            if (_transportMode == mode)
                return;

            LogService.Info($"[MediaService.SetTransportMode] {_transportMode} -> {mode}. reason={reason}");
            _transportMode = mode;
        }

        private async Task PlayCoreAsync(
            string contextUri,
            string startUri = null,
            bool isOnlineQueueRecovery = false,
            IReadOnlyList<string> orderedTrackUris = null)
        {
            _waitingForOnlineQueueContinuation = false;

            if (!IsSelectedSpotifyConnectDeviceLocal)
            {
                await PlayRemoteAsync(contextUri, startUri).ConfigureAwait(false);
                return;
            }

            if (!isOnlineQueueRecovery && ConnectivityHelper.HasNetworkReportedInternetAccess())
                ConnectivityHelper.ClearInternetAccessFailure(force: true);

            var isOffline = !ConnectivityHelper.HasInternetAccess();
            var wasOffline = Current.IsOffline;
            var originalContextUri = contextUri;
            var originalStartUri = startUri;
            var playbackContextUri = GetPlaybackContextUri(originalContextUri, startUri);
            var directTrackUri = !string.IsNullOrWhiteSpace(startUri) ? startUri : contextUri;
            var isDirectTrack = !string.IsNullOrWhiteSpace(directTrackUri) &&
                directTrackUri.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase);

            if (!isOnlineQueueRecovery)
                SetPlaybackIntent(originalContextUri, originalStartUri, playbackContextUri, "play request");

            if (isOnlineQueueRecovery && isOffline)
            {
                _onlineQueueRecoveryPending = HasOnlineQueueResumePoint();
                SetTransportMode(PlaybackTransportMode.WaitingForOnline, "online recovery requested while connectivity is unhealthy");
                ScheduleConnectivityRecheckAfterBackoff("online recovery requested while connectivity is unhealthy");
                UpdateState(s =>
                {
                    s.IsOffline = true;
                    s.StatusMessage = "Internet is not stable enough to resume the queue yet.";
                });
                return;
            }

            if (isOffline && string.IsNullOrWhiteSpace(startUri) && !isDirectTrack)
            {
                var offlineQueue = await GetKnownTrackUrisForPlaybackContextAsync(contextUri).ConfigureAwait(false);
                var firstPlayableIndex = FindPlayableOfflineQueueIndex(offlineQueue, -1, 1);
                if (firstPlayableIndex < 0)
                {
                    SetOnlineQueueResumePoint(playbackContextUri, offlineQueue, 0);
                    if (!IsPersistedOfflineContext(playbackContextUri) &&
                        await TryLoadRandomOfflineFallbackCoreAsync("selected context is unavailable offline").ConfigureAwait(false))
                    {
                        return;
                    }

                    SetTransportMode(PlaybackTransportMode.WaitingForOnline, "selected context unavailable offline");
                    UpdateState(s =>
                    {
                        s.IsOffline = true;
                        s.StatusMessage = IsPersistedOfflineContext(playbackContextUri)
                            ? "Offline. This downloaded album or playlist is not ready to continue yet."
                            : "Offline. This album or playlist has not been downloaded yet.";
                    });
                    return;
                }

                _offlineQueue = offlineQueue.ToArray();
                _offlineQueueIndex = firstPlayableIndex;
                _offlineQueueContextUri = playbackContextUri;
                _currentOfflineFallbackIsRandom = false;
                SetTransportMode(PlaybackTransportMode.OfflineContextSubstitute, "offline context playback");
                SetOnlineQueueResumePoint(_offlineQueueContextUri, _offlineQueue, _offlineQueueIndex + 1);
                contextUri = _offlineQueue[_offlineQueueIndex];
                startUri = null;
                LogService.Info($"[MediaService.PlayAsync] Offline context playback for {originalContextUri} starting at {_offlineQueue[_offlineQueueIndex]}. Queue size={_offlineQueue.Length}, index={_offlineQueueIndex}.");
            }
            else if (isOffline)
            {
                var queueSeed = await GetKnownTrackUrisForPlaybackContextAsync(originalContextUri).ConfigureAwait(false);
                if (queueSeed.Count == 0 && isDirectTrack)
                    queueSeed = new[] { directTrackUri };
                _offlineQueue = queueSeed.ToArray();
                _offlineQueueIndex = IndexOfTrackUri(_offlineQueue, directTrackUri);

                if (isDirectTrack && !IsDownloadedTrackPlayableOffline(directTrackUri))
                {
                    if (_offlineQueueIndex >= 0)
                        SetOnlineQueueResumePoint(playbackContextUri, _offlineQueue, _offlineQueueIndex);
                    else
                        SetOnlineQueueResumeTrack(playbackContextUri, directTrackUri);

                    if (!IsPersistedOfflineContext(playbackContextUri) &&
                        await TryLoadRandomOfflineFallbackCoreAsync("selected track is unavailable offline").ConfigureAwait(false))
                    {
                        return;
                    }

                    SetTransportMode(PlaybackTransportMode.WaitingForOnline, "selected track unavailable offline");
                    UpdateState(s =>
                    {
                        s.IsOffline = true;
                        s.StatusMessage = "Offline. This track has not finished downloading yet.";
                    });
                    return;
                }

                if (_offlineQueueIndex < 0 && isDirectTrack)
                {
                    _offlineQueue = new[] { directTrackUri };
                    _offlineQueueIndex = 0;
                    LogService.Warn($"[MediaService.PlayAsync] Offline direct track {directTrackUri} was not present in context {originalContextUri ?? "(null)"}; using a single-track offline queue.");
                }
                _offlineQueueContextUri = playbackContextUri;
                _currentOfflineFallbackIsRandom = false;
                SetTransportMode(PlaybackTransportMode.OfflineContextSubstitute, "offline direct playback");
                if (_offlineQueueIndex >= 0)
                    SetOnlineQueueResumePoint(_offlineQueueContextUri, _offlineQueue, _offlineQueueIndex + 1);
                LogService.Info($"[MediaService.PlayAsync] Offline direct playback for {directTrackUri}. Queue size={_offlineQueue.Length}, index={_offlineQueueIndex}.");
            }
            else
            {
                _offlineQueue = Array.Empty<string>();
                _offlineQueueIndex = -1;
                _offlineQueueContextUri = null;
                _currentOfflineFallbackIsRandom = false;
                SetTransportMode(PlaybackTransportMode.OnlineContext, isOnlineQueueRecovery ? "online queue recovery" : "online play request");
                if (!isOnlineQueueRecovery)
                    ClearOnlineQueueRecoveryState(clearResumePoint: true);
                _lastPersistedSnapshotPositionMs = uint.MaxValue;
            }

            if (isOffline && string.IsNullOrWhiteSpace(contextUri))
            {
                SetTransportMode(PlaybackTransportMode.WaitingForOnline, "offline playback has no playable context");
                UpdateState(s =>
                {
                    s.IsOffline = true;
                    s.StatusMessage = "Offline. Select a downloaded track to continue.";
                });
                return;
            }

            if (!isOffline && wasOffline && !isOnlineQueueRecovery)
                await StopOfflinePlaybackForOnlineTransitionAsync(clearRecoveryState: true);

            if (!await EnsureLocalLibrespotConnectedAsync(
                interactive: true,
                allowOfflineToken: isOffline,
                forceFreshOnlineSession: isOnlineQueueRecovery).ConfigureAwait(false))
            {
                return;
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
            await EnsureRingPlayerAsync();
            _ringPlayer.BeginTransition(
                isOnlineQueueRecovery ? "reconnect-load" : "load",
                Current.Track?.Uri,
                startUri ?? (IsSpotifyTrackUri(contextUri) ? contextUri : null),
                preserveCurrent: false,
                shouldPlay: true);
            UpdateState(s => s.PlaybackState = LibrespotPlaybackState.Loading);
            if (_mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                _mediaPlayer.Pause();
            ApplyPlaybackPosition(0, persistSnapshot: false);

            await _librespot.LoadAndPlayAsync(
                contextUri,
                startUri,
                isOffline ? null : orderedTrackUris);
        }

        private async Task<bool> EnsureLocalLibrespotConnectedAsync(
            bool interactive,
            bool allowOfflineToken = false,
            bool forceFreshOnlineSession = false)
        {
            var isOffline = !ConnectivityHelper.HasInternetAccess();
            var librespotReady = (_librespot as LibrespotService)?.HasInstance == true;
            var requiresOnlineReconnect = !isOffline && !_librespot.Session.IsConnected;
            if (librespotReady &&
                !requiresOnlineReconnect &&
                !forceFreshOnlineSession &&
                (isOffline || !_librespotTransportUnhealthy))
            {
                return true;
            }

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

            if ((forceFreshOnlineSession || _librespotTransportUnhealthy || requiresOnlineReconnect) && !isOffline)
            {
                _ringPlayer?.BeginTransition(
                    "reconnect",
                    Current.Track?.Uri,
                    Current.Track?.Uri,
                    preserveCurrent: false,
                    shouldPlay: Current.PlaybackState == LibrespotPlaybackState.Playing ||
                        Current.PlaybackState == LibrespotPlaybackState.Loading);
                await _librespot.ReconnectWithAccessTokenAsync(accessToken).ConfigureAwait(false);
                _librespotTransportUnhealthy = false;
            }
            else
            {
                await _librespot.ConnectWithAccessTokenAsync(accessToken).ConfigureAwait(false);
            }

            if (!isOffline && !await WaitForLocalLibrespotSessionConnectedAsync(
                forceFreshOnlineSession ? "fresh online session" : "online session").ConfigureAwait(false))
            {
                _librespotTransportUnhealthy = true;
                UpdateState(s =>
                {
                    s.IsOffline = false;
                    s.StatusMessage = "Spotify is still reconnecting. Try again in a moment.";
                });
                return false;
            }

            return true;
        }

        private async Task<bool> WaitForLocalLibrespotSessionConnectedAsync(string reason)
        {
            if (_librespot.Session.IsConnected)
                return true;

            var startedAt = DateTimeOffset.UtcNow;
            while (DateTimeOffset.UtcNow - startedAt < LocalSessionConnectTimeout)
            {
                await Task.Delay(100).ConfigureAwait(false);
                if (_librespot.Session.IsConnected)
                    return true;

                if (!ConnectivityHelper.HasNetworkReportedInternetAccess())
                    break;
            }

            LogService.Warn($"[MediaService.WaitForLocalLibrespotSessionConnectedAsync] Timed out waiting for local librespot session. reason={reason}");
            return _librespot.Session.IsConnected;
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

        private async Task StopOfflinePlaybackForOnlineTransitionAsync(bool clearRecoveryState)
        {
            LogService.Info("[MediaService.StopOfflinePlaybackForOnlineTransitionAsync] Stopping offline playback before switching back online.");

            _offlineQueue = Array.Empty<string>();
            _offlineQueueIndex = -1;
            _offlineQueueContextUri = null;
            _currentOfflineFallbackIsRandom = false;
            _pendingOfflineLoadTrackUri = null;
            Interlocked.Increment(ref _offlineLoadVersion);
            if (clearRecoveryState)
                ClearOnlineQueueRecoveryState(clearResumePoint: true);

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
            Volatile.Write(ref _playbackRequested, 0);
            CancelProducerRecovery(resetAttemptBudget: true);
            CancelPlaybackContinuationWatchdog();
            await _playbackGate.WaitAsync();
            try
            {
                await PauseCoreAsync();
            }
            finally
            {
                _playbackGate.Release();
            }
        }

        private void ScheduleApplicationQueueHydration(
            long queueGeneration,
            string contextUri,
            string startUri,
            int startIndex,
            int initialTrackCount)
        {
            if (string.IsNullOrWhiteSpace(contextUri) || IsSpotifyTrackUri(contextUri))
                return;

            _ = HydrateApplicationQueueAsync(
                queueGeneration,
                contextUri,
                startUri,
                startIndex,
                initialTrackCount);
        }

        private async Task HydrateApplicationQueueAsync(
            long queueGeneration,
            string contextUri,
            string startUri,
            int startIndex,
            int initialTrackCount)
        {
            try
            {
                // Give the native load/context resolver priority over metadata work.
                await Task.Delay(500, _lifetimeCancellation.Token).ConfigureAwait(false);
                var resolvedQueueUris = await GetKnownTrackUrisForPlaybackContextAsync(contextUri).ConfigureAwait(false);
                if (resolvedQueueUris == null || resolvedQueueUris.Count <= initialTrackCount)
                    return;

                if (_applicationQueue.TryHydrate(
                    queueGeneration,
                    resolvedQueueUris,
                    startUri,
                    startIndex))
                {
                    LogService.Info($"[MediaService.HydrateApplicationQueueAsync] Hydrated queue generation={queueGeneration}, context={contextUri}, tracks={resolvedQueueUris.Count}.");
                }
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MediaService.HydrateApplicationQueueAsync] Unable to hydrate {contextUri}: {ex.Message}");
            }
        }

        private async Task PauseCoreAsync()
        {
            CancelPlaybackContinuationWatchdog();
            ClearOnlineQueueRecoveryState(clearResumePoint: true);
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

            // Keep draining PCM until the native player confirms Paused. Gating
            // the ring first can fill the producer buffer and block the player
            // thread before it gets a chance to process this command.
            await _librespot.PauseAsync();
        }

        public async Task ResumeAsync()
        {
            Volatile.Write(ref _playbackRequested, 1);
            CancelProducerRecovery(resetAttemptBudget: true);
            await _playbackGate.WaitAsync();
            try
            {
                await ResumeCoreAsync();
            }
            finally
            {
                _playbackGate.Release();
            }
        }

        private async Task ResumeCoreAsync()
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
                        SetPendingRestoreSeek(
                            ClampPlaybackPosition(Current.PositionMs),
                            Current.Track.Uri);
                        await PlayCoreAsync(
                            string.IsNullOrWhiteSpace(Current.ContextUri) ? Current.Track.Uri : Current.ContextUri,
                            Current.Track.Uri).ConfigureAwait(false);
                    }

                    return;
                }

                UpdateState(s => s.PlaybackState = LibrespotPlaybackState.Playing);
                await RefreshSpotifyConnectPlaybackAsync(force: true).ConfigureAwait(false);
                return;
            }

            if (Current.PlaybackState == LibrespotPlaybackState.Playing ||
                _librespot.PlaybackState == LibrespotPlaybackState.Playing)
            {
                await EnsureRingPlayerAsync().ConfigureAwait(false);
                var activeTrack = Current.Track ?? _librespot.CurrentTrack;
                if (activeTrack?.AudioGeneration > 0 &&
                    await _ringPlayer.RequestPlaybackAsync(
                        activeTrack.PlayRequestId,
                        activeTrack.AudioGeneration).ConfigureAwait(false))
                {
                    UpdateState(s => s.PlaybackState = LibrespotPlaybackState.Playing);
                    if (_mediaPlayer.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
                        _mediaPlayer.Play();
                    _smtc.PlaybackStatus = MediaPlaybackStatus.Playing;
                    return;
                }
            }

            if (_restoredSnapshotPendingResume &&
                _lastPlaybackSnapshot != null &&
                _state.PlaybackState == LibrespotPlaybackState.Paused)
            {
                _restoredSnapshotPendingResume = false;
                SetPendingRestoreSeek(
                    _lastPlaybackSnapshot.PositionMs,
                    _lastPlaybackSnapshot.TrackUri);
                await PlayCoreAsync(
                    string.IsNullOrWhiteSpace(_lastPlaybackSnapshot.ContextUri) ? _lastPlaybackSnapshot.TrackUri : _lastPlaybackSnapshot.ContextUri,
                    _lastPlaybackSnapshot.TrackUri);

                return;
            }

            await _librespot.ResumeAsync();
        }

        public async Task StopAsync()
        {
            Volatile.Write(ref _playbackRequested, 0);
            CancelProducerRecovery(resetAttemptBudget: true);
            CancelPlaybackContinuationWatchdog();
            await _playbackGate.WaitAsync();
            try
            {
                await StopCoreAsync();
            }
            finally
            {
                _playbackGate.Release();
            }
        }

        private async Task StopCoreAsync()
        {
            CancelPlaybackContinuationWatchdog();
            ClearOnlineQueueRecoveryState(clearResumePoint: true);
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

            // Invalidate an in-flight load/seek immediately. Waiting for the
            // native Stopped event would leave delayed PCM able to satisfy the
            // pending pre-roll after the user has cancelled playback.
            _ringPlayer?.Stop();
            _mediaPlayer?.Pause();
            await _librespot.StopAsync();
        }

        public async Task PrepareForSuspendingAsync()
        {
            if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _suspended, 1) != 0)
                return;

            _resumeAfterSuspension = Current.PlaybackState == LibrespotPlaybackState.Playing ||
                Current.PlaybackState == LibrespotPlaybackState.Loading;
            CancelProducerRecovery(resetAttemptBudget: false);
            CancelPlaybackContinuationWatchdog();

            await _playbackGate.WaitAsync();
            try
            {
                if (_ringPlayer != null)
                    await _ringPlayer.PauseAsync();
                _mediaPlayer?.Pause();
                if (_resumeAfterSuspension && IsSelectedSpotifyConnectDeviceLocal)
                    await _librespot.PauseAsync();

                LogService.Info($"[MediaService.PrepareForSuspendingAsync] Audio processing suspended. graphId={_ringPlayer?.GraphInstanceId ?? 0}, sessionGeneration={_librespot.SessionGeneration}, resumeRequested={_resumeAfterSuspension}.");
            }
            finally
            {
                _playbackGate.Release();
            }
        }

        public async Task ResumeAfterSuspendingAsync()
        {
            if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _suspended, 0) == 0)
                return;

            bool shouldResume = _resumeAfterSuspension;
            _resumeAfterSuspension = false;
            Interlocked.Exchange(ref _producerRecoveryBlocked, 0);
            LogService.Info($"[MediaService.ResumeAfterSuspendingAsync] App resumed. graphId={_ringPlayer?.GraphInstanceId ?? 0}, sessionGeneration={_librespot.SessionGeneration}, resumeRequested={shouldResume}.");
            if (shouldResume)
                await ResumeAsync();
        }

        private void VolumeDebounceTimer_Tick(object sender, object e)
        {
            if (Volatile.Read(ref _volumeDirty) == 0)
                return;

            _ = ApplyPendingVolumeAsync();
        }

        public void SetVolumeDebounced(double percent)
        {
            var boundedPercent = Math.Max(0, Math.Min(100, percent));
            var raw = (ushort)Math.Round(boundedPercent * 65535 / 100);
            Volatile.Write(ref _pendingVolume, raw);
            Interlocked.Increment(ref _volumeVersion);
            Interlocked.Exchange(ref _volumeDirty, 1);

            // Preserve the locally-rendered slider value in future state
            // snapshots without broadcasting a full UI update for every pixel.
            // The debounced apply publishes one authoritative update.
            UpdateStateWithoutNotification(s => s.Volume = raw);
        }

        private async Task ApplyPendingVolumeAsync()
        {
            if (Interlocked.CompareExchange(ref _volumeUpdateRunning, 1, 0) != 0)
                return;

            try
            {
                while (Volatile.Read(ref _volumeDirty) != 0)
                {
                    Interlocked.Exchange(ref _volumeDirty, 0);
                    var version = Volatile.Read(ref _volumeVersion);
                    var volume = (ushort)Volatile.Read(ref _pendingVolume);

                    try
                    {
                        await SetVolumeAsync(volume).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogService.Warn($"[MediaService.ApplyPendingVolumeAsync] Unable to apply volume {volume}: {ex.Message}");
                        if (version == Volatile.Read(ref _volumeVersion))
                            Volatile.Write(ref _volumeAppliedVersion, version);
                        continue;
                    }

                    // A newer slider position arrived while the request was in
                    // flight. Let the loop apply that value and skip stale
                    // persistence/UI work for this one.
                    if (version != Volatile.Read(ref _volumeVersion))
                        continue;

                    Volatile.Write(ref _volumeAppliedVersion, version);
                    var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
                    settings.Values[VolumeKey] = volume;
                    UpdateState(s => s.Volume = volume);
                    PersistPlaybackSnapshot();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _volumeUpdateRunning, 0);
                if (Volatile.Read(ref _volumeDirty) != 0)
                    _ = ApplyPendingVolumeAsync();
            }
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
                _applicationQueue.UpdateShuffle(enabled);
                await RefreshSpotifyConnectPlaybackAsync(force: true).ConfigureAwait(false);
                return;
            }

            await _librespot.SetShuffleAsync(enabled);

            UpdateState(s => s.Shuffle = enabled);
            _applicationQueue.UpdateShuffle(enabled);
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
                _applicationQueue.UpdateRepeatMode(mode);
                await RefreshSpotifyConnectPlaybackAsync(force: true).ConfigureAwait(false);
                return;
            }

            await _librespot.SetRepeatAsync((uint)mode);

            UpdateState(s => s.RepeatMode = mode);
            _applicationQueue.UpdateRepeatMode(mode);
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
            NativeWindowsAudioPlayer.ApplyEffects();
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
            await _audioConfigurationGate.WaitAsync().ConfigureAwait(false);
            await _playbackGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (string.Equals(UserSettings.AudioOutputDeviceId, deviceId, StringComparison.Ordinal))
                    return;

                var previousDeviceId = UserSettings.AudioOutputDeviceId;
                var backend = UserSettings.AudioBackend;
                try
                {
                    if (backend == AudioBackendKind.RingBuffer)
                    {
                        // AudioGraph binds its render device during creation, so
                        // changing a Ring Buffer output requires a replacement graph.
                        await RecreateAudioPlayerAsync(
                            "output-device-change",
                            backend,
                            deviceId).ConfigureAwait(false);
                    }
                    else
                    {
                        // The Rust renderers monitor the native backend selection
                        // generation and can switch endpoints without replacing the
                        // managed player or seeking the active track.
                        await _ringPlayerGate.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            await NativeWindowsAudioPlayer.SelectBackendAsync(backend, deviceId).ConfigureAwait(false);
                            NativeWindowsAudioPlayer.ApplyEffects();
                            (_ringPlayer as NativeWindowsAudioPlayer)?.CommitOutputDevice(deviceId);
                        }
                        finally
                        {
                            _ringPlayerGate.Release();
                        }
                    }

                    // Commit only after the selected endpoint has initialized.
                    UserSettings.AudioOutputDeviceId = deviceId;
                    LogService.Info(
                        $"[MediaService.SetAudioOutputDeviceAsync] backend={backend}, " +
                        $"outputDevice={(deviceId.Length == 0 ? "default" : deviceId)}, " +
                        $"liveNativeSwitch={backend != AudioBackendKind.RingBuffer}.");
                }
                catch (Exception changeError)
                {
                    try
                    {
                        if (backend == AudioBackendKind.RingBuffer)
                        {
                            await RecreateAudioPlayerAsync(
                                "output-device-rollback",
                                backend,
                                previousDeviceId).ConfigureAwait(false);
                        }
                        else
                        {
                            await _ringPlayerGate.WaitAsync().ConfigureAwait(false);
                            try
                            {
                                await NativeWindowsAudioPlayer.SelectBackendAsync(backend, previousDeviceId).ConfigureAwait(false);
                                NativeWindowsAudioPlayer.ApplyEffects();
                            }
                            finally
                            {
                                _ringPlayerGate.Release();
                            }
                        }
                    }
                    catch (Exception rollbackError)
                    {
                        LogService.Error(rollbackError, "[MediaService.SetAudioOutputDeviceAsync] Audio output rollback failed");
                    }

                    UpdateState(state => state.StatusMessage = "Unable to change audio output. The previous output was restored.");
                    throw new InvalidOperationException("Unable to change the audio output device.", changeError);
                }
            }
            finally
            {
                _playbackGate.Release();
                _audioConfigurationGate.Release();
            }
        }

        public async Task SetAudioBackendAsync(AudioBackendKind backend)
        {
            if (!Enum.IsDefined(typeof(AudioBackendKind), backend))
                throw new ArgumentOutOfRangeException(nameof(backend));

            await _audioConfigurationGate.WaitAsync().ConfigureAwait(false);
            await _playbackGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (UserSettings.AudioBackend == backend)
                    return;

                var previousBackend = UserSettings.AudioBackend;
                var outputDeviceId = UserSettings.AudioOutputDeviceId;
                try
                {
                    await RecreateAudioPlayerAsync(
                        "audio-backend-change",
                        backend,
                        outputDeviceId).ConfigureAwait(false);
                    UserSettings.AudioBackend = backend;
                }
                catch (Exception changeError)
                {
                    try
                    {
                        await RecreateAudioPlayerAsync(
                            "audio-backend-rollback",
                            previousBackend,
                            outputDeviceId).ConfigureAwait(false);
                    }
                    catch (Exception rollbackError)
                    {
                        LogService.Error(rollbackError, "[MediaService.SetAudioBackendAsync] Audio backend rollback failed");
                    }

                    UpdateState(state => state.StatusMessage = "Unable to change the audio backend. The previous backend was restored.");
                    throw new InvalidOperationException("Unable to change the audio backend.", changeError);
                }
            }
            finally
            {
                _playbackGate.Release();
                _audioConfigurationGate.Release();
            }
        }

        public async Task RefreshCurrentTrackMetadataAsync()
        {
            var trackUri = Current?.Track?.Uri;
            if (string.IsNullOrWhiteSpace(trackUri) || !trackUri.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase))
                return;

            var version = Interlocked.Increment(ref _metadataRefreshVersion);
            var id = trackUri.Replace("spotify:track:", "");
            OfflineTrackEntry offlineTrack;
            CacheResponse<FullTrack> metadata;
            using (UiResponsivenessTelemetry.BeginOperation("Metadata.Refresh", version))
            {
                offlineTrack = await App.OfflineCatalog.GetDownloadedTrackAsync(trackUri);
                metadata = await _web.GetTrackAsync(id, true);
            }

            if (version != _metadataRefreshVersion ||
                !string.Equals(Current?.Track?.Uri, trackUri, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            UpdateState(state =>
            {
                state.Metadata = metadata?.Value;
                state.IsTrackMetadataFromCache = metadata?.IsFromCache == true;
                state.ArtworkUri = ResolveArtworkUri(metadata?.Value, state.Track, offlineTrack);
                state.StatusMessage = metadata?.IsFromCache == true
                    ? "Showing refreshed track details from cache."
                    : null;
            });

            UpdateSmtcDisplay();
        }
        public void Next()
        {
            CancelProducerRecovery(resetAttemptBudget: true);
            CancelPlaybackContinuationWatchdog();
            _ = RunSerializedPlaybackCommandAsync(NextCoreAsync);
        }

        private async Task NextCoreAsync()
        {
            if (!IsSelectedSpotifyConnectDeviceLocal)
            {
                _ = SkipRemoteAsync(1);
                return;
            }

            var shouldContinuePlaying = Current.PlaybackState == LibrespotPlaybackState.Playing
                || Current.PlaybackState == LibrespotPlaybackState.Loading;

            if (TryHandleLocalOfflineSkip(1, shouldContinuePlaying, "next"))
                return;

            var transition = _applicationQueue.BeginManualMove(
                1,
                Current.Track?.PlayRequestId ?? 0,
                _librespot.SessionGeneration,
                IsApplicationQueueTrackPlayable);
            if (await TryLoadManualApplicationQueueTargetAsync(
                transition,
                shouldContinuePlaying,
                "next").ConfigureAwait(false))
            {
                return;
            }

            _ringPlayer?.BeginTransition(
                "next",
                Current.Track?.Uri,
                null,
                preserveCurrent: false,
                shouldPlay: shouldContinuePlaying);
            UpdateState(s => s.PlaybackState = LibrespotPlaybackState.Loading);
            _librespot.Next();
        }

        public void Previous()
        {
            CancelProducerRecovery(resetAttemptBudget: true);
            CancelPlaybackContinuationWatchdog();
            _ = RunSerializedPlaybackCommandAsync(PreviousCoreAsync);
        }

        private async Task PreviousCoreAsync()
        {
            if (!IsSelectedSpotifyConnectDeviceLocal)
            {
                _ = SkipRemoteAsync(-1);
                return;
            }

            var shouldContinuePlaying = Current.PlaybackState == LibrespotPlaybackState.Playing
                || Current.PlaybackState == LibrespotPlaybackState.Loading;

            if (TryHandleLocalOfflineSkip(-1, shouldContinuePlaying, "previous"))
                return;

            var transition = _applicationQueue.BeginManualMove(
                -1,
                Current.Track?.PlayRequestId ?? 0,
                _librespot.SessionGeneration,
                IsApplicationQueueTrackPlayable);
            if (await TryLoadManualApplicationQueueTargetAsync(
                transition,
                shouldContinuePlaying,
                "previous").ConfigureAwait(false))
            {
                return;
            }

            _ringPlayer?.BeginTransition(
                "previous",
                Current.Track?.Uri,
                null,
                preserveCurrent: false,
                shouldPlay: shouldContinuePlaying);
            UpdateState(s => s.PlaybackState = LibrespotPlaybackState.Loading);
            _librespot.Previous();
        }

        private async Task<bool> TryLoadManualApplicationQueueTargetAsync(
            ApplicationQueueTransition transition,
            bool shouldContinuePlaying,
            string reason)
        {
            if (transition == null || !transition.IsValid || string.IsNullOrWhiteSpace(transition.ExpectedUri))
                return false;

            var snapshot = _applicationQueue.Snapshot;
            if (snapshot.TrackUris == null || snapshot.TrackUris.Count == 0)
                return false;

            LogService.Info($"[MediaService.TryLoadManualApplicationQueueTargetAsync] {reason} expected={transition.ExpectedUri}, queueGeneration={transition.QueueGenerationId}, transition={transition.TransitionId}.");
            await EnsureRingPlayerAsync().ConfigureAwait(false);
            _ringPlayer.BeginTransition(
                reason,
                Current.Track?.Uri,
                transition.ExpectedUri,
                preserveCurrent: false,
                shouldPlay: shouldContinuePlaying);
            UpdateState(state => state.PlaybackState = LibrespotPlaybackState.Loading);
            if (_mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                _mediaPlayer.Pause();
            ApplyPlaybackPosition(0, persistSnapshot: false);

            var contextUri = string.IsNullOrWhiteSpace(snapshot.ContextUri)
                ? transition.ExpectedUri
                : snapshot.ContextUri;
            await _librespot.LoadAndPlayAsync(
                contextUri,
                transition.ExpectedUri,
                snapshot.TrackUris,
                shouldContinuePlaying).ConfigureAwait(false);
            return true;
        }

        private async Task RunSerializedPlaybackCommandAsync(Func<Task> command)
        {
            await _playbackGate.WaitAsync();
            try
            {
                if (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _suspended) == 0)
                    await command().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogService.Error(ex, "[MediaService.RunSerializedPlaybackCommandAsync] Playback command failed");
            }
            finally
            {
                _playbackGate.Release();
            }
        }

        private bool TryHandleLocalOfflineSkip(int delta, bool shouldContinuePlaying, string reason)
        {
            var usingOfflineSubstitute = Current.IsOffline || IsOfflineSubstituteTransport() || !ConnectivityHelper.HasInternetAccess();
            if (!usingOfflineSubstitute)
                return false;

            if (!Current.IsOffline && HasOnlineQueueResumePoint())
            {
                _onlineQueueRecoveryPending = true;
                if (TryResumeOnlineQueueFromOfflineBoundary(shouldContinuePlaying))
                    return true;
            }

            if (TryPlayOfflineRelativeTrack(delta))
            {
                return true;
            }

            if (ShouldUseRandomOfflineFallback())
            {
                _ = TryPlayRandomOfflineFallbackAsync($"{reason} requested while no offline queue track is available");
                return true;
            }

            UpdateState(s =>
            {
                s.StatusMessage = HasOnlineQueueResumePoint()
                    ? "Offline. Waiting for internet before continuing the queue."
                    : "Offline. No downloaded song is available for that action.";
            });
            return true;
        }

        private void CancelPlaybackContinuationWatchdog()
        {
            LogApplicationQueueTransitionResult(_applicationQueue.CancelPendingTransition(
                "superseded-by-serialized-command",
                DateTimeOffset.UtcNow));
        }

        public void Seek(uint posMs)
        {
            CancelProducerRecovery(resetAttemptBudget: true);
            CancelPlaybackContinuationWatchdog();
            _ = SeekSerializedAsync(posMs);
        }

        private async Task SeekSerializedAsync(uint posMs)
        {
            await _playbackGate.WaitAsync();
            try
            {
                if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _suspended) != 0)
                    return;

                posMs = ClampPlaybackPosition(posMs);
                var request = _positionSynchronizer.BeginUserSeek(posMs);
                ApplyPlaybackPosition(posMs, persistSnapshot: false);

                if (!IsSelectedSpotifyConnectDeviceLocal)
                {
                    await SeekRemoteAsync(request).ConfigureAwait(false);
                    return;
                }

                var shouldContinuePlaying = Current.PlaybackState == LibrespotPlaybackState.Playing
                    || Current.PlaybackState == LibrespotPlaybackState.Loading;
                await SeekLocalAsync(request, shouldContinuePlaying).ConfigureAwait(false);
            }
            finally
            {
                _playbackGate.Release();
            }
        }

        private async Task SeekLocalAsync(PositionSeekRequest request, bool shouldContinuePlaying)
        {
            try
            {
                await Task.Run(() =>
                {
                    // Decoder seek and transition work never runs synchronously in
                    // the slider/UI event. A superseded origin token is discarded.
                    if (!_positionSynchronizer.TryRecordSeekIssued(request))
                        return;

                    _ringPlayer?.BeginTransition(
                        "seek",
                        Current.Track?.Uri,
                        Current.Track?.Uri,
                        preserveCurrent: false,
                        shouldPlay: shouldContinuePlaying);
                    UpdateState(s => s.PlaybackState = LibrespotPlaybackState.Loading);
                    _librespot.Seek(request.PositionMs);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogService.Error(ex, $"[MediaService.SeekLocalAsync] Unable to seek to {request?.PositionMs}ms");
            }
        }

        private async Task IssueSystemLibrespotSeekAsync(uint positionMs)
        {
            _positionSynchronizer.Reset(positionMs);
            _positionSynchronizer.RecordSystemSeekIssued();
            await Task.Run(() => _librespot.Seek(positionMs)).ConfigureAwait(false);
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

        private async Task SeekRemoteAsync(PositionSeekRequest request)
        {
            try
            {
                if (!_positionSynchronizer.TryRecordSeekIssued(request))
                    return;

                await _web.SeekToAsync(GetSelectedSpotifyConnectDeviceId(), request.PositionMs).ConfigureAwait(false);
                ApplyPlaybackPosition(request.PositionMs, persistSnapshot: false);
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
            await _ringPlayerGate.WaitAsync();
            try
            {
                if (_ringPlayer != null)
                {
                    await _ringPlayer.InitializeAsync();
                    _ringPlayer.SetSessionState(_librespot.Session.IsConnected, _librespot.SessionGeneration);
                    return;
                }

                _ringPlayer = await CreateAudioPlayerAsync(
                    UserSettings.AudioBackend,
                    UserSettings.AudioOutputDeviceId).ConfigureAwait(false);
            }
            finally
            {
                _ringPlayerGate.Release();
            }
        }

        private async Task<ILibrespotAudioPlayer> CreateAudioPlayerAsync(
            AudioBackendKind backend,
            string outputDeviceId)
        {
            ILibrespotAudioPlayer player;
            if (backend == AudioBackendKind.RingBuffer)
            {
                var props = (_librespot as LibrespotService)?.EncodingProperties
                            ?? AudioEncodingProperties.CreatePcm(44100, 2, 16);
                player = new LibrespotRingBufferPlayer(props, outputDeviceId);
            }
            else
            {
                player = new NativeWindowsAudioPlayer(backend, outputDeviceId);
            }

            try
            {
                await player.InitializeAsync().ConfigureAwait(false);
                player.SetAudioEffectsPreset(UserSettings.AudioEffectsPreset);
                player.SetSessionState(_librespot.Session.IsConnected, _librespot.SessionGeneration);
                player.ProducerStalled += OnProducerStalled;
                return player;
            }
            catch
            {
                await player.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        private async Task PrewarmRingPlayerAsync()
        {
            if ((_librespot as LibrespotService)?.HasInstance != true)
                return;

            var requestedBackend = UserSettings.AudioBackend;
            try
            {
                await EnsureRingPlayerAsync();
                _ringPlayer?.Stop();
                LogService.Info($"[MediaService.PrewarmRingPlayerAsync] Audio backend initialized during startup: {requestedBackend}.");
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MediaService.PrewarmRingPlayerAsync] Unable to prewarm audio backend {requestedBackend}: {ex.Message}");
                if (requestedBackend == AudioBackendKind.RustWasapi)
                    return;

                await _audioConfigurationGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (UserSettings.AudioBackend != requestedBackend)
                        return;

                    UserSettings.AudioBackend = AudioBackendKind.RustWasapi;
                    try
                    {
                        await EnsureRingPlayerAsync().ConfigureAwait(false);
                        _ringPlayer?.Stop();
                        LogService.Warn($"[MediaService.PrewarmRingPlayerAsync] Falling back from {requestedBackend} to RustWasapi.");
                    }
                    catch (Exception fallbackError)
                    {
                        LogService.Warn($"[MediaService.PrewarmRingPlayerAsync] WASAPI fallback failed: {fallbackError.Message}");
                    }
                }
                finally
                {
                    _audioConfigurationGate.Release();
                }
            }
        }

        private async Task RecreateAudioPlayerAsync(
            string reason,
            AudioBackendKind backend,
            string outputDeviceId)
        {
            var wasPlaying =
                Volatile.Read(ref _playbackRequested) != 0 ||
                Current.PlaybackState == LibrespotPlaybackState.Playing ||
                _librespot.PlaybackState == LibrespotPlaybackState.Playing;
            var position = Current.PositionMs;

            // The Rust call probes the complete replacement graph before it
            // publishes the selection. A rejected backend therefore leaves
            // the current renderer and managed controller untouched.
            await NativeWindowsAudioPlayer.SelectBackendAsync(
                backend,
                outputDeviceId).ConfigureAwait(false);
            NativeWindowsAudioPlayer.ApplyEffects();

            if ((_librespot as LibrespotService)?.HasInstance != true)
                return;

            ILibrespotAudioPlayer previousPlayer = null;
            ILibrespotAudioPlayer replacementPlayer;
            await _ringPlayerGate.WaitAsync().ConfigureAwait(false);
            try
            {
                replacementPlayer = await CreateAudioPlayerAsync(backend, outputDeviceId).ConfigureAwait(false);
                previousPlayer = _ringPlayer;
                _ringPlayer = replacementPlayer;
                if (previousPlayer != null)
                    previousPlayer.ProducerStalled -= OnProducerStalled;
            }
            finally
            {
                _ringPlayerGate.Release();
            }

            if (previousPlayer != null)
            {
                try
                {
                    previousPlayer.Stop();
                    await previousPlayer.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogService.Warn($"[MediaService.RecreateAudioPlayerAsync] Previous audio player cleanup failed after replacement: {ex.Message}");
                }
            }

            if (wasPlaying)
            {
                replacementPlayer.BeginTransition(
                    reason,
                    Current.Track?.Uri,
                    Current.Track?.Uri,
                    preserveCurrent: false,
                    shouldPlay: true);
                await IssueSystemLibrespotSeekAsync(position).ConfigureAwait(false);
            }

            LogService.Info($"[MediaService.RecreateAudioPlayerAsync] backend={backend}, reason={reason}, resumed={wasPlaying}.");
        }

        private async Task RefreshSpotifyConnectPlaybackAsync(bool force = false)
        {
            if (!ConnectivityHelper.HasInternetAccess())
                return;

            if (!CanRefreshSpotifyConnectPlayback(force))
                return;

            if (_refreshingSpotifyConnectPlayback && !force)
                return;

            _refreshingSpotifyConnectPlayback = true;
            try
            {
                var playback = await _web.GetCurrentPlaybackAsync().ConfigureAwait(false);
                ResetSpotifyConnectRefreshFailureBackoff();
                ApplyRemotePlayback(playback);
            }
            catch (Exception ex)
            {
                var retryDelay = DeferSpotifyConnectRefreshAfterFailure(force);
                if (force)
                    LogService.Warn($"[MediaService.RefreshSpotifyConnectPlaybackAsync] Unable to refresh Connect playback: {ex.Message}");
                else
                    LogService.Warn($"[MediaService.RefreshSpotifyConnectPlaybackAsync] Unable to refresh Connect playback, retrying in {retryDelay.TotalSeconds:F0}s: {ex.Message}");
            }
            finally
            {
                _refreshingSpotifyConnectPlayback = false;
            }
        }

        private bool CanRefreshSpotifyConnectPlayback(bool force)
        {
            if (force)
                return true;

            lock (_spotifyConnectRefreshScheduleLock)
            {
                return DateTimeOffset.UtcNow >= _nextSpotifyConnectRefreshAt;
            }
        }

        private void ResetSpotifyConnectRefreshFailureBackoff()
        {
            lock (_spotifyConnectRefreshScheduleLock)
            {
                _spotifyConnectRefreshFailureCount = 0;
                _nextSpotifyConnectRefreshAt = DateTimeOffset.MinValue;
            }
        }

        private TimeSpan DeferSpotifyConnectRefreshAfterFailure(bool force)
        {
            if (force)
                return TimeSpan.Zero;

            lock (_spotifyConnectRefreshScheduleLock)
            {
                _spotifyConnectRefreshFailureCount = Math.Min(_spotifyConnectRefreshFailureCount + 1, 6);
                var delaySeconds = Math.Min(
                    SpotifyConnectRefreshMaxFailureBackoff.TotalSeconds,
                    5 * _spotifyConnectRefreshFailureCount);
                var delay = TimeSpan.FromSeconds(delaySeconds);
                _nextSpotifyConnectRefreshAt = DateTimeOffset.UtcNow.Add(delay);
                return delay;
            }
        }

        private void ApplyRemotePlayback(CurrentlyPlayingContext playback)
        {
            var transferObservation = _spotifyConnectTransfer.Observe(
                playback?.Device?.Id,
                DateTimeOffset.UtcNow);
            if (transferObservation == SpotifyConnectTransferObservation.IgnoreStaleSnapshot)
            {
                LogService.Info("[MediaService.ApplyRemotePlayback] Ignoring stale playback snapshot while a Connect transfer is pending.");
                return;
            }

            if (transferObservation == SpotifyConnectTransferObservation.Confirmed)
                LogService.Info("[MediaService.ApplyRemotePlayback] Connect transfer confirmed by the active device snapshot.");
            else if (transferObservation == SpotifyConnectTransferObservation.Expired)
                LogService.Warn("[MediaService.ApplyRemotePlayback] Connect transfer confirmation timed out; accepting the current active device snapshot.");

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
            var remoteTrackChanged = !string.Equals(
                Current.Track?.Uri,
                trackInfo?.Uri,
                StringComparison.OrdinalIgnoreCase);

            _hasRemotePositionAnchor = true;

            if (remoteTrackChanged)
                _positionSynchronizer.Reset(progress);
            else
                _positionSynchronizer.ObserveAuthoritative(
                    progress,
                    PlaybackPositionOrigin.RemoteConnect,
                    playback.IsPlaying);

            var synchronizedProgress = _positionSynchronizer.GetEstimatedPosition(
                trackInfo != null ? (uint)trackInfo.Duration.TotalMilliseconds : Current.DurationMs,
                playback.IsPlaying);

            UpdateState(s =>
            {
                s.SpotifyConnectDeviceId = selectedId;
                s.SpotifyConnectDeviceName = string.IsNullOrWhiteSpace(deviceName) ? s.SpotifyConnectDeviceName : deviceName;
                s.IsSpotifyConnectRemote = true;
                s.Track = trackInfo;
                s.Metadata = fullTrack;
                s.PlaybackState = playback.IsPlaying ? LibrespotPlaybackState.Playing : LibrespotPlaybackState.Paused;
                s.PositionMs = synchronizedProgress;
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
            UpdateSmtcTimeline(synchronizedProgress);
            if (_smtc != null)
                _smtc.PlaybackStatus = playback.IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        }

        private void UpdateEstimatedRemotePosition()
        {
            if (_state.PlaybackState != LibrespotPlaybackState.Playing || !_hasRemotePositionAnchor)
                return;

            // Local progression comes from Stopwatch through the synchronizer;
            // wall-clock changes must not move a remote Connect position.
            PublishSynchronizedPosition(persistSnapshot: false);
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

                if (track != null && track.SessionGeneration != _librespot.SessionGeneration)
                {
                    LogService.Info($"[MediaService.OnTrackChanged] Ignoring stale session event. eventSessionGeneration={track.SessionGeneration}, activeSessionGeneration={_librespot.SessionGeneration}.");
                    return;
                }

                var changeVersion = Interlocked.Increment(ref _trackChangeVersion);
                if (track == null)
                {
                    _positionSynchronizer.Reset(0);
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

                await EnsureRingPlayerAsync();
                if (!_ringPlayer.ObserveTrackChanged(
                    track.Uri,
                    track.PlayRequestId,
                    track.AudioGeneration,
                    track.WasPreloaded))
                {
                    LogService.Info($"[MediaService.OnTrackChanged] Ignoring stale track event uri={track.Uri}, generation={track.AudioGeneration}, request={track.PlayRequestId}.");
                    return;
                }

                _restoredSnapshotPendingResume = false;

                var transitionResult = _applicationQueue.ObserveTrackChanged(
                    track.Uri,
                    track.PlayRequestId,
                    track.SessionGeneration,
                    DateTimeOffset.UtcNow);
                LogApplicationQueueTransitionResult(transitionResult);
                var queueSnapshot = _applicationQueue.Snapshot;
                if (_offlineQueue.Length > 0 && queueSnapshot.CurrentIndex >= 0)
                {
                    _offlineQueueIndex = queueSnapshot.CurrentIndex;
                    SetOnlineQueueResumePoint(
                        FirstNonBlank(_offlineQueueContextUri, queueSnapshot.ContextUri),
                        _offlineQueue,
                        _offlineQueueIndex + 1);
                }

                UpdateState(state =>
                {
                    state.Track = track;
                    state.Metadata = null;
                    state.DurationMs = (uint)track.Duration.TotalMilliseconds;
                    state.IsTrackMetadataFromCache = false;
                    state.IsCurrentTrackPersisted = App.OfflineCatalog.IsTrackPersisted(track.Uri);
                    state.IsOffline = !ConnectivityHelper.HasInternetAccess();
                    if (!state.IsOffline)
                        state.StatusMessage = null;
                    state.ArtworkUri = ResolveArtworkUri(null, track, null);

                    if (string.IsNullOrWhiteSpace(state.ContextUri))
                        state.ContextUri = track.Uri;

                    if (string.IsNullOrWhiteSpace(state.ContextName))
                        state.ContextName = ResolveImmediateContextName(state.ContextUri, null);

                    if (HasPendingRestoreSeekFor(track.Uri) &&
                        _lastPlaybackSnapshot != null &&
                        string.Equals(_lastPlaybackSnapshot.TrackUri, track.Uri, StringComparison.OrdinalIgnoreCase))
                    {
                        state.PositionMs = _lastPlaybackSnapshot.PositionMs;
                        state.DurationMs = _lastPlaybackSnapshot.DurationMs > 0 ? _lastPlaybackSnapshot.DurationMs : state.DurationMs;
                        if (string.IsNullOrWhiteSpace(state.ArtworkUri) &&
                            !string.IsNullOrWhiteSpace(_lastPlaybackSnapshot.ArtworkUri))
                        {
                            state.ArtworkUri = _lastPlaybackSnapshot.ArtworkUri;
                        }
                    }
                });

                _positionSynchronizer.Reset(Current.PositionMs);

                UpdateSmtcDisplay();
                PersistPlaybackSnapshot(forceWrite: true);

                LogService.Info($"[MediaService.OnTrackChanged] Accepted track marker uri={track.Uri}, generation={track.AudioGeneration}, request={track.PlayRequestId}, preloaded={track.WasPreloaded}.");

                uint seekPosition;
                if (TryTakePendingRestoreSeek(track.Uri, out seekPosition))
                {
                    if (seekPosition > 0)
                    {
                        _ringPlayer.BeginTransition(
                            "restore-seek",
                            track.Uri,
                            track.Uri,
                            preserveCurrent: false,
                            shouldPlay: true);
                        await IssueSystemLibrespotSeekAsync(seekPosition).ConfigureAwait(false);
                    }

                    ApplyPlaybackPosition(seekPosition, persistSnapshot: true);
                    PersistPlaybackSnapshot(forceWrite: true);
                }

                TryCompleteOnlineQueueRecovery(track.Uri);

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

                if (changeVersion != _trackChangeVersion ||
                    !string.Equals(Current.Track?.Uri, track.Uri, StringComparison.OrdinalIgnoreCase))
                {
                    LogService.Info($"[MediaService.OnTrackChanged] Ignoring stale metadata update for {track.Uri}.");
                    return;
                }

                UpdateState(state =>
                {
                    state.Metadata = metadata;
                    state.IsTrackMetadataFromCache = trackResponse?.IsFromCache == true;
                    state.IsCurrentTrackPersisted = App.OfflineCatalog.IsTrackPersisted(track.Uri);
                    state.StatusMessage = BuildPlaybackStatusMessage(trackResponse) ?? state.StatusMessage;
                    state.ArtworkUri = ResolveArtworkUri(metadata, track, offlineTrack);

                    if (string.IsNullOrWhiteSpace(state.ContextUri))
                        state.ContextUri = track.Uri;

                    if (string.IsNullOrWhiteSpace(state.ContextName))
                        state.ContextName = ResolveImmediateContextName(state.ContextUri, null);
                });

                UpdateSmtcDisplay();
                PersistPlaybackSnapshot(forceWrite: true);
            }
            catch (Exception ex)
            {
                LogService.Error(ex, $"[MediaService.OnTrackChanged] Unhandled error while processing track change for {track?.Uri ?? "(null)"}");
            }
        }

        private async void OnPlaybackChanged(object sender, LibrespotPlaybackEvent playbackEvent)
        {
            try
            {
                if (!IsSelectedSpotifyConnectDeviceLocal || playbackEvent == null)
                    return;

                if (playbackEvent.SessionGeneration != _librespot.SessionGeneration)
                {
                    LogService.Info($"[MediaService.OnPlaybackChanged] Ignoring stale session event. eventSessionGeneration={playbackEvent.SessionGeneration}, activeSessionGeneration={_librespot.SessionGeneration}.");
                    return;
                }

                if (playbackEvent.IsAudioKeyUnavailable)
                {
                    HandleAudioKeyUnavailable(playbackEvent);
                    return;
                }

                if (playbackEvent.IsUnavailable &&
                    await TryHandleUnavailableApplicationQueueTrackAsync(playbackEvent).ConfigureAwait(false))
                {
                    return;
                }

                var state = playbackEvent.State;
                var previousState = _state.PlaybackState;
                uint currentPos = playbackEvent.PositionMs;

                if (playbackEvent.IsSeek)
                {
                    await EnsureRingPlayerAsync();
                    if (!_ringPlayer.ObserveSeek(playbackEvent.PlayRequestId, playbackEvent.AudioGeneration))
                    {
                        LogService.Info($"[MediaService.OnPlaybackChanged] Ignoring stale seek generation={playbackEvent.AudioGeneration}, request={playbackEvent.PlayRequestId}.");
                        return;
                    }

                    ApplyPlaybackPosition(currentPos, persistSnapshot: true);
                    if (state == LibrespotPlaybackState.Playing &&
                        await _ringPlayer.RequestPlaybackAsync(playbackEvent.PlayRequestId, playbackEvent.AudioGeneration))
                    {
                        UpdateState(s => s.PlaybackState = LibrespotPlaybackState.Playing);
                        if (_mediaPlayer.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
                            _mediaPlayer.Play();
                        _smtc.PlaybackStatus = MediaPlaybackStatus.Playing;
                    }
                    return;
                }

                switch (state)
                {
                    case LibrespotPlaybackState.Loading:
                        await EnsureRingPlayerAsync();
                        if (!_ringPlayer.ObserveLoading(playbackEvent.PlayRequestId))
                        {
                            LogService.Info($"[MediaService.OnPlaybackChanged] Ignoring stale Loading request={playbackEvent.PlayRequestId}.");
                            return;
                        }
                        _positionSynchronizer.ObserveAuthoritative(
                            currentPos,
                            PlaybackPositionOrigin.StateTransition,
                            isPlaying: false);
                        UpdateState(s => s.PlaybackState = LibrespotPlaybackState.Loading);
                        ApplyPlaybackPosition(currentPos, persistSnapshot: false);
                        break;

                    case LibrespotPlaybackState.Playing:
                        Volatile.Write(ref _playbackRequested, 1);
                        _librespotTransportUnhealthy = false;
                        if (!string.IsNullOrWhiteSpace(_pendingOfflineLoadTrackUri))
                        {
                            _pendingOfflineLoadTrackUri = null;
                            Interlocked.Increment(ref _offlineLoadVersion);
                        }
                        uint seekPosition;
                        if (TryTakePendingRestoreSeek(
                            FirstNonBlank(playbackEvent.TrackUri, _state.Track?.Uri),
                            out seekPosition))
                        {
                            if (seekPosition > 0)
                            {
                                await EnsureRingPlayerAsync();
                                _ringPlayer.BeginTransition(
                                    "restore-seek",
                                    _state.Track?.Uri,
                                    _state.Track?.Uri,
                                    preserveCurrent: false,
                                    shouldPlay: true);
                                await IssueSystemLibrespotSeekAsync(seekPosition).ConfigureAwait(false);
                            }

                            ApplyPlaybackPosition(seekPosition, persistSnapshot: true);
                            PersistPlaybackSnapshot(forceWrite: true);
                        }

                        await EnsureRingPlayerAsync();
                        if (!await _ringPlayer.RequestPlaybackAsync(
                            playbackEvent.PlayRequestId,
                            playbackEvent.AudioGeneration))
                        {
                            LogService.Info($"[MediaService.OnPlaybackChanged] Ignoring stale PlaybackResumed generation={playbackEvent.AudioGeneration}, request={playbackEvent.PlayRequestId}.");
                            return;
                        }

                        _positionSynchronizer.ObserveAuthoritative(
                            currentPos,
                            PlaybackPositionOrigin.StateTransition,
                            isPlaying: true);
                        UpdateState(s => s.PlaybackState = LibrespotPlaybackState.Playing);
                        ApplyPlaybackPosition(currentPos, persistSnapshot: true);
                        if (_mediaPlayer.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
                            _mediaPlayer.Play();

                        LogService.Info($"[MediaService.OnPlaybackChanged] Playback published after pre-roll for {_state.Track?.Uri ?? "(unknown)"}, generation={playbackEvent.AudioGeneration}, request={playbackEvent.PlayRequestId}.");
                        _smtc.PlaybackStatus = MediaPlaybackStatus.Playing;
                        TryCompleteOnlineQueueRecovery(_state.Track?.Uri);
                        break;

                    case LibrespotPlaybackState.Paused:
                        if (_ringPlayer?.HasPendingTransition == true &&
                            !_ringPlayer.IsEventForPendingTransition(
                                playbackEvent.PlayRequestId,
                                playbackEvent.AudioGeneration))
                        {
                            LogService.Info($"[MediaService.OnPlaybackChanged] Ignoring stale pause while a newer PCM transition is pending. request={playbackEvent.PlayRequestId}.");
                            break;
                        }
                        if (_ringPlayer != null &&
                            !_ringPlayer.HasPendingTransition &&
                            !_ringPlayer.IsEventForActiveGeneration(
                                playbackEvent.PlayRequestId,
                                playbackEvent.AudioGeneration))
                        {
                            LogService.Info($"[MediaService.OnPlaybackChanged] Ignoring stale pause for a non-active PCM generation. request={playbackEvent.PlayRequestId}, generation={playbackEvent.AudioGeneration}.");
                            break;
                        }

                        Volatile.Write(ref _playbackRequested, 0);

                        _positionSynchronizer.ObserveAuthoritative(
                            currentPos,
                            PlaybackPositionOrigin.StateTransition,
                            isPlaying: false);
                        UpdateState(s => s.PlaybackState = LibrespotPlaybackState.Paused);
                        ApplyPlaybackPosition(currentPos, persistSnapshot: false);
                        if (_ringPlayer != null)
                            await _ringPlayer.PauseAsync();

                        if (_mediaPlayer.PlaybackSession.PlaybackState != MediaPlaybackState.Paused)
                            _mediaPlayer.Pause();

                        _smtc.PlaybackStatus = MediaPlaybackStatus.Paused;
                        PersistPlaybackSnapshot(forceWrite: true);
                        break;

                    case LibrespotPlaybackState.Stopped:
                        var hasPendingAudioTransition = _ringPlayer?.HasPendingTransition == true;
                        if (hasPendingAudioTransition &&
                            !_ringPlayer.IsEventForPendingTransition(
                                playbackEvent.PlayRequestId,
                                playbackEvent.AudioGeneration))
                        {
                            LogService.Info($"[MediaService.OnPlaybackChanged] Ignoring stale stopped event while a newer PCM transition is pending. request={playbackEvent.PlayRequestId}.");
                            break;
                        }
                        if (!hasPendingAudioTransition &&
                            _ringPlayer != null &&
                            !_ringPlayer.IsEventForActiveGeneration(
                                playbackEvent.PlayRequestId,
                                playbackEvent.AudioGeneration))
                        {
                            LogService.Info($"[MediaService.OnPlaybackChanged] Ignoring stale stopped event for a non-active PCM generation. request={playbackEvent.PlayRequestId}, generation={playbackEvent.AudioGeneration}.");
                            break;
                        }

                        if (!hasPendingAudioTransition)
                            _ringPlayer?.Stop();
                        _positionSynchronizer.ObserveAuthoritative(
                            currentPos,
                            PlaybackPositionOrigin.StateTransition,
                            isPlaying: false);
                        UpdateState(s => s.PlaybackState = LibrespotPlaybackState.Stopped);
                        ApplyPlaybackPosition(currentPos, persistSnapshot: false);
                        _mediaPlayer.Pause();
                        _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
                        PersistPlaybackSnapshot(forceWrite: true);

                        var pendingOfflineLoadTrackUri = _pendingOfflineLoadTrackUri;
                        if (!string.IsNullOrWhiteSpace(pendingOfflineLoadTrackUri) &&
                            IsOfflineSubstituteTransport())
                        {
                            LogService.Info($"[MediaService.OnPlaybackChanged] Offline load emitted Stopped for {pendingOfflineLoadTrackUri}; validating after transition settles.");
                            SchedulePendingOfflineLoadStopValidation(pendingOfflineLoadTrackUri, _offlineLoadVersion);
                            break;
                        }

                        if (_onlineQueueRecoveryActive)
                        {
                            if (ConnectivityHelper.HasInternetAccess())
                                LogService.Info("[MediaService.OnPlaybackChanged] Ignoring stopped event during online queue recovery; watchdog will decide if recovery failed.");
                            else
                            {
                                _onlineQueueRecoveryPending = true;
                                _ = ContinueDownloadedPlaybackAfterOnlineFailureAsync("online queue recovery lost connectivity");
                            }
                        }
                        else if (!hasPendingAudioTransition &&
                            (previousState == LibrespotPlaybackState.Playing ||
                            previousState == LibrespotPlaybackState.Loading)
                        )
                        {
                            ScheduleOfflineStopRecovery("playback stopped while offline or unstable");
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                LogService.Error(ex, $"[MediaService.OnPlaybackChanged] Unhandled error while processing playback state {playbackEvent?.State}");
            }
        }

        private void HandleAudioKeyUnavailable(LibrespotPlaybackEvent playbackEvent)
        {
            Volatile.Write(ref _playbackRequested, 0);
            _ringPlayer?.Stop();
            _mediaPlayer?.Pause();
            _positionSynchronizer.ObserveAuthoritative(
                0,
                PlaybackPositionOrigin.StateTransition,
                isPlaying: false);
            ApplyPlaybackPosition(0, persistSnapshot: false);
            UpdateState(state =>
            {
                state.PlaybackState = LibrespotPlaybackState.Stopped;
                state.StatusMessage = "Spotify rejected this account's audio key. This is a known Spotify/librespot compatibility issue.";
            });
            _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
            PersistPlaybackSnapshot(forceWrite: true);
            LogService.Warn($"[MediaService.HandleAudioKeyUnavailable] Playback stopped without queue skipping. track={playbackEvent.TrackUri ?? "(unknown)"}, sessionGeneration={playbackEvent.SessionGeneration}.");
        }

        private async Task<bool> TryHandleUnavailableApplicationQueueTrackAsync(
            LibrespotPlaybackEvent playbackEvent)
        {
            await _playbackGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var next = _applicationQueue.MarkPendingExpectedUnavailable(
                    playbackEvent.TrackUri,
                    playbackEvent.SessionGeneration,
                    IsApplicationQueueTrackPlayable);
                if (next == null)
                    return false;

                LogService.Warn($"[MediaService.TryHandleUnavailableApplicationQueueTrackAsync] Skipping unavailable track {playbackEvent.TrackUri}; next={next.ExpectedUri ?? "(end-of-queue)"}.");
                if (string.IsNullOrWhiteSpace(next.ExpectedUri))
                {
                    var completed = _applicationQueue.CompleteWithoutTrack(
                        next.QueueGenerationId,
                        next.TransitionId,
                        DateTimeOffset.UtcNow);
                    LogApplicationQueueTransitionResult(completed);
                    _ringPlayer?.Stop();
                    _mediaPlayer?.Pause();
                    UpdateState(state => state.PlaybackState = LibrespotPlaybackState.Stopped);
                    return true;
                }

                ApplicationQueueTransition claimed;
                if (_applicationQueue.TryClaimFallback(
                    next.QueueGenerationId,
                    next.TransitionId,
                    out claimed))
                {
                    await LoadApplicationQueueFallbackCoreAsync(claimed).ConfigureAwait(false);
                }
                return true;
            }
            finally
            {
                _playbackGate.Release();
            }
        }

        private void OnPositionChanged(object sender, LibrespotPositionUpdate update)
        {
            try
            {
                if (!IsSelectedSpotifyConnectDeviceLocal)
                    return;

                if (update.SessionGeneration != _librespot.SessionGeneration)
                    return;

                PlaybackPositionOrigin origin;
                switch (update.Origin)
                {
                    case LibrespotPositionUpdateOrigin.SeekAcknowledgement:
                        origin = PlaybackPositionOrigin.SeekAcknowledgement;
                        break;
                    case LibrespotPositionUpdateOrigin.PositionCorrection:
                        origin = PlaybackPositionOrigin.LibrespotCorrection;
                        break;
                    default:
                        origin = PlaybackPositionOrigin.LibrespotProgress;
                        break;
                }

                // This handler runs on the native callback thread and deliberately
                // performs no dispatcher work. The 4 Hz position timer publishes
                // the latest monotonic estimate to MediaState and the UI.
                _positionSynchronizer.ObserveAuthoritative(
                    update.PositionMs,
                    origin,
                    _state.PlaybackState == LibrespotPlaybackState.Playing);
            }
            catch (Exception ex)
            {
                LogService.Error(ex, $"[MediaService.OnPositionChanged] Unhandled error while processing position {update.PositionMs}");
            }
        }

        private async void OnSessionStateChanged(object sender, LibrespotSessionState ev)
        {
            try
            {
                if (ev == null || ev.SessionGeneration != _librespot.SessionGeneration)
                {
                    LogService.Info($"[MediaService.OnSessionStateChanged] Ignoring stale session event. eventSessionGeneration={ev?.SessionGeneration ?? 0}, activeSessionGeneration={_librespot.SessionGeneration}.");
                    return;
                }

                if (ev.IsConnected &&
                    Interlocked.Exchange(ref _applicationQueueSessionGeneration, ev.SessionGeneration) != ev.SessionGeneration)
                {
                    var queueGeneration = _applicationQueue.RebaseForReconnect(ev.SessionGeneration);
                    LogService.Info($"[MediaService.OnSessionStateChanged] Rebased application queue after reconnect. queueGeneration={queueGeneration}, sessionGeneration={ev.SessionGeneration}.");
                }

                // Native session activation is authoritative. Spotify's Web
                // API may still report the prior device for a few seconds;
                // waiting for that snapshot caused the following local
                // PlaybackResumed event to be discarded as remote playback.
                if (ev.IsConnected)
                    AdoptLocalSpotifyConnectSession();

                _ringPlayer?.SetSessionState(ev.IsConnected, ev.SessionGeneration);
                if (!ev.IsConnected)
                {
                    _librespotTransportUnhealthy = true;
                    _ringPlayer?.ReportTransportFailure(ev.SessionGeneration, "session-disconnected");
                }

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

            if (Volatile.Read(ref _volumeVersion) != Volatile.Read(ref _volumeAppliedVersion))
                return;

            Volatile.Write(ref _pendingVolume, volume);
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
            _applicationQueue.UpdateShuffle(enabled);
        }

        private void OnRepeatChanged(object sender, uint mode)
        {
            if (!IsSelectedSpotifyConnectDeviceLocal)
                return;

            UpdateState(s => s.RepeatMode = (int)mode);
            _applicationQueue.UpdateRepeatMode((int)mode);
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

        private void OnLibrespotLogMessage(object sender, string message)
        {
            var applicationQueueFailure = GetApplicationQueueFailureReason(message);
            if (applicationQueueFailure != null)
                _applicationQueue.RecordInternalQueueFailure(applicationQueueFailure);

            if (!IsMediaConnectivityFailure(message))
                return;

            LogService.Warn($"[MediaService.OnLibrespotLogMessage] Handling librespot media failure: {message}");

            if (IsOfflineSubstituteTransport() &&
                string.IsNullOrWhiteSpace(_pendingOfflineLoadTrackUri) &&
                Current.Track != null)
            {
                LogService.Info("[MediaService.OnLibrespotLogMessage] Ignoring media failure from offline substitute preload/state sync.");
                return;
            }

            _ringPlayer?.ReportTransportFailure(
                _librespot.SessionGeneration,
                GetTransportFailureReason(message));

            var enteredOfflineBackoff = ReportMediaConnectivityFailure("librespot media connectivity failure");
            MarkFailedDownloadedTrackFromLibrespotLog(message);

            if (_onlineQueueRecoveryActive)
                ScheduleOnlineQueueRecoveryRetry("librespot media connectivity failure");
            else if (enteredOfflineBackoff)
                ScheduleOfflineStopRecovery("librespot media connectivity failure");
        }

        private bool ReportMediaConnectivityFailure(string reason)
        {
            _librespotTransportUnhealthy = true;

            if (ConnectivityHelper.HasNetworkReportedInternetAccess())
            {
                ConnectivityHelper.ClearInternetAccessFailure(force: true);
                UpdateConnectivityState(isOfflineOverride: false);
                LogService.Info($"[MediaService.ReportMediaConnectivityFailure] Windows still reports internet access; marking librespot transport unhealthy without offline backoff. reason={reason}");
                return false;
            }

            ConnectivityHelper.ReportInternetAccessFailure(MediaFailureInternetBackoff);
            ScheduleConnectivityRecheckAfterBackoff(reason);
            return true;
        }

        private static bool IsMediaConnectivityFailure(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            return message.IndexOf("Audio key response timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("Unable to load audio item", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("Streamer error requesting range", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("Connection to server closed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("UnexpectedEof", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("unexpected EOF", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("incomplete stream", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("incomplete body", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("unexpected shutdown", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetApplicationQueueFailureReason(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return null;
            if (message.IndexOf("context is not available", StringComparison.OrdinalIgnoreCase) >= 0)
                return "spotify-context-unavailable";
            if (message.IndexOf("collection-data", StringComparison.OrdinalIgnoreCase) >= 0 &&
                message.IndexOf("parsing", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "collection-data-parse-failed";
            }
            if (message.IndexOf("no more tracks left in queue", StringComparison.OrdinalIgnoreCase) >= 0)
                return "internal-librespot-queue-empty";
            if (message.IndexOf("failed filling up next_track", StringComparison.OrdinalIgnoreCase) >= 0)
                return "internal-next-track-fill-failed";
            return null;
        }

        private static string GetTransportFailureReason(string message)
        {
            if (message?.IndexOf("UnexpectedEof", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message?.IndexOf("unexpected EOF", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "unexpected-eof";
            }

            if (message?.IndexOf("incomplete", StringComparison.OrdinalIgnoreCase) >= 0)
                return "incomplete-stream";
            if (message?.IndexOf("Connection to server closed", StringComparison.OrdinalIgnoreCase) >= 0)
                return "connection-closed";
            if (message?.IndexOf("unexpected shutdown", StringComparison.OrdinalIgnoreCase) >= 0)
                return "unexpected-shutdown";

            return "stream-failure";
        }

        private void OnProducerStalled(object sender, ProducerStalledEventArgs args)
        {
            if (args == null ||
                Volatile.Read(ref _disposed) != 0 ||
                Volatile.Read(ref _suspended) != 0 ||
                Volatile.Read(ref _producerRecoveryBlocked) != 0 ||
                !IsSelectedSpotifyConnectDeviceLocal ||
                !ReferenceEquals(sender, _ringPlayer) ||
                args.GraphInstanceId != _ringPlayer?.GraphInstanceId ||
                args.SessionGeneration != _librespot.SessionGeneration ||
                (Current.PlaybackState != LibrespotPlaybackState.Playing &&
                 Current.PlaybackState != LibrespotPlaybackState.Loading))
            {
                return;
            }

            _ = RunRequestedProducerRecoveryAsync(args);
        }

        private async Task RunRequestedProducerRecoveryAsync(ProducerStalledEventArgs args)
        {
            try
            {
                await _producerRecovery.RunAsync(
                    cancellationToken => RecoverProducerAsync(args, cancellationToken),
                    _lifetimeCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogService.Info($"[MediaService.RecoverProducerAsync] Recovery cancelled. graphId={args.GraphInstanceId}, sessionGeneration={args.SessionGeneration}.");
            }
            catch (Exception ex)
            {
                LogService.Error(ex, $"[MediaService.RecoverProducerAsync] Unhandled recovery failure. graphId={args.GraphInstanceId}, sessionGeneration={args.SessionGeneration}");
            }
        }

        private async Task RecoverProducerAsync(
            ProducerStalledEventArgs trigger,
            CancellationToken cancellationToken)
        {
            string trackUri = Current.Track?.Uri ?? _playbackIntent?.StartUri;
            string contextUri = _playbackIntent?.ContextUri ?? Current.ContextUri;
            uint positionMs = ClampPlaybackPosition(Current.PositionMs);
            if (string.IsNullOrWhiteSpace(trackUri))
            {
                LogService.Warn("[MediaService.RecoverProducerAsync] No current track is available to recover.");
                return;
            }

            if (string.IsNullOrWhiteSpace(contextUri))
                contextUri = trackUri;

            Exception lastFailure = null;
            for (int attempt = 1; attempt <= MaxProducerRecoveryAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int delayMs = BoundedRecoveryBackoff.GetDelayMilliseconds(attempt);
                if (delayMs > 0)
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);

                bool gateAcquired = false;
                bool ringPlayerGateAcquired = false;
                try
                {
                    await _playbackGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    gateAcquired = true;
                    await _ringPlayerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    ringPlayerGateAcquired = true;
                    cancellationToken.ThrowIfCancellationRequested();

                    if (Volatile.Read(ref _suspended) != 0 ||
                        Volatile.Read(ref _disposed) != 0 ||
                        !IsSelectedSpotifyConnectDeviceLocal)
                    {
                        return;
                    }

                    var player = _ringPlayer;
                    if (player == null || player.GraphInstanceId != trigger.GraphInstanceId)
                        return;

                    var accessToken = await _auth.EnsureValidAccessTokenAsync(interactive: false).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(accessToken))
                        throw new InvalidOperationException("No access token is available for producer recovery.");

                    LogService.Warn($"[MediaService.RecoverProducerAsync] Starting attempt={attempt}/{MaxProducerRecoveryAttempts}, graphId={player.GraphInstanceId}, failedSessionGeneration={trigger.SessionGeneration}, reason={trigger.Reason}, track={trackUri}, positionMs={positionMs}.");
                    player.BeginTransition(
                        "producer-recovery",
                        trackUri,
                        trackUri,
                        preserveCurrent: false,
                        shouldPlay: true);
                    UpdateState(state =>
                    {
                        state.PlaybackState = LibrespotPlaybackState.Loading;
                        state.StatusMessage = "Spotify playback stalled. Reconnecting…";
                    });

                    await _librespot.ReconnectWithAccessTokenAsync(accessToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    long replacementSessionGeneration = _librespot.SessionGeneration;
                    var replacementSession = _librespot.Session;
                    player.SetSessionState(
                        replacementSession.IsConnected &&
                        replacementSession.SessionGeneration == replacementSessionGeneration,
                        replacementSessionGeneration);

                    if (!await WaitForSessionGenerationConnectedAsync(
                        replacementSessionGeneration,
                        cancellationToken).ConfigureAwait(false))
                    {
                        throw new TimeoutException($"Session generation {replacementSessionGeneration} did not connect.");
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    SetPendingRestoreSeek(positionMs, trackUri);
                    string startUri = string.Equals(contextUri, trackUri, StringComparison.OrdinalIgnoreCase)
                        ? null
                        : trackUri;
                    await _librespot.LoadAndPlayAsync(contextUri, startUri).ConfigureAwait(false);

                    if (player is LibrespotRingBufferPlayer ringPlayer &&
                        !await WaitForProducerProgressAsync(
                            ringPlayer,
                            replacementSessionGeneration,
                            cancellationToken).ConfigureAwait(false))
                    {
                        throw new TimeoutException("The replacement session connected but produced no PCM.");
                    }

                    _librespotTransportUnhealthy = false;
                    Interlocked.Exchange(ref _producerRecoveryBlocked, 0);
                    UpdateState(state => state.StatusMessage = null);
                    LogService.Info($"[MediaService.RecoverProducerAsync] Recovery succeeded. attempt={attempt}, graphId={player.GraphInstanceId}, sessionGeneration={replacementSessionGeneration}, track={trackUri}, positionMs={positionMs}.");
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastFailure = ex;
                    _librespotTransportUnhealthy = true;
                    LogService.Warn($"[MediaService.RecoverProducerAsync] Attempt {attempt}/{MaxProducerRecoveryAttempts} failed: {ex.Message}");
                }
                finally
                {
                    if (ringPlayerGateAcquired)
                        _ringPlayerGate.Release();
                    if (gateAcquired)
                        _playbackGate.Release();
                }
            }

            Interlocked.Exchange(ref _producerRecoveryBlocked, 1);
            _ringPlayer?.Stop();
            _mediaPlayer?.Pause();
            UpdateState(state =>
            {
                state.PlaybackState = LibrespotPlaybackState.Paused;
                state.StatusMessage = "Spotify playback could not reconnect. Press Play to try again.";
            });
            LogService.Warn($"[MediaService.RecoverProducerAsync] Recovery exhausted after {MaxProducerRecoveryAttempts} attempts. graphId={trigger.GraphInstanceId}, track={trackUri}, lastFailure={lastFailure?.Message ?? "unknown"}.");
        }

        private async Task<bool> WaitForSessionGenerationConnectedAsync(
            long sessionGeneration,
            CancellationToken cancellationToken)
        {
            var startedAt = DateTimeOffset.UtcNow;
            while (DateTimeOffset.UtcNow - startedAt < LocalSessionConnectTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_librespot.SessionGeneration != sessionGeneration)
                    return false;
                if (_librespot.Session.IsConnected &&
                    _librespot.Session.SessionGeneration == sessionGeneration)
                {
                    return true;
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            return false;
        }

        private static async Task<bool> WaitForProducerProgressAsync(
            LibrespotRingBufferPlayer player,
            long sessionGeneration,
            CancellationToken cancellationToken)
        {
            var startedAt = DateTimeOffset.UtcNow;
            while (DateTimeOffset.UtcNow - startedAt < ProducerRecoveryVerificationTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var health = player.GetProducerHealthSnapshot();
                if (health.SessionGeneration != sessionGeneration)
                    return false;
                if (health.LastPcmWriteMs > 0 && health.LastSuccessfulStreamReadMs > 0)
                    return true;

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            return false;
        }

        private void CancelProducerRecovery(bool resetAttemptBudget)
        {
            _producerRecovery.CancelActive();
            if (resetAttemptBudget)
                Interlocked.Exchange(ref _producerRecoveryBlocked, 0);
        }

        private void MarkFailedDownloadedTrackFromLibrespotLog(string message)
        {
            const string prefix = "Audio key response timeout for track ";
            var index = message?.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) ?? -1;
            if (index < 0 || App.OfflineCatalog == null)
                return;

            var start = index + prefix.Length;
            var end = message.IndexOfAny(new[] { ' ', '.', ',', ';' }, start);
            if (end < 0)
                end = message.Length;

            var spotifyId = message.Substring(start, end - start).Trim();
            if (string.IsNullOrWhiteSpace(spotifyId))
                return;

            var trackUri = spotifyId.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase)
                ? spotifyId
                : $"spotify:track:{spotifyId}";

            if (!App.OfflineCatalog.IsTrackPersisted(trackUri))
                return;

            _offlinePlaybackFailedTrackUris.Add(trackUri);
            LogService.Warn($"[MediaService.MarkFailedDownloadedTrackFromLibrespotLog] Excluding failed downloaded track for this session: {trackUri}");
        }

        private async Task ConnectAfterAuthChangedAsync(string accessToken)
        {
            CancelPlaybackContinuationWatchdog();
            await _playbackGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _librespot.ConnectWithAccessTokenAsync(accessToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MediaService.OnAuthChanged] Unable to reconnect librespot after auth changed: {ex.Message}");
            }
            finally
            {
                _playbackGate.Release();
            }
        }

        private void OnNetworkStatusChanged(object sender)
        {
            LogService.Info("[MediaService.OnNetworkStatusChanged] Windows reported a network status change.");
            if (ConnectivityHelper.HasNetworkReportedInternetAccess() &&
                ConnectivityHelper.ClearInternetAccessFailure(force: true))
            {
                LogService.Info("[MediaService.OnNetworkStatusChanged] Windows reports internet access, clearing media connectivity backoff early.");
                return;
            }

            if (ConnectivityHelper.HasNetworkReportedInternetAccess())
                ScheduleConnectivityRecheckAfterBackoff("Windows reported internet access");

            _ = HandleNetworkStatusChangedAsync();
        }

        private void OnInternetAccessFailureReported(object sender, EventArgs e)
        {
            ScheduleConnectivityRecheckAfterBackoff("internet access failure reported");
        }

        private void OnConnectivityStatusChanged(object sender, EventArgs e)
        {
            _ = HandleNetworkStatusChangedAsync();
        }

        private void ScheduleConnectivityRecheckAfterBackoff(string reason)
        {
            var remaining = ConnectivityHelper.GetInternetAccessFailureBackoffRemaining();
            if (remaining <= TimeSpan.Zero)
                return;

            var delay = remaining.Add(TimeSpan.FromSeconds(1));
            var version = Interlocked.Increment(ref _connectivityRecheckVersion);
            LogService.Info($"[MediaService.ScheduleConnectivityRecheckAfterBackoff] Rechecking connectivity in {delay.TotalMilliseconds:0}ms. reason={reason}");
            _ = RecheckConnectivityAfterBackoffAsync(version, delay, reason);
        }

        private async Task RecheckConnectivityAfterBackoffAsync(int version, TimeSpan delay, string reason)
        {
            await Task.Delay(delay).ConfigureAwait(false);
            if (version != _connectivityRecheckVersion)
                return;

            if (ConnectivityHelper.HasNetworkReportedInternetAccess())
            {
                LogService.Info($"[MediaService.RecheckConnectivityAfterBackoffAsync] Backoff expired and Windows reports internet access. reason={reason}");
                ConnectivityHelper.ClearInternetAccessFailure();
            }

            await HandleNetworkStatusChangedAsync().ConfigureAwait(false);
        }

        private async Task RefreshCurrentTrackMetadataAfterConnectivityRestoredAsync()
        {
            try
            {
                await RefreshCurrentTrackMetadataAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MediaService.RefreshCurrentTrackMetadataAfterConnectivityRestoredAsync] Unable to refresh current track metadata: {ex.Message}");
            }
        }

        private void OnEndOfTrack(object sender, LibrespotTrackBoundaryInfo boundary)
        {
            if (boundary == null || boundary.SessionGeneration != _librespot.SessionGeneration)
                return;

            LogService.Info($"[MediaService.OnEndOfTrack] Received end-of-track for {boundary.TrackUri ?? "(unknown)"}, request={boundary.PlayRequestId}, sessionGeneration={boundary.SessionGeneration}.");
            UpdateConnectivityState();
            var shouldContinuePlaying = Volatile.Read(ref _playbackRequested) != 0;
            var transition = _applicationQueue.BeginEndOfTrack(
                boundary.TrackUri,
                boundary.PlayRequestId,
                boundary.SessionGeneration,
                DateTimeOffset.UtcNow,
                IsApplicationQueueTrackPlayable);

            if (!transition.IsValid)
            {
                LogService.Info($"[MediaService.OnEndOfTrack] Ignoring {transition.FailureReason}; queueGeneration={transition.QueueGenerationId}.");
                return;
            }

            _ringPlayer?.BeginAutomaticTransition(boundary.TrackUri, shouldContinuePlaying);
            _ = CompleteApplicationQueueTransitionAsync(transition, shouldContinuePlaying);
        }

        private void OnTimeToPreloadNextTrack(object sender, LibrespotTrackBoundaryInfo boundary)
        {
            if (boundary == null || boundary.SessionGeneration != _librespot.SessionGeneration)
                return;

            var transition = _applicationQueue.ObservePreloadRequested(
                boundary.TrackUri,
                boundary.PlayRequestId,
                boundary.SessionGeneration,
                IsApplicationQueueTrackPlayable);
            if (!transition.IsValid)
                return;

            LogService.Info($"[MediaService.OnTimeToPreloadNextTrack] queueGeneration={transition.QueueGenerationId}, transition={transition.TransitionId}, expected={transition.ExpectedUri ?? "(end-of-queue)"}.");
        }

        private void OnTrackPreloading(object sender, LibrespotTrackBoundaryInfo boundary)
        {
            if (boundary == null || boundary.SessionGeneration != _librespot.SessionGeneration)
                return;

            var matched = _applicationQueue.ObservePreloaded(boundary.TrackUri, boundary.SessionGeneration);
            LogService.Info($"[MediaService.OnTrackPreloading] uri={boundary.TrackUri ?? "(unknown)"}, matchesApplicationQueue={matched}.");
        }

        private async Task CompleteApplicationQueueTransitionAsync(
            ApplicationQueueTransition transition,
            bool shouldContinuePlaying)
        {
            await Task.Delay(EndOfQueueGracePeriod).ConfigureAwait(false);
            await _playbackGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) != 0 ||
                    Volatile.Read(ref _suspended) != 0 ||
                    !IsSelectedSpotifyConnectDeviceLocal)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(transition.ExpectedUri))
                {
                    if (Current.IsOffline && HasOnlineQueueResumePoint())
                    {
                        var waiting = _applicationQueue.RetargetPendingExpected(
                            _onlineQueueResumeStartUri,
                            "next-track-not-available-offline");
                        if (waiting != null)
                        {
                            ApplicationQueueTransition claimedWaiting;
                            _applicationQueue.TryClaimFallback(
                                waiting.QueueGenerationId,
                                waiting.TransitionId,
                                out claimedWaiting);
                            SetWaitingForOnlineApplicationQueue(waiting.ExpectedUri);
                            return;
                        }
                    }

                    var completed = _applicationQueue.CompleteWithoutTrack(
                        transition.QueueGenerationId,
                        transition.TransitionId,
                        DateTimeOffset.UtcNow);
                    if (completed == null)
                        return;

                    _ringPlayer?.Stop();
                    _mediaPlayer?.Pause();
                    UpdateState(state =>
                    {
                        state.PlaybackState = LibrespotPlaybackState.Stopped;
                        state.StatusMessage = Current.IsOffline ? "Offline queue finished." : null;
                    });
                    if (_smtc != null)
                        _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
                    LogApplicationQueueTransitionResult(completed);
                    return;
                }

                if (!shouldContinuePlaying)
                {
                    LogApplicationQueueTransitionResult(_applicationQueue.CancelPendingTransition(
                        "playback-no-longer-requested",
                        DateTimeOffset.UtcNow));
                    return;
                }

                ApplicationQueueTransition claimed;
                if (!_applicationQueue.TryClaimFallback(
                    transition.QueueGenerationId,
                    transition.TransitionId,
                    out claimed))
                {
                    return;
                }


                if (Current.IsOffline && !UserSettings.PlayDownloadedSongsDuringConnectionLoss)
                {
                    SetOnlineQueueResumeTrack(_applicationQueue.Snapshot.ContextUri, claimed.ExpectedUri);
                    SetWaitingForOnlineApplicationQueue(claimed.ExpectedUri);
                    return;
                }

                await LoadApplicationQueueFallbackCoreAsync(claimed).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MediaService.CompleteApplicationQueueTransitionAsync] Fallback failed: {ex.Message}");
                LogApplicationQueueTransitionResult(_applicationQueue.CompleteWithoutTrack(
                    transition.QueueGenerationId,
                    transition.TransitionId,
                    DateTimeOffset.UtcNow));
            }
            finally
            {
                _playbackGate.Release();
            }
        }

        private async Task LoadApplicationQueueFallbackCoreAsync(ApplicationQueueTransition transition)
        {
            LogService.Warn($"[MediaService.LoadApplicationQueueFallbackCoreAsync] Loading expected={transition.ExpectedUri}, queueGeneration={transition.QueueGenerationId}, transition={transition.TransitionId}, reason={transition.FailureReason}.");
            await EnsureRingPlayerAsync().ConfigureAwait(false);
            _ringPlayer.BeginTransition(
                "application-queue-fallback",
                Current.Track?.Uri,
                transition.ExpectedUri,
                preserveCurrent: false,
                shouldPlay: true);
            UpdateState(state => state.PlaybackState = LibrespotPlaybackState.Loading);
            if (_mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                _mediaPlayer.Pause();
            ApplyPlaybackPosition(0, persistSnapshot: false);

            if (Current.IsOffline)
            {
                _pendingOfflineLoadTrackUri = transition.ExpectedUri;
                Interlocked.Increment(ref _offlineLoadVersion);
            }

            // librespot_load(..., play: true) is the only playback command used here.
            // Calling Resume/Play as well races a player that may already be Playing.
            var snapshot = _applicationQueue.Snapshot;
            var contextUri = string.IsNullOrWhiteSpace(snapshot.ContextUri)
                ? transition.ExpectedUri
                : snapshot.ContextUri;
            await _librespot.LoadAndPlayAsync(
                contextUri,
                transition.ExpectedUri,
                snapshot.TrackUris).ConfigureAwait(false);
        }

        private void SetWaitingForOnlineApplicationQueue(string expectedUri)
        {
            _waitingForOnlineQueueContinuation = true;
            UpdateState(state =>
            {
                state.PlaybackState = LibrespotPlaybackState.Paused;
                state.StatusMessage = "Offline. Waiting for internet before continuing the queue.";
            });
            LogService.Info($"[MediaService.SetWaitingForOnlineApplicationQueue] Waiting to load {expectedUri} when connectivity returns.");
        }

        private bool IsApplicationQueueTrackPlayable(string trackUri)
        {
            if (!Current.IsOffline && ConnectivityHelper.HasInternetAccess())
                return true;
            return IsDownloadedTrackPlayableOffline(trackUri);
        }

        private static void LogApplicationQueueTransitionResult(ApplicationQueueTransitionResult result)
        {
            if (result == null)
                return;
            LogService.Info(
                $"[MediaService.ApplicationQueueTransitionResult] expected={result.ExpectedNextUri}, preloaded={result.PreloadedUri}, " +
                $"actual={result.ActualChangedUri}, fallback={result.FallbackUsed}, failure={result.InternalQueueFailureReason}, " +
                $"elapsedMs={result.ElapsedTransitionMilliseconds}, queueGeneration={result.QueueGenerationId}, transition={result.TransitionId}.");
        }

        private void UpdateConnectivityState(bool? isOfflineOverride = null)
        {
            var isOffline = isOfflineOverride ?? !ConnectivityHelper.HasInternetAccess();
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

        private static bool IsConnectivityStatusMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            return message.StartsWith("Offline.", StringComparison.OrdinalIgnoreCase) ||
                message.StartsWith("Offline queue", StringComparison.OrdinalIgnoreCase) ||
                message.StartsWith("Internet is unstable", StringComparison.OrdinalIgnoreCase) ||
                message.StartsWith("Internet is not stable", StringComparison.OrdinalIgnoreCase) ||
                message.StartsWith("Internet restored.", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsOfflineSubstituteTransport()
        {
            return _transportMode == PlaybackTransportMode.OfflineContextSubstitute ||
                _transportMode == PlaybackTransportMode.OfflineRandomSubstitute;
        }

        private bool IsOfflineSubstituteLoadOrPlaybackActive()
        {
            if (!IsOfflineSubstituteTransport())
                return false;

            if (!string.IsNullOrWhiteSpace(_pendingOfflineLoadTrackUri))
                return true;

            return Current.PlaybackState == LibrespotPlaybackState.Playing;
        }

        private void SchedulePendingOfflineLoadStopValidation(string trackUri, int version)
        {
            _ = ValidatePendingOfflineLoadStoppedAsync(trackUri, version);
        }

        private async Task ValidatePendingOfflineLoadStoppedAsync(string trackUri, int version)
        {
            await Task.Delay(OfflineLoadStopValidationDelay).ConfigureAwait(false);

            if (version != _offlineLoadVersion ||
                !string.Equals(_pendingOfflineLoadTrackUri, trackUri, StringComparison.OrdinalIgnoreCase) ||
                Current.PlaybackState == LibrespotPlaybackState.Playing ||
                Current.PlaybackState == LibrespotPlaybackState.Loading)
            {
                return;
            }

            LogService.Warn($"[MediaService.ValidatePendingOfflineLoadStoppedAsync] Offline load did not reach playback; excluding {trackUri} for this session.");
            _offlinePlaybackFailedTrackUris.Add(trackUri);
            _pendingOfflineLoadTrackUri = null;
            Interlocked.Increment(ref _offlineLoadVersion);
            ScheduleOfflineStopRecovery("offline load stopped before playback");
        }

        private bool TryPlayOfflineRelativeTrack(int delta)
        {
            if (!Current.IsOffline || _offlineQueue.Length == 0)
                return false;

            if (_currentOfflineFallbackIsRandom)
            {
                LogService.Info("[MediaService.TryPlayOfflineRelativeTrack] Current offline fallback is random; not advancing the original context queue.");
                return false;
            }

            if (!UserSettings.PlayDownloadedSongsDuringConnectionLoss)
            {
                LogService.Info("[MediaService.TryPlayOfflineRelativeTrack] Offline queue continuation is disabled; waiting for connectivity to return.");
                UpdateState(s =>
                {
                    s.StatusMessage = "Offline. Waiting for internet before continuing the queue.";
                });
                return false;
            }

            var nextIndex = FindPlayableOfflineQueueIndex(_offlineQueue, _offlineQueueIndex, delta);
            if (nextIndex < 0)
            {
                LogService.Info($"[MediaService.TryPlayOfflineRelativeTrack] No downloaded track available from index {_offlineQueueIndex} with delta {delta}. Queue size={_offlineQueue.Length}.");
                return false;
            }

            _offlineQueueIndex = nextIndex;
            SetOnlineQueueResumePoint(_offlineQueueContextUri, _offlineQueue, _offlineQueueIndex + 1);
            LogService.Info($"[MediaService.TryPlayOfflineRelativeTrack] Advancing offline queue to index {_offlineQueueIndex} ({_offlineQueue[_offlineQueueIndex]}).");
            var contextUri = string.IsNullOrWhiteSpace(_offlineQueueContextUri)
                ? _offlineQueue[_offlineQueueIndex]
                : _offlineQueueContextUri;
            _ = PlayAsync(
                contextUri,
                _offlineQueue[_offlineQueueIndex],
                _offlineQueue,
                _offlineQueueIndex);
            return true;
        }

        private bool TryResumeOnlineQueueFromOfflineBoundary(bool shouldContinuePlaying)
        {
            if (!_onlineQueueRecoveryPending && !_waitingForOnlineQueueContinuation)
                return false;

            var contextUri = FirstNonBlank(
                _onlineQueueResumeContextUri,
                _playbackIntent?.ResumeContextUri,
                _offlineQueueContextUri,
                _playbackIntent?.PlaybackContextUri);
            if (string.IsNullOrWhiteSpace(contextUri))
                return false;

            var startUri = FirstNonBlank(_onlineQueueResumeStartUri, _playbackIntent?.ResumeStartUri);
            if (string.IsNullOrWhiteSpace(startUri))
            {
                LogService.Info("[MediaService.TryResumeOnlineQueueFromOfflineBoundary] No known next track is available, avoiding a context restart.");
                return false;
            }

            _ = BeginOnlineQueueRecoveryAsync(contextUri, startUri, shouldContinuePlaying, "track boundary");
            return true;
        }

        private void SetOnlineQueueResumePoint(string contextUri, IReadOnlyList<string> queue, int resumeIndex)
        {
            if (string.IsNullOrWhiteSpace(contextUri) || queue == null)
                return;

            _onlineQueueResumeContextUri = contextUri;
            _onlineQueueResumeStartUri = resumeIndex >= 0 && resumeIndex < queue.Count
                ? queue[resumeIndex]
                : null;

            if (_playbackIntent != null)
            {
                _playbackIntent.ResumeContextUri = _onlineQueueResumeContextUri;
                _playbackIntent.ResumeStartUri = _onlineQueueResumeStartUri;
            }

            LogService.Info($"[MediaService.SetOnlineQueueResumePoint] Online resume context={_onlineQueueResumeContextUri}, start={_onlineQueueResumeStartUri ?? "(none)"}, resumeIndex={resumeIndex}, queueSize={queue.Count}.");
        }

        private void SetOnlineQueueResumeTrack(string contextUri, string startUri)
        {
            _onlineQueueResumeContextUri = contextUri;
            _onlineQueueResumeStartUri = startUri;

            if (_playbackIntent != null)
            {
                _playbackIntent.ResumeContextUri = contextUri;
                _playbackIntent.ResumeStartUri = startUri;
            }

            LogService.Info($"[MediaService.SetOnlineQueueResumeTrack] Online resume context={contextUri ?? "(null)"}, start={startUri ?? "(null)"}.");
        }

        private bool HasOnlineQueueResumePoint()
        {
            return !string.IsNullOrWhiteSpace(_onlineQueueResumeContextUri) &&
                !string.IsNullOrWhiteSpace(_onlineQueueResumeStartUri);
        }

        private async Task BeginOnlineQueueRecoveryAsync(string contextUri, string startUri, bool shouldContinuePlaying, string reason)
        {
            _onlineQueueRecoveryPending = false;
            _waitingForOnlineQueueContinuation = false;
            _onlineQueueRecoveryActive = true;
            _onlineQueueRecoveryRetryScheduled = false;
            _onlineQueueRecoveryAttempt = 1;
            _onlineQueueRecoveryContextUri = contextUri;
            _onlineQueueRecoveryStartUri = startUri;

            var version = Interlocked.Increment(ref _onlineQueueRecoveryVersion);
            LogService.Info($"[MediaService.BeginOnlineQueueRecovery] Returning to online context={contextUri}, start={startUri}, reason={reason}, attempt={_onlineQueueRecoveryAttempt}.");
            SetTransportMode(PlaybackTransportMode.OnlineContext, $"online queue recovery: {reason}");
            UpdateState(s =>
            {
                s.IsOffline = false;
                s.IsRecoveringOnlinePlayback = true;
                s.StatusMessage = "Reconnecting to Spotify...";
            });

            if (!await EnsureOnlineRecoveryHealthAsync(reason).ConfigureAwait(false))
            {
                LogService.Info($"[MediaService.BeginOnlineQueueRecovery] Online recovery deferred because connectivity is not healthy. reason={reason}");
                ClearOnlineQueueRecoveryState(clearResumePoint: false);
                _onlineQueueRecoveryPending = HasOnlineQueueResumePoint();
                SetTransportMode(PlaybackTransportMode.WaitingForOnline, "online recovery health check failed");
                await ContinueDownloadedPlaybackAfterOnlineFailureAsync(reason).ConfigureAwait(false);
                return;
            }

            _ = PlayOnlineQueueRecoveryAsync(contextUri, startUri);
            ScheduleOnlineQueueRecoveryWatchdog(version, shouldContinuePlaying, reason);
        }

        private async Task<bool> EnsureOnlineRecoveryHealthAsync(string reason)
        {
            if (!ConnectivityHelper.HasInternetAccess())
            {
                ScheduleConnectivityRecheckAfterBackoff($"online recovery health check while offline: {reason}");
                return false;
            }

            try
            {
                var token = await _auth.EnsureValidAccessTokenAsync(interactive: false).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(token))
                    return false;

                await _auth.EnsureCurrentAccountIsPremiumAsync().ConfigureAwait(false);
                ConnectivityHelper.ClearInternetAccessFailure();
                return true;
            }
            catch (SpotifyPremiumRequiredException ex)
            {
                LogService.Warn($"[MediaService.EnsureOnlineRecoveryHealthAsync] Spotify Premium is required: {ex.Message}");
                UpdateState(s =>
                {
                    s.StatusMessage = "Spotify Premium is required for playback.";
                });
                return false;
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MediaService.EnsureOnlineRecoveryHealthAsync] Connectivity probe failed while resuming queue: {ex.Message}");
                ReportMediaConnectivityFailure("online recovery health check failed");
                return false;
            }
        }

        private void ScheduleOnlineQueueRecoveryWatchdog(int version, bool shouldContinuePlaying, string reason)
        {
            if (shouldContinuePlaying)
                _ = WatchOnlineQueueRecoveryAsync(version, reason);
        }

        private async Task WatchOnlineQueueRecoveryAsync(int version, string reason)
        {
            await Task.Delay(OnlineQueueRecoveryWatchdogDelay).ConfigureAwait(false);
            if (version != _onlineQueueRecoveryVersion || !_onlineQueueRecoveryActive)
                return;

            if (Current.PlaybackState == LibrespotPlaybackState.Playing &&
                string.Equals(Current.Track?.Uri, _onlineQueueRecoveryStartUri, StringComparison.OrdinalIgnoreCase))
            {
                TryCompleteOnlineQueueRecovery(Current.Track?.Uri);
                return;
            }

            ScheduleOnlineQueueRecoveryRetry($"watchdog after {reason}");
        }

        private void ScheduleOnlineQueueRecoveryRetry(string reason)
        {
            if (!_onlineQueueRecoveryActive || _onlineQueueRecoveryRetryScheduled)
                return;

            _onlineQueueRecoveryRetryScheduled = true;
            var version = _onlineQueueRecoveryVersion;
            _ = RetryOnlineQueueRecoveryAsync(version, reason);
        }

        private async Task RetryOnlineQueueRecoveryAsync(int version, string reason)
        {
            await Task.Delay(1500).ConfigureAwait(false);
            _onlineQueueRecoveryRetryScheduled = false;

            if (version != _onlineQueueRecoveryVersion || !_onlineQueueRecoveryActive)
                return;

            if (!ConnectivityHelper.HasInternetAccess())
            {
                LogService.Info("[MediaService.RetryOnlineQueueRecoveryAsync] Internet was lost again before retrying online queue recovery.");
                ClearOnlineQueueRecoveryState(clearResumePoint: false);
                _onlineQueueRecoveryPending = HasOnlineQueueResumePoint();
                await ContinueDownloadedPlaybackAfterOnlineFailureAsync(reason).ConfigureAwait(false);
                return;
            }

            if (Current.PlaybackState == LibrespotPlaybackState.Playing &&
                string.Equals(Current.Track?.Uri, _onlineQueueRecoveryStartUri, StringComparison.OrdinalIgnoreCase))
            {
                TryCompleteOnlineQueueRecovery(Current.Track?.Uri);
                return;
            }

            if (_onlineQueueRecoveryAttempt >= MaxOnlineQueueRecoveryAttempts)
            {
                LogService.Warn($"[MediaService.RetryOnlineQueueRecoveryAsync] Online queue recovery failed after {_onlineQueueRecoveryAttempt} attempts. context={_onlineQueueRecoveryContextUri}, start={_onlineQueueRecoveryStartUri}, reason={reason}.");
                var enteredOfflineBackoff = ReportMediaConnectivityFailure("online queue recovery retry failed");
                UpdateState(s =>
                {
                    s.IsOffline = enteredOfflineBackoff;
                    s.StatusMessage = enteredOfflineBackoff
                        ? "Internet is not stable enough to resume the queue. Continuing offline playback."
                        : "Spotify playback did not resume automatically. Try again.";
                });
                ClearOnlineQueueRecoveryState(clearResumePoint: false);
                _onlineQueueRecoveryPending = HasOnlineQueueResumePoint();
                await ContinueDownloadedPlaybackAfterOnlineFailureAsync(reason).ConfigureAwait(false);
                return;
            }

            _onlineQueueRecoveryAttempt++;
            LogService.Info($"[MediaService.RetryOnlineQueueRecoveryAsync] Retrying online queue recovery attempt {_onlineQueueRecoveryAttempt}/{MaxOnlineQueueRecoveryAttempts}. reason={reason}, context={_onlineQueueRecoveryContextUri}, start={_onlineQueueRecoveryStartUri}.");
            await PlayOnlineQueueRecoveryAsync(_onlineQueueRecoveryContextUri, _onlineQueueRecoveryStartUri).ConfigureAwait(false);
            ScheduleOnlineQueueRecoveryWatchdog(_onlineQueueRecoveryVersion, shouldContinuePlaying: true, reason: "retry");
        }

        private void TryCompleteOnlineQueueRecovery(string trackUri)
        {
            if (!_onlineQueueRecoveryActive ||
                string.IsNullOrWhiteSpace(trackUri) ||
                !string.Equals(trackUri, _onlineQueueRecoveryStartUri, StringComparison.OrdinalIgnoreCase) ||
                Current.PlaybackState != LibrespotPlaybackState.Playing)
            {
                return;
            }

            LogService.Info($"[MediaService.TryCompleteOnlineQueueRecovery] Online queue recovery reached {trackUri}.");
            _offlineQueue = Array.Empty<string>();
            _offlineQueueIndex = -1;
            _offlineQueueContextUri = null;
            _currentOfflineFallbackIsRandom = false;
            ClearOnlineQueueRecoveryState(clearResumePoint: true);
        }

        private void ClearOnlineQueueRecoveryState(bool clearResumePoint)
        {
            _onlineQueueRecoveryPending = false;
            _waitingForOnlineQueueContinuation = false;
            _onlineQueueRecoveryActive = false;
            _onlineQueueRecoveryRetryScheduled = false;
            _onlineQueueRecoveryAttempt = 0;
            _onlineQueueRecoveryContextUri = null;
            _onlineQueueRecoveryStartUri = null;

            if (clearResumePoint)
            {
                _onlineQueueResumeContextUri = null;
                _onlineQueueResumeStartUri = null;
                if (_playbackIntent != null)
                {
                    _playbackIntent.ResumeContextUri = null;
                    _playbackIntent.ResumeStartUri = null;
                }
            }

            UpdateState(s =>
            {
                s.IsRecoveringOnlinePlayback = false;
                if (!s.IsOffline && string.Equals(s.StatusMessage, "Reconnecting to Spotify...", StringComparison.Ordinal))
                    s.StatusMessage = null;
            });
        }

        private async Task<bool> TryPlayRandomOfflineFallbackAsync(string reason)
        {
            if (!ShouldUseRandomOfflineFallback())
                return false;

            CancelPlaybackContinuationWatchdog();
            await _playbackGate.WaitAsync();
            try
            {
                return await TryLoadRandomOfflineFallbackCoreAsync(reason).ConfigureAwait(false);
            }
            finally
            {
                _playbackGate.Release();
            }
        }

        private async Task<bool> TryLoadRandomOfflineFallbackCoreAsync(string reason, bool allowWhileOnline = false)
        {
            if (!ShouldUseRandomOfflineFallback() ||
                (!allowWhileOnline && ConnectivityHelper.HasInternetAccess()) ||
                App.OfflineCatalog == null)
            {
                return false;
            }

            var trackUri = await ChooseRandomDownloadedTrackUriAsync(Current.Track?.Uri).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(trackUri))
            {
                LogService.Info($"[MediaService.TryLoadRandomOfflineFallbackCoreAsync] Random offline fallback requested but no downloaded tracks were available. reason={reason}.");
                return false;
            }

            try
            {
                _currentOfflineFallbackIsRandom = true;
                LogService.Info($"[MediaService.TryLoadRandomOfflineFallbackCoreAsync] Playing random downloaded fallback {trackUri}. reason={reason}.");
                return await LoadOfflineFallbackTrackCoreAsync(
                    trackUri,
                    "Offline. Playing a random downloaded song until internet returns.",
                    FirstNonBlank(_onlineQueueResumeContextUri, _playbackIntent?.PlaybackContextUri)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _currentOfflineFallbackIsRandom = false;
                _offlinePlaybackFailedTrackUris.Add(trackUri);
                LogService.Warn($"[MediaService.TryLoadRandomOfflineFallbackCoreAsync] Unable to play random offline fallback {trackUri}: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> LoadOfflineFallbackTrackCoreAsync(string trackUri, string statusMessage, string stateContextUri = null)
        {
            if (string.IsNullOrWhiteSpace(trackUri))
                return false;

            if (!await EnsureLocalLibrespotConnectedAsync(interactive: true, allowOfflineToken: true).ConfigureAwait(false))
                return false;

            var contextUri = FirstNonBlank(
                stateContextUri,
                _offlineQueueContextUri,
                _onlineQueueResumeContextUri,
                _playbackIntent?.PlaybackContextUri,
                trackUri);

            SetTransportMode(
                _currentOfflineFallbackIsRandom
                    ? PlaybackTransportMode.OfflineRandomSubstitute
                    : PlaybackTransportMode.OfflineContextSubstitute,
                "loading offline fallback track");

            var networkOffline = !ConnectivityHelper.HasNetworkReportedInternetAccess();
            var effectiveStatusMessage = !networkOffline &&
                statusMessage != null &&
                statusMessage.StartsWith("Offline.", StringComparison.OrdinalIgnoreCase)
                    ? "Playing a downloaded song while Spotify reconnects."
                    : statusMessage;

            UpdateState(s =>
            {
                s.IsOffline = networkOffline;
                s.PlaybackState = LibrespotPlaybackState.Loading;
                s.ContextUri = contextUri;
                s.ContextName = null;
                s.StatusMessage = effectiveStatusMessage;
            });

            await EnsureRingPlayerAsync().ConfigureAwait(false);
            _ringPlayer.BeginTransition(
                "offline-load",
                Current.Track?.Uri,
                trackUri,
                preserveCurrent: false,
                shouldPlay: true);
            if (_mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                _mediaPlayer.Pause();
            ApplyPlaybackPosition(0, persistSnapshot: false);

            _pendingOfflineLoadTrackUri = trackUri;
            Interlocked.Increment(ref _offlineLoadVersion);
            await _librespot.LoadAndPlayAsync(trackUri, null).ConfigureAwait(false);
            return true;
        }

        private async Task<string> ChooseRandomDownloadedTrackUriAsync(string excludedTrackUri)
        {
            var downloadedTracks = await App.OfflineCatalog.GetDownloadedTracksAsync().ConfigureAwait(false);
            var candidates = downloadedTracks
                .Select(track => track.TrackUri)
                .Where(IsDownloadedTrackPlayableOffline)
                .Where(uri => !string.Equals(uri, excludedTrackUri, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 0)
            {
                candidates = downloadedTracks
                    .Select(track => track.TrackUri)
                    .Where(IsDownloadedTrackPlayableOffline)
                    .ToList();
            }

            if (candidates.Count == 0)
                return null;

            lock (_random)
                return candidates[_random.Next(candidates.Count)];
        }

        private static bool ShouldUseRandomOfflineFallback()
        {
            return UserSettings.PlayDownloadedSongsDuringConnectionLoss &&
                UserSettings.PlayRandomOfflineSongWhenUnavailable;
        }

        private void ScheduleOfflineStopRecovery(string reason)
        {
            if (ConnectivityHelper.HasInternetAccess() && !IsOfflineSubstituteTransport())
                return;

            var version = Interlocked.Increment(ref _offlineStopRecoveryVersion);
            _ = RecoverDownloadedPlaybackAfterUnexpectedStopAsync(version, reason);
        }

        private async Task RecoverDownloadedPlaybackAfterUnexpectedStopAsync(int version, string reason)
        {
            await Task.Delay(750).ConfigureAwait(false);
            if (version != _offlineStopRecoveryVersion)
                return;

            var state = Current.PlaybackState;
            if (state == LibrespotPlaybackState.Playing)
                return;

            if (state == LibrespotPlaybackState.Loading &&
                (!IsOfflineSubstituteTransport() || !string.IsNullOrWhiteSpace(_pendingOfflineLoadTrackUri)))
            {
                return;
            }

            if (ConnectivityHelper.HasInternetAccess())
            {
                _onlineQueueRecoveryPending = HasOnlineQueueResumePoint();
                if (_onlineQueueRecoveryPending &&
                    TryResumeOnlineQueueFromOfflineBoundary(shouldContinuePlaying: true))
                {
                    LogService.Info($"[MediaService.RecoverDownloadedPlaybackAfterUnexpectedStopAsync] Resuming online queue after offline substitute stalled. reason={reason}");
                    return;
                }

                if (!IsOfflineSubstituteTransport())
                    return;
            }

            if (IsOfflineSubstituteLoadOrPlaybackActive())
                return;

            LogService.Warn($"[MediaService.RecoverDownloadedPlaybackAfterUnexpectedStopAsync] Playback was {state} while offline/substitute; trying downloaded fallback. reason={reason}");
            await ContinueDownloadedPlaybackAfterOnlineFailureAsync(reason).ConfigureAwait(false);
        }

        private async Task<bool> ContinueDownloadedPlaybackAfterOnlineFailureAsync(string reason)
        {
            if (!UserSettings.PlayDownloadedSongsDuringConnectionLoss)
            {
                UpdateState(s =>
                {
                    s.IsOffline = true;
                    s.PlaybackState = LibrespotPlaybackState.Paused;
                    s.StatusMessage = "Offline. Waiting for internet before continuing the queue.";
                });
                return false;
            }

            if (IsOfflineSubstituteLoadOrPlaybackActive())
            {
                LogService.Info($"[MediaService.ContinueDownloadedPlaybackAfterOnlineFailureAsync] Offline substitute is already active; not starting another fallback. reason={reason}");
                return true;
            }

            if (Interlocked.CompareExchange(ref _downloadedPlaybackRecoveryInProgress, 1, 0) != 0)
            {
                LogService.Info($"[MediaService.ContinueDownloadedPlaybackAfterOnlineFailureAsync] Downloaded playback recovery is already running. reason={reason}");
                return true;
            }

            try
            {
                CancelPlaybackContinuationWatchdog();
                await _playbackGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    UpdateConnectivityState(isOfflineOverride: !ConnectivityHelper.HasNetworkReportedInternetAccess());

                    if (!_currentOfflineFallbackIsRandom && _offlineQueue.Length > 0)
                    {
                        var searchIndex = _offlineQueueIndex;
                        while (true)
                        {
                            var nextIndex = FindPlayableOfflineQueueIndex(_offlineQueue, searchIndex, 1);
                            if (nextIndex < 0)
                                break;

                            _offlineQueueIndex = nextIndex;
                            SetOnlineQueueResumePoint(_offlineQueueContextUri, _offlineQueue, _offlineQueueIndex + 1);
                            _currentOfflineFallbackIsRandom = false;
                            LogService.Info($"[MediaService.ContinueDownloadedPlaybackAfterOnlineFailureAsync] Continuing downloaded queue at index {_offlineQueueIndex} ({_offlineQueue[_offlineQueueIndex]}). reason={reason}");

                            try
                            {
                                return await LoadOfflineFallbackTrackCoreAsync(
                                    _offlineQueue[_offlineQueueIndex],
                                    "Internet is unstable. Continuing with downloaded playback.",
                                    _offlineQueueContextUri).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                LogService.Warn($"[MediaService.ContinueDownloadedPlaybackAfterOnlineFailureAsync] Unable to load downloaded track {_offlineQueue[_offlineQueueIndex]}: {ex.Message}");
                                _offlinePlaybackFailedTrackUris.Add(_offlineQueue[_offlineQueueIndex]);
                                searchIndex = _offlineQueueIndex;
                            }
                        }
                    }

                    if (ShouldKeepPersistedOfflineContextQueue())
                    {
                        LogService.Info($"[MediaService.ContinueDownloadedPlaybackAfterOnlineFailureAsync] Preserving persisted context queue instead of choosing a random downloaded song. reason={reason}");
                        UpdateState(s =>
                        {
                            s.IsOffline = true;
                            s.PlaybackState = LibrespotPlaybackState.Paused;
                            s.StatusMessage = "Offline. No further downloaded songs are available in this album or playlist.";
                        });
                        return false;
                    }

                    if (await TryLoadRandomOfflineFallbackCoreAsync(reason, allowWhileOnline: true).ConfigureAwait(false))
                        return true;

                    UpdateState(s =>
                    {
                        s.IsOffline = true;
                        s.PlaybackState = LibrespotPlaybackState.Paused;
                        s.StatusMessage = HasOnlineQueueResumePoint()
                            ? "Offline. Waiting for internet before continuing the queue."
                            : "Offline. No downloaded continuation is available.";
                    });
                    return false;
                }
                finally
                {
                    _playbackGate.Release();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _downloadedPlaybackRecoveryInProgress, 0);
            }
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

        private void UpdateStateWithoutNotification(Action<MediaState> mutator)
        {
            lock (_lock)
            {
                var clone = _state.Clone();
                mutator(clone);
                _state = clone;
            }
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
                snapshot = await Task.Run(() =>
                {
                    UiResponsivenessTelemetry.VerifyBackgroundThread("playback snapshot JSON parsing");
                    return JsonConvert.DeserializeObject<PlaybackSnapshot>(json);
                }).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MediaService.RestorePlaybackSnapshotAsync] Unable to parse playback snapshot: {ex.Message}");
                return;
            }

                if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.TrackUri))
                return;

            _lastPlaybackSnapshot = snapshot;
            _restoredSnapshotPendingResume = true;
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
            var offlineTrack = await App.OfflineCatalog.GetDownloadedTrackAsync(snapshot.TrackUri);
            var artworkUri = FirstNonBlank(
                ResolveArtworkUri(null, trackInfo, offlineTrack),
                snapshot.ArtworkUri);

            UpdateState(s =>
            {
                s.Track = trackInfo;
                s.ContextUri = snapshot.ContextUri;
                s.ContextName = snapshot.ContextName;
                s.PositionMs = snapshot.PositionMs;
                s.DurationMs = snapshot.DurationMs;
                s.PlaybackState = LibrespotPlaybackState.Paused;
                s.Volume = snapshot.Volume > 0 ? snapshot.Volume : s.Volume;
                s.ArtworkUri = artworkUri ?? s.ArtworkUri;
            });

            UpdateSmtcDisplay();
            UpdateSmtcTimeline(snapshot.PositionMs);
            if (_smtc != null)
                _smtc.PlaybackStatus = MediaPlaybackStatus.Paused;

            if (UserSettings.ResumeLastPlaybackIfWasPlaying && snapshot.WasPlaying)
            {
                _restoredSnapshotPendingResume = false;
                SetPendingRestoreSeek(snapshot.PositionMs, snapshot.TrackUri);

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

                _lastSnapshotWriteAt = now;
                _snapshotWriteQueue.Post(() =>
                {
                    try
                    {
                        UiResponsivenessTelemetry.VerifyBackgroundThread("playback snapshot persistence");
                        var json = JsonConvert.SerializeObject(snapshot);
                        Windows.Storage.ApplicationData.Current.LocalSettings.Values[PlaybackSnapshotKey] = json;
                    }
                    catch (Exception ex)
                    {
                        LogService.Telemetry(
                            "playback-snapshot-write-failure",
                            $"Unable to save playback snapshot: {ex.Message}",
                            warning: true);
                    }
                });
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
            await _networkStatusGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await HandleNetworkStatusChangedCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _networkStatusGate.Release();
            }
        }

        private async Task HandleNetworkStatusChangedCoreAsync()
        {
            var wasOffline = _state.IsOffline;
            var isOffline = !ConnectivityHelper.HasInternetAccess();
            if (wasOffline != isOffline)
            {
                LogService.Info(
                    $"[MediaService.HandleNetworkStatusChangedAsync] Connectivity changed {(isOffline ? "offline" : "online")}; playback={_state.PlaybackState}, sessionConnected={_librespot.Session.IsConnected}, offlineQueue={_offlineQueue.Length}, offlineQueueIndex={_offlineQueueIndex}.");
            }

            UpdateConnectivityState(isOffline);

            if (isOffline)
            {
                if (ConnectivityHelper.HasNetworkReportedInternetAccess())
                    ScheduleConnectivityRecheckAfterBackoff("network reports internet while media backoff is active");

                await PrepareOfflineQueueForCurrentTrackAsync().ConfigureAwait(false);

                if (_librespot.Session.IsConnected || (_librespot as LibrespotService)?.HasInstance == true)
                    LogService.Info("[MediaService.HandleNetworkStatusChangedAsync] Connectivity lost or offline mode enabled, keeping librespot alive for local playback.");

                return;
            }

            try
            {
                UpdateState(s =>
                {
                    s.IsOffline = false;
                    if (!s.IsRecoveringOnlinePlayback && IsConnectivityStatusMessage(s.StatusMessage))
                        s.StatusMessage = null;
                });
                _ = RefreshCurrentTrackMetadataAfterConnectivityRestoredAsync();

                if (IsSelectedSpotifyConnectDeviceLocal &&
                    _state.PlaybackState == LibrespotPlaybackState.Playing &&
                    !_waitingForOnlineQueueContinuation)
                {
                    if (HasOnlineQueueResumePoint())
                    {
                        _onlineQueueRecoveryPending = true;
                        UpdateState(s =>
                        {
                            s.IsOffline = false;
                            s.StatusMessage = "Internet restored. Spotify playback will resume after this song.";
                        });
                        LogService.Info("[MediaService.HandleNetworkStatusChangedAsync] Connectivity restored; keeping current local song running and deferring queue recovery until a track boundary.");
                    }
                    else
                    {
                        UpdateState(s =>
                        {
                            s.IsOffline = false;
                            if (s.StatusMessage != null &&
                                s.StatusMessage.StartsWith("Offline.", StringComparison.OrdinalIgnoreCase))
                            {
                                s.StatusMessage = null;
                            }
                        });
                        LogService.Info("[MediaService.HandleNetworkStatusChangedAsync] Connectivity restored while local playback is active; leaving playback uninterrupted.");
                    }

                    return;
                }

                if (IsSelectedSpotifyConnectDeviceLocal &&
                    _onlineQueueRecoveryActive &&
                    _state.PlaybackState != LibrespotPlaybackState.Stopped)
                {
                    LogService.Info("[MediaService.HandleNetworkStatusChangedAsync] Connectivity restored while online queue recovery is active.");
                    return;
                }

                var accessToken = await _auth.EnsureValidAccessTokenAsync(interactive: false);
                if (!string.IsNullOrWhiteSpace(accessToken) &&
                    (!_librespot.Session.IsConnected || _librespotTransportUnhealthy))
                {
                    CancelPlaybackContinuationWatchdog();
                    await _playbackGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (_librespotTransportUnhealthy)
                        {
                            LogService.Info("[MediaService.HandleNetworkStatusChangedAsync] Connectivity restored, recreating unhealthy librespot transport.");
                            _ringPlayer?.BeginTransition(
                                "reconnect",
                                Current.Track?.Uri,
                                Current.Track?.Uri,
                                preserveCurrent: false,
                                shouldPlay: _state.PlaybackState == LibrespotPlaybackState.Playing ||
                                    _state.PlaybackState == LibrespotPlaybackState.Loading);
                            await _librespot.ReconnectWithAccessTokenAsync(accessToken).ConfigureAwait(false);
                            _librespotTransportUnhealthy = false;
                        }
                        else
                        {
                            LogService.Info("[MediaService.HandleNetworkStatusChangedAsync] Connectivity restored, reconnecting librespot.");
                            await _librespot.ConnectWithAccessTokenAsync(accessToken).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _playbackGate.Release();
                    }

                    if (App.OfflineCatalog != null)
                        await App.OfflineCatalog.RemoveExpiredPersistedTracksAsync();
                }

                if (IsSelectedSpotifyConnectDeviceLocal &&
                    _waitingForOnlineQueueContinuation)
                {
                    LogService.Info("[MediaService.HandleNetworkStatusChangedAsync] Connectivity restored; resuming queue that was waiting for internet.");
                    if (!TryResumeOnlineQueueFromOfflineBoundary(shouldContinuePlaying: true))
                    {
                        _waitingForOnlineQueueContinuation = false;
                        var playbackState = Current.PlaybackState;
                        if (playbackState == LibrespotPlaybackState.Paused ||
                            playbackState == LibrespotPlaybackState.Stopped)
                        {
                            await ResumeAsync().ConfigureAwait(false);
                        }
                    }
                }

                if (IsSelectedSpotifyConnectDeviceLocal &&
                    _onlineQueueRecoveryPending &&
                    _state.PlaybackState != LibrespotPlaybackState.Playing)
                {
                    if (_state.PlaybackState == LibrespotPlaybackState.Loading)
                    {
                        LogService.Info("[MediaService.HandleNetworkStatusChangedAsync] Connectivity restored while local fallback is still loading; deferring online queue recovery.");
                        return;
                    }

                    LogService.Info("[MediaService.HandleNetworkStatusChangedAsync] Connectivity restored; starting pending online queue recovery.");
                    TryResumeOnlineQueueFromOfflineBoundary(shouldContinuePlaying: true);
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MediaService.HandleNetworkStatusChangedAsync] Unable to reconnect after connectivity restored: {ex.Message}");
                ReportMediaConnectivityFailure("connectivity restored reconnect failed");
            }
        }

        private async Task PrepareOfflineQueueForCurrentTrackAsync()
        {
            var trackUri = _state.Track?.Uri;
            if (string.IsNullOrWhiteSpace(trackUri) || App.OfflineCatalog == null)
                return;

            var currentTrackPersisted = App.OfflineCatalog.IsTrackPersisted(trackUri);

            var contextUri = FirstNonBlank(
                _onlineQueueResumeContextUri,
                _playbackIntent?.ResumeContextUri,
                _playbackIntent?.PlaybackContextUri,
                _state.ContextUri,
                trackUri);
            var isPersistedContextQueue = IsPersistedOfflineContext(contextUri);
            var queue = await GetKnownTrackUrisForPlaybackContextAsync(contextUri).ConfigureAwait(false);
            if (queue.Count == 0)
            {
                if (!currentTrackPersisted)
                {
                    LogService.Info($"[MediaService.PrepareOfflineQueueForCurrentTrackAsync] Current track {trackUri} is not fully downloaded and no cached context queue was available.");
                    return;
                }

                queue = new[] { trackUri };
            }

            _offlineQueue = queue.ToArray();
            _offlineQueueIndex = IndexOfTrackUri(_offlineQueue, trackUri);
            if (_offlineQueueIndex >= 0)
            {
                _currentOfflineFallbackIsRandom = false;
                SetOnlineQueueResumePoint(contextUri, _offlineQueue, _offlineQueueIndex + 1);
            }
            else
            {
                if (!currentTrackPersisted)
                {
                    LogService.Info($"[MediaService.PrepareOfflineQueueForCurrentTrackAsync] Current track {trackUri} is not in known context {contextUri} and is not fully downloaded.");
                    _offlineQueue = Array.Empty<string>();
                    _offlineQueueIndex = -1;
                    _offlineQueueContextUri = null;
                    return;
                }

                if (!_currentOfflineFallbackIsRandom &&
                    isPersistedContextQueue &&
                    _offlineQueue.Length > 0)
                {
                    var resumeIndex = IndexOfTrackUri(_offlineQueue, _onlineQueueResumeStartUri);
                    _offlineQueueIndex = resumeIndex > 0 ? resumeIndex - 1 : -1;
                    SetOnlineQueueResumePoint(contextUri, _offlineQueue, resumeIndex >= 0 ? resumeIndex : 0);
                    LogService.Warn($"[MediaService.PrepareOfflineQueueForCurrentTrackAsync] Current downloaded track {trackUri} was not found in persisted context {contextUri}; preserving the {queue.Count}-track context queue from index {_offlineQueueIndex}.");
                }
                else
                {
                    _offlineQueue = new[] { trackUri };
                    _offlineQueueIndex = 0;
                    LogService.Warn($"[MediaService.PrepareOfflineQueueForCurrentTrackAsync] Current downloaded track {trackUri} was not found in known context {contextUri}; using a single-track queue.");
                }
            }
            _offlineQueueContextUri = contextUri;

            UpdateState(s =>
            {
                s.IsOffline = true;
                s.StatusMessage = currentTrackPersisted
                    ? "Offline. Continuing with downloaded playback."
                    : "Offline. Will continue with downloaded songs when available.";
            });

            LogService.Info($"[MediaService.PrepareOfflineQueueForCurrentTrackAsync] Prepared offline queue for {trackUri}. Queue size={_offlineQueue.Length}, index={_offlineQueueIndex}, currentPersisted={currentTrackPersisted}.");
        }

        private async Task<IReadOnlyList<string>> GetKnownTrackUrisForPlaybackContextAsync(string contextUri)
        {
            if (string.IsNullOrWhiteSpace(contextUri))
                return Array.Empty<string>();

            if (App.OfflineCatalog != null)
            {
                var catalogQueue = await App.OfflineCatalog.GetKnownTrackUrisForContextAsync(contextUri).ConfigureAwait(false);
                if (catalogQueue.Count > 0)
                {
                    LogService.Info($"[MediaService.GetKnownTrackUrisForPlaybackContextAsync] Loaded {catalogQueue.Count} known tracks for {contextUri} from offline catalog.");
                    return catalogQueue;
                }
            }

            try
            {
                if (TryGetSpotifyUriId(contextUri, "spotify:album:", out var albumId))
                {
                    var albumTracks = await _web.GetAlbumTracksAsync(albumId, false).ConfigureAwait(false);
                    var albumItems = await GetAllPagingItemsAsync(albumTracks?.Value).ConfigureAwait(false);
                    var trackUris = albumItems
                        .Select(track => track?.Uri)
                        .Where(IsSpotifyTrackUri)
                        .ToList();

                    if (trackUris?.Count > 0)
                    {
                        LogService.Info($"[MediaService.GetKnownTrackUrisForPlaybackContextAsync] Loaded {trackUris.Count} known tracks for {contextUri} from {(albumTracks.IsOfflineFallback || albumTracks.IsFromCache ? "cached" : "live")} album metadata.");
                        return trackUris;
                    }
                }
                else if (TryGetSpotifyUriId(contextUri, "spotify:playlist:", out var playlistId))
                {
                    var playlistItems = await _web.GetPlaylistItemsAsync(playlistId, false).ConfigureAwait(false);
                    var allPlaylistItems = await GetAllPagingItemsAsync(playlistItems?.Value).ConfigureAwait(false);
                    var trackUris = allPlaylistItems
                        .Select(item => (item?.Track as FullTrack)?.Uri)
                        .Where(IsSpotifyTrackUri)
                        .ToList();

                    if (trackUris?.Count > 0)
                    {
                        LogService.Info($"[MediaService.GetKnownTrackUrisForPlaybackContextAsync] Loaded {trackUris.Count} known tracks for {contextUri} from {(playlistItems.IsOfflineFallback || playlistItems.IsFromCache ? "cached" : "live")} playlist metadata.");
                        return trackUris;
                    }
                }
                else if (string.Equals(contextUri, "spotify:collection:tracks", StringComparison.OrdinalIgnoreCase))
                {
                    var savedTracks = await _web.GetSavedTracksAsync(false).ConfigureAwait(false);
                    var allSavedTracks = await GetAllPagingItemsAsync(savedTracks?.Value).ConfigureAwait(false);
                    var trackUris = allSavedTracks
                        .Select(item => item?.Track?.Uri)
                        .Where(IsSpotifyTrackUri)
                        .ToList();
                    if (trackUris?.Count > 0)
                    {
                        LogService.Info($"[MediaService.GetKnownTrackUrisForPlaybackContextAsync] Loaded {trackUris.Count} liked-song URIs from {(savedTracks.IsOfflineFallback || savedTracks.IsFromCache ? "cache" : "Spotify")}.");
                        return trackUris;
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MediaService.GetKnownTrackUrisForPlaybackContextAsync] Unable to resolve known track order for {contextUri}: {ex.Message}");
            }

            if (IsSpotifyTrackUri(contextUri))
                return new[] { contextUri };

            return Array.Empty<string>();
        }

        private async Task<IReadOnlyList<T>> GetAllPagingItemsAsync<T>(Paging<T> firstPage)
        {
            if (firstPage == null)
                return Array.Empty<T>();

            var items = new List<T>(firstPage.Items ?? new List<T>());
            var page = firstPage;
            while (!string.IsNullOrWhiteSpace(page.Next))
            {
                var response = await _web.GetNextPageAsync(page).ConfigureAwait(false);
                page = response?.Value;
                if (page == null)
                    break;
                if (page.Items != null)
                    items.AddRange(page.Items);
            }
            return items;
        }

        private int FindPlayableOfflineQueueIndex(IReadOnlyList<string> queue, int currentIndex, int delta)
        {
            if (queue == null || queue.Count == 0 || delta == 0 || App.OfflineCatalog == null)
                return -1;

            for (var index = currentIndex + delta; index >= 0 && index < queue.Count; index += delta)
            {
                if (IsDownloadedTrackPlayableOffline(queue[index]))
                    return index;
            }

            return -1;
        }

        private bool IsDownloadedTrackPlayableOffline(string trackUri)
        {
            if (!IsSpotifyTrackUri(trackUri) ||
                _offlinePlaybackFailedTrackUris.Contains(trackUri) ||
                App.OfflineCatalog == null ||
                !App.OfflineCatalog.IsTrackPersisted(trackUri))
            {
                return false;
            }

            var trackIdHex = SpotifyIdHelper.TrackUriToHexId(trackUri);
            var hasKey = !string.IsNullOrWhiteSpace(trackIdHex) &&
                App.KeyCache?.GetKeySync(trackIdHex) != null;

            if (!hasKey)
                LogService.Warn($"[MediaService.IsDownloadedTrackPlayableOffline] Skipping persisted track without a cached audio key: {trackUri}");

            return hasKey;
        }

        private bool ShouldKeepPersistedOfflineContextQueue()
        {
            return !_currentOfflineFallbackIsRandom &&
                _offlineQueue.Length > 0 &&
                IsPersistedOfflineContext(_offlineQueueContextUri);
        }

        private bool IsPersistedOfflineContext(string contextUri)
        {
            if (App.OfflineCatalog == null || string.IsNullOrWhiteSpace(contextUri))
                return false;

            if (TryGetSpotifyUriId(contextUri, "spotify:album:", out var albumId))
                return App.OfflineCatalog.IsAlbumPersisted(albumId);

            if (TryGetSpotifyUriId(contextUri, "spotify:playlist:", out var playlistId))
                return App.OfflineCatalog.IsPlaylistPersisted(playlistId);

            return false;
        }

        private static int IndexOfTrackUri(IReadOnlyList<string> queue, string trackUri)
        {
            if (queue == null || string.IsNullOrWhiteSpace(trackUri))
                return -1;

            for (var index = 0; index < queue.Count; index++)
            {
                if (string.Equals(queue[index], trackUri, StringComparison.OrdinalIgnoreCase))
                    return index;
            }

            return -1;
        }

        private static bool TryGetSpotifyUriId(string uri, string prefix, out string id)
        {
            id = null;
            if (string.IsNullOrWhiteSpace(uri) ||
                !uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            id = uri.Substring(prefix.Length);
            return !string.IsNullOrWhiteSpace(id);
        }

        private static bool IsSpotifyTrackUri(string uri)
        {
            return !string.IsNullOrWhiteSpace(uri) &&
                uri.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveArtworkUri(FullTrack metadata, LibrespotTrackInfo track, OfflineTrackEntry offlineTrack)
        {
            var localImageUri = GetExistingLocalArtworkUri(offlineTrack?.ImageLocalUri);
            if (!string.IsNullOrWhiteSpace(localImageUri))
                return localImageUri;

            var imageUrl = metadata?.Album?.Images?.FirstOrDefault()?.Url;
            if (!string.IsNullOrWhiteSpace(imageUrl))
                return imageUrl;

            if (!string.IsNullOrWhiteSpace(offlineTrack?.ImageUrl))
                return offlineTrack.ImageUrl;

            return track?.CoverUrl;
        }

        private static string GetExistingLocalArtworkUri(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            if (!value.StartsWith(AppDataLocalUriPrefix, StringComparison.OrdinalIgnoreCase))
                return value;

            // OfflineCatalogService only commits this URI after its asynchronous
            // image write succeeds. Avoid probing the filesystem from playback/UI
            // event handlers; a missing cache file is handled by the image source.
            return value;
        }

        private static string FirstNonBlank(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
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

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try { _lifetimeCancellation.Cancel(); } catch (ObjectDisposedException) { }
            _producerRecovery.Dispose();
            CancelPlaybackContinuationWatchdog();

            NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
            ConnectivityHelper.InternetAccessFailureReported -= OnInternetAccessFailureReported;
            ConnectivityHelper.ConnectivityStatusChanged -= OnConnectivityStatusChanged;
            _auth.AuthStateChanged -= OnAuthChanged;
            _librespot.TrackChanged -= OnTrackChanged;
            _librespot.PlaybackEvent -= OnPlaybackChanged;
            _librespot.PositionChanged -= OnPositionChanged;
            _librespot.SessionStateChanged -= OnSessionStateChanged;
            _librespot.VolumeChanged -= OnVolumeChanged;
            _librespot.ShuffleChanged -= OnShuffleChanged;
            _librespot.RepeatChanged -= OnRepeatChanged;
            _librespot.EndOfTrack -= OnEndOfTrack;
            _librespot.TimeToPreloadNextTrack -= OnTimeToPreloadNextTrack;
            _librespot.TrackPreloading -= OnTrackPreloading;
            _librespot.LogMessage -= OnLibrespotLogMessage;

            _positionTimer?.Stop();
            _volumeDebounceTimer?.Stop();
            _spotifyConnectTimer?.Stop();
            if (_smtc != null)
                _smtc.ButtonPressed -= OnSmtcButtonPressed;

            _ = DisposeRingPlayerAsync();

            _mediaPlayer?.Dispose();
            _mediaPlayer = null;
        }

        private async Task DisposeRingPlayerAsync()
        {
            await _ringPlayerGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_ringPlayer != null)
                {
                    _ringPlayer.ProducerStalled -= OnProducerStalled;
                    await _ringPlayer.DisposeAsync().ConfigureAwait(false);
                    _ringPlayer = null;
                }

                LogService.Info($"[MediaService.Dispose] Media service disposed. sessionGeneration={_librespot.SessionGeneration}, liveGraphs={LibrespotRingBufferPlayer.LiveGraphCount}.");
            }
            finally
            {
                _ringPlayerGate.Release();
            }
        }
    }
}
