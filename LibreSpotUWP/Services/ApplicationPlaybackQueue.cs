using System;
using System.Collections.Generic;
using System.Linq;

namespace LibreSpotUWP.Services
{
    public sealed class ApplicationPlaybackQueueSnapshot
    {
        public string ContextUri { get; internal set; }
        public IReadOnlyList<string> TrackUris { get; internal set; }
        public int CurrentIndex { get; internal set; }
        public IReadOnlyList<int> ShuffleOrder { get; internal set; }
        public long ShuffleSeed { get; internal set; }
        public bool Shuffle { get; internal set; }
        public int RepeatMode { get; internal set; }
        public long GenerationId { get; internal set; }
    }

    public sealed class ApplicationQueueTransition
    {
        public long QueueGenerationId { get; internal set; }
        public long TransitionId { get; internal set; }
        public int FromIndex { get; internal set; }
        public int ExpectedIndex { get; internal set; }
        public string ExpectedUri { get; internal set; }
        public string PreloadedUri { get; internal set; }
        public ulong FromPlayRequestId { get; internal set; }
        public long SessionGeneration { get; internal set; }
        public DateTimeOffset StartedAtUtc { get; internal set; }
        public bool EndObserved { get; internal set; }
        public bool FallbackUsed { get; internal set; }
        public bool IsUserInitiated { get; internal set; }
        public bool IsValid { get; internal set; }
        public string FailureReason { get; internal set; }
    }

    public sealed class ApplicationQueueTransitionResult
    {
        public string ExpectedNextUri { get; set; }
        public string PreloadedUri { get; set; }
        public string ActualChangedUri { get; set; }
        public bool FallbackUsed { get; set; }
        public string InternalQueueFailureReason { get; set; }
        public long ElapsedTransitionMilliseconds { get; set; }
        public long QueueGenerationId { get; set; }
        public long TransitionId { get; set; }
    }

    /// <summary>
    /// Owns playback ordering independently of librespot. Indexes identify occurrences,
    /// rather than URIs, so duplicate tracks remain unambiguous.
    /// </summary>
    public sealed class ApplicationPlaybackQueue
    {
        private readonly object _gate = new object();
        private readonly HashSet<int> _unavailableIndexes = new HashSet<int>();
        private string _contextUri;
        private string[] _tracks = new string[0];
        private int[] _playOrder = new int[0];
        private int _currentIndex = -1;
        private bool _shuffle;
        private long _shuffleSeed;
        private int _repeatMode;
        private long _generationId;
        private long _nextTransitionId;
        private ulong _currentPlayRequestId;
        private long _sessionGeneration;
        private bool _awaitingInitialConfirmation;
        private bool _allowBoundaryBeforeInitialConfirmation;
        private string _lastInternalQueueFailure;
        private ApplicationQueueTransition _pending;

        public ApplicationPlaybackQueueSnapshot Snapshot
        {
            get
            {
                lock (_gate)
                {
                    return new ApplicationPlaybackQueueSnapshot
                    {
                        ContextUri = _contextUri,
                        TrackUris = _tracks.ToArray(),
                        CurrentIndex = _currentIndex,
                        ShuffleOrder = _playOrder.ToArray(),
                        ShuffleSeed = _shuffleSeed,
                        Shuffle = _shuffle,
                        RepeatMode = _repeatMode,
                        GenerationId = _generationId
                    };
                }
            }
        }

        public long Replace(
            string contextUri,
            IEnumerable<string> orderedTrackUris,
            string startUri,
            int startIndex,
            bool shuffle,
            int repeatMode,
            long? shuffleSeed = null)
        {
            lock (_gate)
            {
                _contextUri = contextUri;
                _tracks = (orderedTrackUris ?? Enumerable.Empty<string>())
                    .Where(uri => !string.IsNullOrWhiteSpace(uri))
                    .ToArray();
                _shuffle = shuffle;
                _shuffleSeed = shuffleSeed ?? CreateShuffleSeed();
                _repeatMode = NormalizeRepeatMode(repeatMode);
                _unavailableIndexes.Clear();
                _currentIndex = ResolveStartIndex(_tracks, startUri, startIndex);
                _playOrder = BuildPlayOrder(_tracks.Length, _shuffle, _shuffleSeed, _currentIndex);
                _currentPlayRequestId = 0;
                _sessionGeneration = 0;
                _awaitingInitialConfirmation = _tracks.Length > 0;
                _allowBoundaryBeforeInitialConfirmation = false;
                _lastInternalQueueFailure = null;
                _pending = null;
                return ++_generationId;
            }
        }

        public bool TryHydrate(
            long expectedGenerationId,
            IEnumerable<string> orderedTrackUris,
            string startUri,
            int startIndex)
        {
            var hydratedTracks = (orderedTrackUris ?? Enumerable.Empty<string>())
                .Where(uri => !string.IsNullOrWhiteSpace(uri))
                .ToArray();
            if (hydratedTracks.Length == 0)
                return false;

            lock (_gate)
            {
                if (_generationId != expectedGenerationId || _pending != null)
                    return false;

                var currentUri = UriAt(_currentIndex) ?? startUri;
                var hydratedCurrentIndex = ResolveStartIndex(hydratedTracks, currentUri, startIndex);
                if (hydratedCurrentIndex < 0)
                    return false;

                _tracks = hydratedTracks;
                _currentIndex = hydratedCurrentIndex;
                _playOrder = BuildPlayOrder(_tracks.Length, _shuffle, _shuffleSeed, _currentIndex);
                _unavailableIndexes.Clear();
                return true;
            }
        }

        public void UpdateShuffle(bool shuffle, long? shuffleSeed = null)
        {
            lock (_gate)
            {
                if (_shuffle == shuffle && !shuffleSeed.HasValue)
                    return;

                _shuffle = shuffle;
                _shuffleSeed = shuffleSeed ?? CreateShuffleSeed();
                _playOrder = BuildPlayOrder(_tracks.Length, _shuffle, _shuffleSeed, _currentIndex);
                InvalidatePendingAndAdvanceGeneration();
            }
        }

        public void UpdateRepeatMode(int repeatMode)
        {
            lock (_gate)
            {
                repeatMode = NormalizeRepeatMode(repeatMode);
                if (_repeatMode == repeatMode)
                    return;

                _repeatMode = repeatMode;
                InvalidatePendingAndAdvanceGeneration();
            }
        }

        public long RebaseForReconnect(long sessionGeneration)
        {
            lock (_gate)
            {
                _sessionGeneration = sessionGeneration;
                _currentPlayRequestId = 0;
                _awaitingInitialConfirmation = _currentIndex >= 0;
                _allowBoundaryBeforeInitialConfirmation = _currentIndex >= 0;
                _lastInternalQueueFailure = null;
                InvalidatePendingAndAdvanceGeneration();
                return _generationId;
            }
        }

        public ApplicationQueueTransitionResult CancelPendingTransition(
            string reason,
            DateTimeOffset cancelledAtUtc)
        {
            lock (_gate)
            {
                var pending = _pending;
                _pending = null;
                if (pending == null || !pending.EndObserved)
                    return null;
                _lastInternalQueueFailure = null;
                return CreateResult(
                    pending,
                    null,
                    AppendReason(pending.FailureReason, reason),
                    cancelledAtUtc);
            }
        }

        public void RecordInternalQueueFailure(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return;

            lock (_gate)
                _lastInternalQueueFailure = reason;
        }

        public ApplicationQueueTransition ObservePreloadRequested(
            string currentTrackUri,
            ulong playRequestId,
            long sessionGeneration,
            Func<string, bool> isPlayable = null)
        {
            lock (_gate)
            {
                if (!MatchesCurrentIdentity(currentTrackUri, playRequestId, sessionGeneration))
                    return InvalidTransition("stale-preload-request");

                var expectedIndex = FindRelativeIndex(1, isPlayable);
                _pending = CreateTransition(expectedIndex, playRequestId, sessionGeneration, false, DateTimeOffset.MinValue);
                return Clone(_pending);
            }
        }

        public bool ObservePreloaded(string preloadedUri, long sessionGeneration)
        {
            lock (_gate)
            {
                if (_pending == null ||
                    _pending.QueueGenerationId != _generationId ||
                    (_pending.SessionGeneration != 0 && sessionGeneration != 0 &&
                     _pending.SessionGeneration != sessionGeneration))
                {
                    return false;
                }

                _pending.PreloadedUri = preloadedUri;
                return string.Equals(_pending.ExpectedUri, preloadedUri, StringComparison.OrdinalIgnoreCase);
            }
        }

        public ApplicationQueueTransition BeginEndOfTrack(
            string endedTrackUri,
            ulong playRequestId,
            long sessionGeneration,
            DateTimeOffset startedAtUtc,
            Func<string, bool> isPlayable = null)
        {
            lock (_gate)
            {
                if (!MatchesCurrentIdentity(endedTrackUri, playRequestId, sessionGeneration))
                    return InvalidTransition("stale-end-of-track");

                if (_pending != null &&
                    _pending.QueueGenerationId == _generationId &&
                    _pending.IsUserInitiated)
                {
                    return InvalidTransition("end-of-track-superseded-by-user-command");
                }

                var expectedIndex = FindRelativeIndex(1, isPlayable);
                if (_pending == null ||
                    _pending.QueueGenerationId != _generationId ||
                    _pending.FromIndex != _currentIndex ||
                    _pending.ExpectedIndex != expectedIndex)
                {
                    _pending = CreateTransition(expectedIndex, playRequestId, sessionGeneration, true, startedAtUtc);
                }
                else
                {
                    _pending.EndObserved = true;
                    _pending.StartedAtUtc = startedAtUtc;
                    _pending.FromPlayRequestId = playRequestId;
                    _pending.SessionGeneration = sessionGeneration;
                }

                if (_pending.ExpectedIndex < 0)
                    _pending.FailureReason = "end-of-queue";

                return Clone(_pending);
            }
        }

        public ApplicationQueueTransition BeginManualMove(
            int delta,
            ulong playRequestId,
            long sessionGeneration,
            Func<string, bool> isPlayable = null)
        {
            lock (_gate)
            {
                var expectedIndex = FindRelativeIndex(delta, isPlayable);
                _pending = CreateTransition(expectedIndex, playRequestId, sessionGeneration, false, DateTimeOffset.MinValue);
                _pending.IsUserInitiated = true;
                return Clone(_pending);
            }
        }

        public ApplicationQueueTransitionResult ObserveTrackChanged(
            string trackUri,
            ulong playRequestId,
            long sessionGeneration,
            DateTimeOffset observedAtUtc)
        {
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(trackUri))
                    return null;

                if (_sessionGeneration != 0 && sessionGeneration != 0 && sessionGeneration != _sessionGeneration)
                    return null;

                if (_awaitingInitialConfirmation && _pending == null)
                {
                    var initialIndex = FindInitialOccurrence(trackUri);
                    if (initialIndex < 0)
                        return null;

                    _currentIndex = initialIndex;
                    if (_shuffle)
                        _playOrder = RotateToFirst(_playOrder, _currentIndex);
                    _currentPlayRequestId = playRequestId;
                    _sessionGeneration = sessionGeneration;
                    _awaitingInitialConfirmation = false;
                    _allowBoundaryBeforeInitialConfirmation = false;
                    _pending = null;
                    _lastInternalQueueFailure = null;
                    return null;
                }

                var pending = _pending;
                if (pending != null && pending.QueueGenerationId == _generationId)
                {
                    var actualIndex = FindTransitionOccurrence(trackUri, pending);
                    if (actualIndex >= 0)
                        _currentIndex = actualIndex;

                    _currentPlayRequestId = playRequestId;
                    _sessionGeneration = sessionGeneration;
                    _awaitingInitialConfirmation = false;
                    _allowBoundaryBeforeInitialConfirmation = false;
                    _pending = null;
                    _lastInternalQueueFailure = null;

                    if (!pending.EndObserved)
                        return null;

                    var failureReason = pending.FailureReason;
                    if (actualIndex < 0)
                        failureReason = AppendReason(failureReason, "actual-track-outside-application-queue");
                    else if (pending.ExpectedIndex >= 0 && actualIndex != pending.ExpectedIndex)
                        failureReason = AppendReason(failureReason, "unexpected-track-changed");

                    return CreateResult(pending, trackUri, failureReason, observedAtUtc);
                }

                if (_currentIndex >= 0 &&
                    string.Equals(_tracks[_currentIndex], trackUri, StringComparison.OrdinalIgnoreCase))
                {
                    _currentPlayRequestId = playRequestId;
                    _sessionGeneration = sessionGeneration;
                }

                return null;
            }
        }

        public bool TryClaimFallback(
            long queueGenerationId,
            long transitionId,
            out ApplicationQueueTransition transition)
        {
            lock (_gate)
            {
                if (_pending == null ||
                    !_pending.EndObserved ||
                    _pending.ExpectedIndex < 0 ||
                    _pending.QueueGenerationId != queueGenerationId ||
                    _pending.TransitionId != transitionId ||
                    _pending.FallbackUsed)
                {
                    transition = null;
                    return false;
                }

                _pending.FallbackUsed = true;
                if (string.IsNullOrWhiteSpace(_pending.FailureReason))
                    _pending.FailureReason = DetermineFallbackReason(_pending);
                _lastInternalQueueFailure = null;
                transition = Clone(_pending);
                return true;
            }
        }

        public ApplicationQueueTransition MarkExpectedUnavailable(
            long queueGenerationId,
            long transitionId,
            string unavailableUri,
            Func<string, bool> isPlayable = null)
        {
            lock (_gate)
            {
                if (_pending == null ||
                    _pending.QueueGenerationId != queueGenerationId ||
                    _pending.TransitionId != transitionId ||
                    !string.Equals(_pending.ExpectedUri, unavailableUri, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (_pending.ExpectedIndex >= 0)
                    _unavailableIndexes.Add(_pending.ExpectedIndex);

                _pending.FailureReason = AppendReason(_pending.FailureReason, "expected-track-unavailable");
                _pending.ExpectedIndex = FindRelativeIndex(1, isPlayable);
                _pending.ExpectedUri = UriAt(_pending.ExpectedIndex);
                _pending.PreloadedUri = null;
                _pending.FallbackUsed = false;
                return Clone(_pending);
            }
        }

        public ApplicationQueueTransition MarkPendingExpectedUnavailable(
            string unavailableUri,
            long sessionGeneration,
            Func<string, bool> isPlayable = null)
        {
            lock (_gate)
            {
                if (_pending == null ||
                    !_pending.EndObserved ||
                    (_pending.SessionGeneration != 0 && sessionGeneration != 0 &&
                     _pending.SessionGeneration != sessionGeneration))
                {
                    return null;
                }

                return MarkExpectedUnavailableCore(unavailableUri, isPlayable);
            }
        }

        public ApplicationQueueTransition RetargetPendingExpected(string expectedUri, string reason)
        {
            lock (_gate)
            {
                if (_pending == null || !_pending.EndObserved || string.IsNullOrWhiteSpace(expectedUri))
                    return null;

                var expectedIndex = FindTransitionOccurrence(expectedUri, _pending);
                if (expectedIndex < 0)
                    return null;

                _pending.ExpectedIndex = expectedIndex;
                _pending.ExpectedUri = _tracks[expectedIndex];
                if (string.Equals(_pending.FailureReason, "end-of-queue", StringComparison.OrdinalIgnoreCase))
                    _pending.FailureReason = null;
                _pending.FailureReason = AppendReason(_pending.FailureReason, reason);
                return Clone(_pending);
            }
        }

        private ApplicationQueueTransition MarkExpectedUnavailableCore(
            string unavailableUri,
            Func<string, bool> isPlayable)
        {
            if (_pending == null ||
                !string.Equals(_pending.ExpectedUri, unavailableUri, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (_pending.ExpectedIndex >= 0)
                _unavailableIndexes.Add(_pending.ExpectedIndex);

            _pending.FailureReason = AppendReason(_pending.FailureReason, "expected-track-unavailable");
            _pending.ExpectedIndex = FindRelativeIndex(1, isPlayable);
            _pending.ExpectedUri = UriAt(_pending.ExpectedIndex);
            _pending.PreloadedUri = null;
            _pending.FallbackUsed = false;
            return Clone(_pending);
        }

        public ApplicationQueueTransitionResult CompleteWithoutTrack(
            long queueGenerationId,
            long transitionId,
            DateTimeOffset completedAtUtc)
        {
            lock (_gate)
            {
                if (_pending == null ||
                    _pending.QueueGenerationId != queueGenerationId ||
                    _pending.TransitionId != transitionId)
                {
                    return null;
                }

                var pending = _pending;
                _pending = null;
                _lastInternalQueueFailure = null;
                return CreateResult(pending, null, pending.FailureReason, completedAtUtc);
            }
        }

        private ApplicationQueueTransition CreateTransition(
            int expectedIndex,
            ulong playRequestId,
            long sessionGeneration,
            bool endObserved,
            DateTimeOffset startedAtUtc)
        {
            return new ApplicationQueueTransition
            {
                QueueGenerationId = _generationId,
                TransitionId = ++_nextTransitionId,
                FromIndex = _currentIndex,
                ExpectedIndex = expectedIndex,
                ExpectedUri = UriAt(expectedIndex),
                FromPlayRequestId = playRequestId,
                SessionGeneration = sessionGeneration,
                StartedAtUtc = startedAtUtc,
                EndObserved = endObserved,
                IsValid = true
            };
        }

        private ApplicationQueueTransition InvalidTransition(string reason)
        {
            return new ApplicationQueueTransition
            {
                QueueGenerationId = _generationId,
                TransitionId = 0,
                FromIndex = _currentIndex,
                ExpectedIndex = -1,
                IsValid = false,
                FailureReason = reason
            };
        }

        private bool MatchesCurrentIdentity(string trackUri, ulong playRequestId, long sessionGeneration)
        {
            if (_awaitingInitialConfirmation && !_allowBoundaryBeforeInitialConfirmation)
                return false;
            if (_currentIndex < 0 || _currentIndex >= _tracks.Length)
                return false;
            if (!string.Equals(_tracks[_currentIndex], trackUri, StringComparison.OrdinalIgnoreCase))
                return false;
            if (_sessionGeneration != 0 && sessionGeneration != 0 && _sessionGeneration != sessionGeneration)
                return false;
            if (_currentPlayRequestId != 0 && playRequestId != 0 && _currentPlayRequestId != playRequestId)
                return false;
            return true;
        }

        private int FindRelativeIndex(int delta, Func<string, bool> isPlayable)
        {
            if (delta == 0 || _currentIndex < 0 || _playOrder.Length == 0)
                return -1;

            if (_repeatMode == 2 && delta > 0)
                return IsPlayableIndex(_currentIndex, isPlayable) ? _currentIndex : -1;

            var currentOrderPosition = Array.IndexOf(_playOrder, _currentIndex);
            if (currentOrderPosition < 0)
                return -1;

            var step = delta > 0 ? 1 : -1;
            var position = currentOrderPosition;
            for (var visited = 0; visited < _playOrder.Length; visited++)
            {
                position += step;
                if (position < 0 || position >= _playOrder.Length)
                {
                    if (_repeatMode != 1)
                        return -1;
                    position = position < 0 ? _playOrder.Length - 1 : 0;
                }

                var candidate = _playOrder[position];
                if (IsPlayableIndex(candidate, isPlayable))
                    return candidate;
            }

            return -1;
        }

        private bool IsPlayableIndex(int index, Func<string, bool> isPlayable)
        {
            return index >= 0 &&
                index < _tracks.Length &&
                !_unavailableIndexes.Contains(index) &&
                (isPlayable == null || isPlayable(_tracks[index]));
        }

        private int FindInitialOccurrence(string uri)
        {
            if (_currentIndex >= 0 && _currentIndex < _tracks.Length &&
                string.Equals(_tracks[_currentIndex], uri, StringComparison.OrdinalIgnoreCase))
            {
                return _currentIndex;
            }

            foreach (var index in _playOrder)
            {
                if (string.Equals(_tracks[index], uri, StringComparison.OrdinalIgnoreCase))
                    return index;
            }

            return -1;
        }

        private int FindTransitionOccurrence(string uri, ApplicationQueueTransition transition)
        {
            if (transition.ExpectedIndex >= 0 &&
                string.Equals(_tracks[transition.ExpectedIndex], uri, StringComparison.OrdinalIgnoreCase))
            {
                return transition.ExpectedIndex;
            }

            var fromPosition = Array.IndexOf(_playOrder, transition.FromIndex);
            if (fromPosition < 0)
                return -1;

            for (var offset = 1; offset <= _playOrder.Length; offset++)
            {
                var position = fromPosition + offset;
                if (position >= _playOrder.Length)
                    position -= _playOrder.Length;
                var candidate = _playOrder[position];
                if (string.Equals(_tracks[candidate], uri, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            return -1;
        }

        private string DetermineFallbackReason(ApplicationQueueTransition transition)
        {
            if (!string.IsNullOrWhiteSpace(_lastInternalQueueFailure))
                return _lastInternalQueueFailure;
            if (!string.IsNullOrWhiteSpace(transition.PreloadedUri) &&
                string.Equals(transition.PreloadedUri, transition.ExpectedUri, StringComparison.OrdinalIgnoreCase))
            {
                return "preloaded-track-did-not-handoff";
            }
            if (!string.IsNullOrWhiteSpace(transition.PreloadedUri))
                return "preloaded-uri-mismatch";
            return "internal-queue-empty-or-context-unavailable";
        }

        private static ApplicationQueueTransitionResult CreateResult(
            ApplicationQueueTransition transition,
            string actualUri,
            string failureReason,
            DateTimeOffset completedAtUtc)
        {
            var elapsed = transition.StartedAtUtc == DateTimeOffset.MinValue
                ? 0
                : Math.Max(0, (long)(completedAtUtc - transition.StartedAtUtc).TotalMilliseconds);
            return new ApplicationQueueTransitionResult
            {
                ExpectedNextUri = transition.ExpectedUri,
                PreloadedUri = transition.PreloadedUri,
                ActualChangedUri = actualUri,
                FallbackUsed = transition.FallbackUsed,
                InternalQueueFailureReason = failureReason,
                ElapsedTransitionMilliseconds = elapsed,
                QueueGenerationId = transition.QueueGenerationId,
                TransitionId = transition.TransitionId
            };
        }

        private void InvalidatePendingAndAdvanceGeneration()
        {
            _pending = null;
            _lastInternalQueueFailure = null;
            _generationId++;
        }

        private string UriAt(int index)
        {
            return index >= 0 && index < _tracks.Length ? _tracks[index] : null;
        }

        private static int ResolveStartIndex(string[] tracks, string startUri, int requestedIndex)
        {
            if (requestedIndex >= 0 && requestedIndex < tracks.Length &&
                (string.IsNullOrWhiteSpace(startUri) ||
                 string.Equals(tracks[requestedIndex], startUri, StringComparison.OrdinalIgnoreCase)))
            {
                return requestedIndex;
            }

            if (!string.IsNullOrWhiteSpace(startUri))
            {
                for (var index = 0; index < tracks.Length; index++)
                {
                    if (string.Equals(tracks[index], startUri, StringComparison.OrdinalIgnoreCase))
                        return index;
                }
            }

            return tracks.Length > 0 ? 0 : -1;
        }

        private static int[] BuildPlayOrder(int count, bool shuffle, long seed, int currentIndex)
        {
            var order = Enumerable.Range(0, count).ToArray();
            if (!shuffle || count < 2)
                return order;

            var random = new Random(unchecked((int)(seed ^ (seed >> 32))));
            for (var index = order.Length - 1; index > 0; index--)
            {
                var swapIndex = random.Next(index + 1);
                var value = order[index];
                order[index] = order[swapIndex];
                order[swapIndex] = value;
            }
            return RotateToFirst(order, currentIndex);
        }

        private static int[] RotateToFirst(int[] order, int currentIndex)
        {
            if (order == null || order.Length < 2 || currentIndex < 0)
                return order;
            var position = Array.IndexOf(order, currentIndex);
            if (position <= 0)
                return order;

            var rotated = new int[order.Length];
            Array.Copy(order, position, rotated, 0, order.Length - position);
            Array.Copy(order, 0, rotated, order.Length - position, position);
            return rotated;
        }

        private static int NormalizeRepeatMode(int repeatMode)
        {
            return repeatMode < 0 || repeatMode > 2 ? 0 : repeatMode;
        }

        private static long CreateShuffleSeed()
        {
            return DateTime.UtcNow.Ticks ^ Environment.TickCount;
        }

        private static string AppendReason(string current, string next)
        {
            if (string.IsNullOrWhiteSpace(current))
                return next;
            if (string.IsNullOrWhiteSpace(next) || current.IndexOf(next, StringComparison.OrdinalIgnoreCase) >= 0)
                return current;
            return current + ";" + next;
        }

        private static ApplicationQueueTransition Clone(ApplicationQueueTransition source)
        {
            if (source == null)
                return null;
            return new ApplicationQueueTransition
            {
                QueueGenerationId = source.QueueGenerationId,
                TransitionId = source.TransitionId,
                FromIndex = source.FromIndex,
                ExpectedIndex = source.ExpectedIndex,
                ExpectedUri = source.ExpectedUri,
                PreloadedUri = source.PreloadedUri,
                FromPlayRequestId = source.FromPlayRequestId,
                SessionGeneration = source.SessionGeneration,
                StartedAtUtc = source.StartedAtUtc,
                EndObserved = source.EndObserved,
                FallbackUsed = source.FallbackUsed,
                IsUserInitiated = source.IsUserInitiated,
                IsValid = source.IsValid,
                FailureReason = source.FailureReason
            };
        }
    }
}
