using System;

namespace LibreSpotUWP.Services
{
    internal struct AudioProducerState
    {
        public ulong Generation;
        public ulong GenerationStartSequence;
        public ulong WriteSequence;
    }

    internal struct AudioTransitionEvaluation
    {
        public long TransitionId;
        public bool ShouldGate;
        public bool ShouldDiscard;
        public ulong DiscardSequence;
        public bool ShouldResume;
        public ulong ActiveGeneration;
        public ulong ActiveStartSequence;
        public ulong ActiveBoundarySequence;
        public ulong AvailableBytes;
    }

    /// <summary>
    /// Pure transition policy shared by the UWP ring player and its simulated
    /// quantum tests. The class itself is used only from the serialized control
    /// path; the AudioGraph callback consumes atomically published primitives.
    /// </summary>
    internal sealed class AudioTransitionStateMachine
    {
        public const ulong NoBoundary = ulong.MaxValue;

        private readonly ulong _preRollBytes;
        private long _transitionId;
        private ulong _activeGeneration;
        private ulong _activePlayRequestId;
        private ulong _pendingGeneration;
        private ulong _pendingStartSequence;
        private ulong _pendingPlayRequestId;
        private bool _hasPendingTransition;
        private bool _preserveCurrent;
        private bool _desiredPlaying;
        private bool _discarded;
        private bool _canceled;

        public AudioTransitionStateMachine(ulong preRollBytes)
        {
            if (preRollBytes == 0)
                throw new ArgumentOutOfRangeException(nameof(preRollBytes));

            _preRollBytes = preRollBytes;
        }

        public long CurrentTransitionId => _transitionId;
        public ulong ActiveGeneration => _activeGeneration;
        public ulong ActivePlayRequestId => _activePlayRequestId;
        public ulong PendingGeneration => _pendingGeneration;
        public ulong PendingPlayRequestId => _pendingPlayRequestId;
        public bool HasPendingTransition => _hasPendingTransition;
        public bool DesiredPlaying => _desiredPlaying;

        public long BeginTransition(bool preserveCurrent, bool desiredPlaying)
        {
            _transitionId++;
            _hasPendingTransition = true;
            _preserveCurrent = preserveCurrent && _activeGeneration != 0;
            _desiredPlaying = desiredPlaying;
            _pendingGeneration = 0;
            _pendingStartSequence = 0;
            _pendingPlayRequestId = 0;
            _discarded = false;
            _canceled = false;
            return _transitionId;
        }

        public long BeginAutomaticTransition(bool desiredPlaying)
        {
            if (_hasPendingTransition && _preserveCurrent)
            {
                _desiredPlaying = desiredPlaying;
                return _transitionId;
            }

            return BeginTransition(true, desiredPlaying);
        }

        public bool ObserveLoading(ulong playRequestId)
        {
            if (playRequestId != 0 &&
                _pendingPlayRequestId == 0 &&
                _activePlayRequestId != 0 &&
                playRequestId <= _activePlayRequestId)
            {
                return false;
            }

            if (!_hasPendingTransition)
            {
                BeginTransition(false, true);
            }

            if (playRequestId == 0 || playRequestId == _pendingPlayRequestId)
                return true;

            if (_pendingPlayRequestId != 0 && playRequestId < _pendingPlayRequestId)
                return false;

            // A newer loading request supersedes a rapidly cancelled one. Its
            // eventual TrackChanged event is the only marker allowed to bind.
            _pendingPlayRequestId = playRequestId;
            _pendingGeneration = 0;
            _pendingStartSequence = 0;
            _discarded = false;
            return true;
        }

        public bool ObserveTrack(ulong playRequestId, ulong generation, ulong startSequence)
        {
            if (generation == 0 || generation <= _activeGeneration)
                return false;

            if (!_hasPendingTransition)
            {
                if (!_desiredPlaying)
                    return false;
                BeginTransition(true, true);
            }

            if (_pendingPlayRequestId != 0 &&
                playRequestId != 0 &&
                playRequestId != _pendingPlayRequestId)
            {
                return false;
            }

            if (_pendingGeneration != 0 && generation < _pendingGeneration)
                return false;

            _pendingPlayRequestId = playRequestId;
            _pendingGeneration = generation;
            _pendingStartSequence = startSequence;
            return true;
        }

        public bool ObserveSeek(ulong playRequestId, ulong generation, ulong startSequence)
        {
            if (!_hasPendingTransition || generation == 0 || generation <= _activeGeneration)
                return false;

            if (_pendingPlayRequestId != 0 &&
                playRequestId != 0 &&
                playRequestId != _pendingPlayRequestId)
            {
                return false;
            }

            _pendingPlayRequestId = playRequestId;
            _pendingGeneration = generation;
            _pendingStartSequence = startSequence;
            return true;
        }

        public bool RequestPlayback(ulong playRequestId, ulong generation)
        {
            if (_canceled)
                return false;

            if (_hasPendingTransition)
            {
                if (_pendingPlayRequestId != 0 &&
                    playRequestId != 0 &&
                    playRequestId != _pendingPlayRequestId)
                {
                    return false;
                }

                if (_pendingGeneration != 0 && generation != 0 && generation != _pendingGeneration)
                    return false;

                if (_pendingPlayRequestId == 0)
                    _pendingPlayRequestId = playRequestId;
            }
            else if (generation != 0 && _activeGeneration != 0 && generation != _activeGeneration)
            {
                return false;
            }

            _desiredPlaying = true;
            return true;
        }

        public void Pause()
        {
            _desiredPlaying = false;
        }

        public void Cancel()
        {
            _transitionId++;
            _hasPendingTransition = false;
            _desiredPlaying = false;
            _pendingGeneration = 0;
            _pendingStartSequence = 0;
            _pendingPlayRequestId = 0;
            _discarded = false;
            _canceled = true;
        }

        public bool IsPendingEvent(ulong playRequestId, ulong generation)
        {
            if (!_hasPendingTransition)
                return false;

            if (_pendingPlayRequestId != 0 &&
                playRequestId != 0 &&
                playRequestId != _pendingPlayRequestId)
            {
                return false;
            }

            if (_pendingGeneration != 0 && generation != 0 && generation != _pendingGeneration)
                return false;

            return true;
        }

        public bool IsActiveEvent(ulong playRequestId, ulong generation)
        {
            if (_activeGeneration == 0)
                return playRequestId == 0 && generation == 0;

            if (generation != 0 && generation != _activeGeneration)
                return false;

            if (_activePlayRequestId != 0 &&
                playRequestId != 0 &&
                playRequestId != _activePlayRequestId)
            {
                return false;
            }

            return true;
        }

        public AudioTransitionEvaluation Evaluate(AudioProducerState producer, ulong readSequence)
        {
            var result = new AudioTransitionEvaluation
            {
                TransitionId = _transitionId,
                ShouldGate = !_desiredPlaying,
                ActiveGeneration = _activeGeneration,
                ActiveBoundarySequence = NoBoundary
            };

            if (_hasPendingTransition)
            {
                if (_pendingGeneration == 0 || producer.Generation != _pendingGeneration)
                {
                    result.ShouldGate = !_preserveCurrent;
                    return result;
                }

                result.ShouldGate = !_preserveCurrent || readSequence >= _pendingStartSequence;

                if (!_preserveCurrent && !_discarded)
                {
                    _discarded = true;
                    result.ShouldDiscard = true;
                    result.DiscardSequence = _pendingStartSequence;
                    readSequence = _pendingStartSequence;
                }

                if (_preserveCurrent && readSequence < _pendingStartSequence)
                {
                    result.ShouldGate = false;
                    result.ActiveBoundarySequence = _pendingStartSequence;
                    return result;
                }

                result.AvailableBytes = producer.WriteSequence > _pendingStartSequence
                    ? producer.WriteSequence - _pendingStartSequence
                    : 0;

                if (_desiredPlaying && result.AvailableBytes >= _preRollBytes)
                {
                    _activeGeneration = _pendingGeneration;
                    _activePlayRequestId = _pendingPlayRequestId;
                    _hasPendingTransition = false;
                    _preserveCurrent = false;
                    _discarded = false;

                    result.ShouldGate = false;
                    result.ShouldResume = true;
                    result.ActiveGeneration = _activeGeneration;
                    result.ActiveStartSequence = _pendingStartSequence;
                    result.ActiveBoundarySequence = NoBoundary;
                }

                return result;
            }

            if (_desiredPlaying && _activeGeneration != 0)
            {
                result.AvailableBytes = producer.WriteSequence > readSequence
                    ? producer.WriteSequence - readSequence
                    : 0;
                if (producer.Generation == _activeGeneration && result.AvailableBytes >= _preRollBytes)
                {
                    result.ShouldGate = false;
                    result.ShouldResume = true;
                    result.ActiveStartSequence = readSequence;
                }
            }

            return result;
        }
    }
}
