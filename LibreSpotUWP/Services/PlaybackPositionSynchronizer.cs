using System;
using System.Diagnostics;

namespace LibreSpotUWP.Services
{
    public enum PlaybackPositionOrigin
    {
        SeekAcknowledgement,
        LibrespotCorrection,
        LibrespotProgress,
        PeriodicPoll,
        RemoteConnect,
        StateTransition
    }

    public sealed class PositionSeekRequest
    {
        public long Token { get; set; }
        public uint PositionMs { get; set; }
    }

    public struct PositionObservation
    {
        public PlaybackPositionOrigin Origin { get; set; }
        public long DriftMs { get; set; }
        public bool AppliedHardCorrection { get; set; }
    }

    public sealed class PositionCorrectionBurstSummary
    {
        public int Count { get; set; }
        public long DurationMs { get; set; }
        public long MaximumDriftMs { get; set; }
        public int HardCorrections { get; set; }
        public int ActualSeeksIssued { get; set; }
    }

    /// <summary>
    /// Keeps position display updates separate from seek commands. Authoritative
    /// updates adjust a monotonic local clock, but only user seek requests can
    /// produce an underlying seek token.
    /// </summary>
    public sealed class PlaybackPositionSynchronizer
    {
        // A second is larger than ordinary decoder, dispatcher and Connect clock
        // jitter while still correcting a genuinely stale playback position.
        public const int HardCorrectionToleranceMs = 1000;
        public const int UiUpdateIntervalMs = 250;
        public const int CorrectionBurstQuietPeriodMs = 1000;

        private readonly object _gate = new object();
        private readonly Func<long> _monotonicMilliseconds;

        private bool _hasAnchor;
        private uint _anchorPositionMs;
        private long _anchorTimeMs;
        private long _lastUiUpdateMs = long.MinValue;
        private long _nextSeekToken;
        private long _pendingSeekToken;

        private bool _burstActive;
        private long _burstStartedMs;
        private long _burstLastCorrectionMs;
        private int _burstCount;
        private long _burstMaximumDriftMs;
        private int _burstHardCorrections;
        private int _burstActualSeeks;
        private int _actualSeeksSinceLastBurst;

        public PlaybackPositionSynchronizer()
            : this(GetStopwatchMilliseconds)
        {
        }

        public PlaybackPositionSynchronizer(Func<long> monotonicMilliseconds)
        {
            _monotonicMilliseconds = monotonicMilliseconds ?? throw new ArgumentNullException(nameof(monotonicMilliseconds));
        }

        public PositionSeekRequest BeginUserSeek(uint positionMs)
        {
            lock (_gate)
            {
                var now = _monotonicMilliseconds();
                var token = ++_nextSeekToken;
                _pendingSeekToken = token;
                ResetAnchorUnsafe(positionMs, now);
                return new PositionSeekRequest { Token = token, PositionMs = positionMs };
            }
        }

        /// <summary>
        /// Claims a user seek token exactly once. Corrections, polls and UI
        /// refreshes never have a token and therefore cannot issue a seek.
        /// </summary>
        public bool TryRecordSeekIssued(PositionSeekRequest request)
        {
            if (request == null)
                return false;

            lock (_gate)
            {
                if (request.Token == 0 || request.Token != _pendingSeekToken)
                    return false;

                _pendingSeekToken = 0;
                RecordActualSeekUnsafe();
                return true;
            }
        }

        public void RecordSystemSeekIssued()
        {
            lock (_gate)
                RecordActualSeekUnsafe();
        }

        public void Reset(uint positionMs)
        {
            lock (_gate)
            {
                _pendingSeekToken = 0;
                ResetAnchorUnsafe(positionMs, _monotonicMilliseconds());
            }
        }

        public PositionObservation ObserveAuthoritative(
            uint positionMs,
            PlaybackPositionOrigin origin,
            bool isPlaying)
        {
            lock (_gate)
            {
                var now = _monotonicMilliseconds();
                if (!_hasAnchor)
                {
                    ResetAnchorUnsafe(positionMs, now);
                    return new PositionObservation { Origin = origin };
                }

                var expected = EstimateUnsafe(now, isPlaying);
                var drift = (long)positionMs - expected;
                var absoluteDrift = Math.Abs(drift);
                var hardCorrection = false;

                if (origin == PlaybackPositionOrigin.LibrespotCorrection)
                    RecordCorrectionUnsafe(now, absoluteDrift);

                if (origin == PlaybackPositionOrigin.SeekAcknowledgement ||
                    origin == PlaybackPositionOrigin.StateTransition ||
                    absoluteDrift > HardCorrectionToleranceMs)
                {
                    ResetAnchorUnsafe(positionMs, now);
                    hardCorrection = absoluteDrift > HardCorrectionToleranceMs;
                    if (hardCorrection && origin == PlaybackPositionOrigin.LibrespotCorrection)
                        _burstHardCorrections++;
                }

                return new PositionObservation
                {
                    Origin = origin,
                    DriftMs = drift,
                    AppliedHardCorrection = hardCorrection
                };
            }
        }

        public bool TryGetVisiblePosition(
            uint durationMs,
            bool isPlaying,
            bool force,
            out uint positionMs)
        {
            lock (_gate)
            {
                var now = _monotonicMilliseconds();
                if (!_hasAnchor)
                    ResetAnchorUnsafe(0, now);

                if (!force && _lastUiUpdateMs != long.MinValue &&
                    now - _lastUiUpdateMs < UiUpdateIntervalMs)
                {
                    positionMs = 0;
                    return false;
                }

                _lastUiUpdateMs = now;
                var estimated = EstimateUnsafe(now, isPlaying);
                if (durationMs > 0 && estimated > durationMs)
                    estimated = durationMs;

                positionMs = (uint)estimated;
                return true;
            }
        }

        public uint GetEstimatedPosition(uint durationMs, bool isPlaying)
        {
            lock (_gate)
            {
                var now = _monotonicMilliseconds();
                if (!_hasAnchor)
                    ResetAnchorUnsafe(0, now);

                var estimated = EstimateUnsafe(now, isPlaying);
                if (durationMs > 0 && estimated > durationMs)
                    estimated = durationMs;
                return (uint)estimated;
            }
        }

        public bool TryTakeCorrectionBurstSummary(out PositionCorrectionBurstSummary summary)
        {
            lock (_gate)
            {
                var now = _monotonicMilliseconds();
                if (!_burstActive || now - _burstLastCorrectionMs < CorrectionBurstQuietPeriodMs)
                {
                    summary = null;
                    return false;
                }

                summary = new PositionCorrectionBurstSummary
                {
                    Count = _burstCount,
                    DurationMs = Math.Max(0, _burstLastCorrectionMs - _burstStartedMs),
                    MaximumDriftMs = _burstMaximumDriftMs,
                    HardCorrections = _burstHardCorrections,
                    ActualSeeksIssued = _burstActualSeeks
                };

                _burstActive = false;
                _burstCount = 0;
                _burstMaximumDriftMs = 0;
                _burstHardCorrections = 0;
                _burstActualSeeks = 0;
                return true;
            }
        }

        private long EstimateUnsafe(long now, bool isPlaying)
        {
            var elapsed = isPlaying ? Math.Max(0, now - _anchorTimeMs) : 0;
            return Math.Min(uint.MaxValue, (long)_anchorPositionMs + elapsed);
        }

        private void ResetAnchorUnsafe(uint positionMs, long now)
        {
            _hasAnchor = true;
            _anchorPositionMs = positionMs;
            _anchorTimeMs = now;
        }

        private void RecordCorrectionUnsafe(long now, long absoluteDrift)
        {
            if (!_burstActive || now - _burstLastCorrectionMs >= CorrectionBurstQuietPeriodMs)
            {
                _burstActive = true;
                _burstStartedMs = now;
                _burstCount = 0;
                _burstMaximumDriftMs = 0;
                _burstHardCorrections = 0;
                _burstActualSeeks = _actualSeeksSinceLastBurst;
                _actualSeeksSinceLastBurst = 0;
            }

            _burstLastCorrectionMs = now;
            _burstCount++;
            _burstMaximumDriftMs = Math.Max(_burstMaximumDriftMs, absoluteDrift);
        }

        private void RecordActualSeekUnsafe()
        {
            if (_burstActive)
                _burstActualSeeks++;
            else
                _actualSeeksSinceLastBurst++;
        }

        private static long GetStopwatchMilliseconds()
        {
            var timestamp = Stopwatch.GetTimestamp();
            var frequency = Stopwatch.Frequency;
            return (timestamp / frequency) * 1000L +
                (timestamp % frequency) * 1000L / frequency;
        }
    }
}
