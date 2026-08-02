using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LibreSpotUWP.Services
{
    public static class SessionReadinessWaiter
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

        public static async Task WaitAsync(
            Func<bool> isReady,
            Func<bool> isValid,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (isReady == null)
                throw new ArgumentNullException(nameof(isReady));
            if (isValid == null)
                throw new ArgumentNullException(nameof(isValid));
            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout));

            var deadline = DateTimeOffset.UtcNow + timeout;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!isValid())
                    throw new InvalidOperationException("The native Spotify session is no longer available.");
                if (isReady())
                    return;

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException("Timed out waiting for the native Spotify session to authenticate.");

                await Task.Delay(
                    remaining < PollInterval ? remaining : PollInterval,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static class CacheFreshness
    {
        public static bool IsStale(DateTimeOffset timestamp, TimeSpan ttl, DateTimeOffset now)
        {
            if (ttl == TimeSpan.MaxValue)
                return false;

            if (ttl <= TimeSpan.Zero)
                return true;

            return now - timestamp >= ttl;
        }
    }

    public sealed class BoundedAsyncGate : IDisposable
    {
        private readonly SemaphoreSlim _gate;
        private int _activeCount;
        private int _maximumObserved;

        public BoundedAsyncGate(int maximumConcurrency)
        {
            if (maximumConcurrency < 1)
                throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));

            MaximumConcurrency = maximumConcurrency;
            _gate = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
        }

        public int MaximumConcurrency { get; }
        public int ActiveCount => Volatile.Read(ref _activeCount);
        public int MaximumObserved => Volatile.Read(ref _maximumObserved);

        public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var active = Interlocked.Increment(ref _activeCount);
            UpdateMaximum(ref _maximumObserved, active);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await action(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCount);
                _gate.Release();
            }
        }

        public async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
        {
            await RunAsync(async token =>
            {
                await action(token).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _gate.Dispose();
        }

        private static void UpdateMaximum(ref int target, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref target)) &&
                   Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }

    public sealed class OperationGeneration : IDisposable
    {
        private readonly object _sync = new object();
        private readonly Dictionary<long, CancellationTokenSource> _sources =
            new Dictionary<long, CancellationTokenSource>();
        private CancellationTokenSource _currentCancellation;
        private long _currentGeneration;

        public OperationGenerationLease Begin(CancellationToken externalCancellation)
        {
            CancellationTokenSource previous;
            CancellationTokenSource current;
            long generation;

            lock (_sync)
            {
                previous = _currentCancellation;
                current = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
                _currentCancellation = current;
                generation = ++_currentGeneration;
                _sources[generation] = current;
            }

            Cancel(previous);
            return new OperationGenerationLease(generation, current.Token);
        }

        public long CurrentGeneration
        {
            get
            {
                lock (_sync)
                    return _currentGeneration;
            }
        }

        public bool IsCurrent(long generation)
        {
            lock (_sync)
            {
                return generation == _currentGeneration &&
                    _currentCancellation != null &&
                    !_currentCancellation.IsCancellationRequested;
            }
        }

        public void CancelCurrent()
        {
            CancellationTokenSource current;
            lock (_sync)
            {
                current = _currentCancellation;
                _currentCancellation = null;
                _currentGeneration++;
            }

            Cancel(current);
        }

        public void Complete(long generation)
        {
            CancellationTokenSource completed;
            lock (_sync)
            {
                if (!_sources.TryGetValue(generation, out completed))
                    return;

                _sources.Remove(generation);
                if (generation == _currentGeneration &&
                    ReferenceEquals(_currentCancellation, completed))
                {
                    _currentCancellation = null;
                }
            }

            completed.Dispose();
        }

        public void Dispose()
        {
            CancellationTokenSource[] sources;
            lock (_sync)
            {
                sources = _sources.Values.ToArray();
                _sources.Clear();
                _currentCancellation = null;
                _currentGeneration++;
            }

            foreach (var source in sources)
            {
                Cancel(source);
                source.Dispose();
            }
        }

        private static void Cancel(CancellationTokenSource source)
        {
            if (source == null)
                return;

            try { source.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    public struct OperationGenerationLease
    {
        public OperationGenerationLease(long generation, CancellationToken token)
        {
            Generation = generation;
            Token = token;
        }

        public long Generation { get; }
        public CancellationToken Token { get; }
    }

    public sealed class LatestWorkCoalescer
    {
        private readonly object _sync = new object();
        private readonly Action<Action> _schedule;
        private Action _latest;
        private bool _scheduled;
        private int _scheduleCount;

        public LatestWorkCoalescer(Action<Action> schedule)
        {
            _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        }

        public int ScheduleCount => Volatile.Read(ref _scheduleCount);
        public bool HasPendingWork
        {
            get
            {
                lock (_sync)
                    return _scheduled;
            }
        }

        public void Post(Action action)
        {
            if (action == null)
                return;

            lock (_sync)
            {
                _latest = action;
                if (_scheduled)
                    return;

                _scheduled = true;
                Interlocked.Increment(ref _scheduleCount);
            }

            _schedule(Drain);
        }

        private void Drain()
        {
            Action action;
            lock (_sync)
            {
                action = _latest;
                _latest = null;
                _scheduled = false;
            }

            action?.Invoke();
        }
    }

    public sealed class RefreshRequestCoalescer
    {
        private readonly object _sync = new object();
        private readonly HashSet<string> _reasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _force;
        private bool _workerRunning;
        private DateTimeOffset _dueAt;

        public bool Enqueue(bool force, string reason, DateTimeOffset dueAt)
        {
            lock (_sync)
            {
                _force |= force;
                if (!string.IsNullOrWhiteSpace(reason))
                    _reasons.Add(reason);
                if (dueAt > _dueAt)
                    _dueAt = dueAt;

                if (_workerRunning)
                    return false;

                _workerRunning = true;
                return true;
            }
        }

        public bool TryTake(DateTimeOffset now, out CoalescedRefreshRequest request, out TimeSpan remainingDelay)
        {
            lock (_sync)
            {
                if (_reasons.Count == 0 && !_force)
                {
                    request = default(CoalescedRefreshRequest);
                    remainingDelay = TimeSpan.Zero;
                    return false;
                }

                if (now < _dueAt)
                {
                    request = default(CoalescedRefreshRequest);
                    remainingDelay = _dueAt - now;
                    return false;
                }

                request = new CoalescedRefreshRequest(
                    _force,
                    string.Join("+", _reasons));
                remainingDelay = TimeSpan.Zero;
                _force = false;
                _reasons.Clear();
                _dueAt = DateTimeOffset.MinValue;
                return true;
            }
        }

        public bool CompleteOrContinue()
        {
            lock (_sync)
            {
                if (_reasons.Count > 0 || _force)
                    return true;

                _workerRunning = false;
                return false;
            }
        }

        public void Expedite(DateTimeOffset dueAt)
        {
            lock (_sync)
            {
                if (_dueAt == DateTimeOffset.MinValue || dueAt < _dueAt)
                    _dueAt = dueAt;
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                _force = false;
                _reasons.Clear();
                _dueAt = DateTimeOffset.MinValue;
                _workerRunning = false;
            }
        }
    }

    public struct CoalescedRefreshRequest
    {
        public CoalescedRefreshRequest(bool force, string reasons)
        {
            Force = force;
            Reasons = reasons;
        }

        public bool Force { get; }
        public string Reasons { get; }
    }

    public sealed class RepetitiveEventLimiter
    {
        private sealed class Bucket
        {
            public readonly object Sync = new object();
            public DateTimeOffset LastEmittedAt = DateTimeOffset.MinValue;
            public long SuppressedCount;
        }

        private readonly TimeSpan _interval;
        private readonly ConcurrentDictionary<string, Bucket> _buckets =
            new ConcurrentDictionary<string, Bucket>(StringComparer.Ordinal);

        public RepetitiveEventLimiter(TimeSpan interval)
        {
            if (interval < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(interval));
            _interval = interval;
        }

        public bool TryEmit(string key, DateTimeOffset now, out long suppressedCount)
        {
            key = string.IsNullOrWhiteSpace(key) ? "unspecified" : key;
            var bucket = _buckets.GetOrAdd(key, ignored => new Bucket());
            lock (bucket.Sync)
            {
                if (bucket.LastEmittedAt != DateTimeOffset.MinValue &&
                    now - bucket.LastEmittedAt < _interval)
                {
                    bucket.SuppressedCount++;
                    suppressedCount = 0;
                    return false;
                }

                suppressedCount = bucket.SuppressedCount;
                bucket.SuppressedCount = 0;
                bucket.LastEmittedAt = now;
                return true;
            }
        }
    }
}
