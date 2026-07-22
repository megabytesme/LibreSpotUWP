using System;
using System.Threading;
using System.Threading.Tasks;

namespace LibreSpotUWP.Services
{
    internal sealed class AsyncOperationOnce
    {
        private readonly object _sync = new object();
        private Task _operation;

        public Task Run(Func<Task> operationFactory)
        {
            if (operationFactory == null)
                throw new ArgumentNullException(nameof(operationFactory));

            lock (_sync)
            {
                if (_operation == null)
                    _operation = operationFactory();

                return _operation;
            }
        }
    }

    internal sealed class CallbackLifetimeGate : IDisposable
    {
        private const int DisposingFlag = int.MinValue;
        private const int CallbackCountMask = int.MaxValue;

        private readonly ManualResetEventSlim _drained = new ManualResetEventSlim(false);
        private int _lifetimeState;

        public int CallbacksInFlight => Volatile.Read(ref _lifetimeState) & CallbackCountMask;
        public bool IsDisposing => Volatile.Read(ref _lifetimeState) < 0;

        public bool TryEnter()
        {
            while (true)
            {
                int state = Volatile.Read(ref _lifetimeState);
                if (state < 0)
                    return false;
                if (state == CallbackCountMask)
                    throw new InvalidOperationException("Too many callbacks are in flight.");

                if (Interlocked.CompareExchange(ref _lifetimeState, state + 1, state) == state)
                    return true;
            }
        }

        public void Exit()
        {
            int state = Interlocked.Decrement(ref _lifetimeState);
            if (state == DisposingFlag)
                _drained.Set();
        }

        public bool BeginDisposeAndWait(TimeSpan timeout)
        {
            while (true)
            {
                int state = Volatile.Read(ref _lifetimeState);
                if (state < 0)
                    break;

                int disposingState = state | DisposingFlag;
                if (Interlocked.CompareExchange(ref _lifetimeState, disposingState, state) != state)
                    continue;

                if (state == 0)
                    return true;
                break;
            }

            return _drained.Wait(timeout);
        }

        public void Dispose()
        {
            _drained.Dispose();
        }
    }

    internal sealed class RecoveryOperationGate : IDisposable
    {
        private readonly object _sync = new object();
        private Task _activeOperation;
        private CancellationTokenSource _activeCancellation;
        private int _disposed;

        public bool IsRunning
        {
            get
            {
                lock (_sync)
                    return _activeOperation != null && !_activeOperation.IsCompleted;
            }
        }

        public Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken lifetimeToken)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            lock (_sync)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return Task.FromCanceled(new CancellationToken(true));

                if (_activeOperation != null && !_activeOperation.IsCompleted)
                    return _activeOperation;

                _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
                _activeOperation = RunCoreAsync(operation, _activeCancellation);
                return _activeOperation;
            }
        }

        public void CancelActive()
        {
            CancellationTokenSource cancellation;
            lock (_sync)
                cancellation = _activeCancellation;

            try
            {
                cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task RunCoreAsync(
            Func<CancellationToken, Task> operation,
            CancellationTokenSource cancellation)
        {
            try
            {
                await operation(cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_activeCancellation, cancellation))
                    {
                        _activeCancellation = null;
                        _activeOperation = null;
                    }
                }

                cancellation.Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            CancelActive();
        }
    }

    internal struct ProducerHealthSnapshot
    {
        public bool PlaybackExpected;
        public bool SessionConnected;
        public bool FatalTransportFailure;
        public long SessionGeneration;
        public ulong TrackGeneration;
        public ulong LastWriteSequence;
        public long ExpectedSinceMs;
        public long LastPcmWriteMs;
        public long LastSuccessfulStreamReadMs;
        public string FatalReason;
    }

    internal struct ProducerHealthDecision
    {
        public bool ShouldRecover;
        public string Reason;
        public ProducerHealthSnapshot Snapshot;
    }

    internal sealed class ProducerHealthMonitor
    {
        private readonly object _sync = new object();
        private ProducerHealthSnapshot _state;
        private bool _stallSignalled;

        public ProducerHealthSnapshot Snapshot
        {
            get
            {
                lock (_sync)
                    return _state;
            }
        }

        public void SetPlaybackExpected(
            bool expected,
            long sessionGeneration,
            ulong trackGeneration,
            ulong writeSequence,
            long nowMs)
        {
            lock (_sync)
            {
                bool identityChanged = sessionGeneration != _state.SessionGeneration ||
                    (trackGeneration != 0 && trackGeneration != _state.TrackGeneration);

                _state.PlaybackExpected = expected;
                _state.SessionGeneration = sessionGeneration;
                if (trackGeneration != 0)
                    _state.TrackGeneration = trackGeneration;

                if (!expected)
                {
                    _stallSignalled = false;
                    _state.ExpectedSinceMs = 0;
                    _state.FatalTransportFailure = false;
                    _state.FatalReason = null;
                    return;
                }

                if (identityChanged || _state.ExpectedSinceMs == 0)
                {
                    _state.ExpectedSinceMs = nowMs;
                    _state.LastWriteSequence = writeSequence;
                    _state.LastPcmWriteMs = 0;
                    _state.LastSuccessfulStreamReadMs = 0;
                    _state.FatalTransportFailure = false;
                    _state.FatalReason = null;
                    _stallSignalled = false;
                }
            }
        }

        public bool SetSessionState(bool connected, long sessionGeneration, long nowMs)
        {
            lock (_sync)
            {
                if (sessionGeneration < _state.SessionGeneration)
                    return false;

                if (sessionGeneration != _state.SessionGeneration)
                {
                    _state.SessionGeneration = sessionGeneration;
                    _state.ExpectedSinceMs = nowMs;
                    _state.LastPcmWriteMs = 0;
                    _state.LastSuccessfulStreamReadMs = 0;
                    _state.FatalTransportFailure = false;
                    _state.FatalReason = null;
                    _stallSignalled = false;
                }

                _state.SessionConnected = connected;
                if (!connected && _state.PlaybackExpected)
                {
                    _state.FatalTransportFailure = true;
                    _state.FatalReason = "session-disconnected";
                }
                else if (connected &&
                    _state.FatalTransportFailure &&
                    string.Equals(_state.FatalReason, "session-disconnected", StringComparison.Ordinal))
                {
                    _state.FatalTransportFailure = false;
                    _state.FatalReason = null;
                    _stallSignalled = false;
                }

                return true;
            }
        }

        public bool ReportFatal(long sessionGeneration, string reason)
        {
            lock (_sync)
            {
                if (sessionGeneration != _state.SessionGeneration)
                    return false;

                _state.FatalTransportFailure = true;
                _state.FatalReason = string.IsNullOrWhiteSpace(reason) ? "transport-failure" : reason;
                return true;
            }
        }

        public void ObserveProducer(ulong writeSequence, long nowMs)
        {
            lock (_sync)
            {
                if (writeSequence == _state.LastWriteSequence)
                    return;

                _state.LastWriteSequence = writeSequence;
                _state.LastPcmWriteMs = nowMs;
                // Decoded PCM can only advance after a successful source-stream
                // read. Keep this independently named timestamp so a future native
                // stream-read counter can replace the inference without changing
                // the watchdog contract.
                _state.LastSuccessfulStreamReadMs = nowMs;
                if (!_state.FatalTransportFailure && _state.SessionConnected)
                    _stallSignalled = false;
            }
        }

        public bool IsDecisionCurrent(ProducerHealthSnapshot decisionSnapshot)
        {
            lock (_sync)
            {
                if (!_state.PlaybackExpected ||
                    _state.SessionGeneration != decisionSnapshot.SessionGeneration ||
                    _state.TrackGeneration != decisionSnapshot.TrackGeneration ||
                    _state.SessionConnected != decisionSnapshot.SessionConnected ||
                    _state.FatalTransportFailure != decisionSnapshot.FatalTransportFailure ||
                    !string.Equals(_state.FatalReason, decisionSnapshot.FatalReason, StringComparison.Ordinal))
                {
                    return false;
                }

                return _state.FatalTransportFailure ||
                    _state.LastWriteSequence == decisionSnapshot.LastWriteSequence;
            }
        }

        public void SetTrackGeneration(ulong trackGeneration, ulong writeSequence, long nowMs)
        {
            lock (_sync)
            {
                if (trackGeneration == 0 || trackGeneration == _state.TrackGeneration)
                    return;

                _state.TrackGeneration = trackGeneration;
                _state.ExpectedSinceMs = nowMs;
                _state.LastWriteSequence = writeSequence;
                _state.LastPcmWriteMs = 0;
                _state.LastSuccessfulStreamReadMs = 0;
                _stallSignalled = false;
            }
        }

        public ProducerHealthDecision Evaluate(
            long nowMs,
            ulong availableBytes,
            ulong lowBufferThresholdBytes,
            long stallIntervalMs)
        {
            lock (_sync)
            {
                var decision = new ProducerHealthDecision { Snapshot = _state };
                if (!_state.PlaybackExpected || _stallSignalled || availableBytes > lowBufferThresholdBytes)
                    return decision;

                long lastProgressMs = Math.Max(_state.ExpectedSinceMs, _state.LastPcmWriteMs);
                bool noPcmForInterval = nowMs - lastProgressMs >= stallIntervalMs;
                if (!_state.FatalTransportFailure && _state.SessionConnected && !noPcmForInterval)
                    return decision;

                _stallSignalled = true;
                decision.ShouldRecover = true;
                decision.Reason = _state.FatalTransportFailure
                    ? _state.FatalReason ?? "transport-failure"
                    : !_state.SessionConnected
                        ? "session-disconnected"
                        : "pcm-stalled";
                decision.Snapshot = _state;
                return decision;
            }
        }
    }

    internal static class BoundedRecoveryBackoff
    {
        public static int GetDelayMilliseconds(int attempt)
        {
            if (attempt <= 1)
                return 0;

            int exponent = Math.Min(2, attempt - 2);
            return 1000 << exponent;
        }
    }
}
