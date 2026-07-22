using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace LibreSpotUWP.Services
{
    public static class UiResponsivenessTelemetry
    {
        private sealed class OperationScope : IDisposable
        {
            private readonly long _id;

            public OperationScope(long id)
            {
                _id = id;
            }

            public void Dispose()
            {
                _activeOperations.TryRemove(_id, out string ignored);
            }
        }

        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(50);
        private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(30);
        private static readonly ConcurrentDictionary<long, string> _activeOperations =
            new ConcurrentDictionary<long, string>();
        private static readonly object _lifecycleSync = new object();
        private static DispatcherTimer _heartbeat;
        private static long _nextOperationId;
        private static long _lastHeartbeatTicks;
        private static long _lastReportTicks;
        private static long _stallOver100Count;
        private static long _stallOver500Count;
        private static long _maximumStallTicks;
        private static int _pendingDispatcherWork;
        private static int _maximumPendingDispatcherWork;
        private static int _uiBlockingViolationCount;
        private static int _severeStallReported;
        private static string _currentPage = "startup";

        public static string CurrentPage => Volatile.Read(ref _currentPage) ?? "unknown";
        public static int PendingDispatcherWork => Volatile.Read(ref _pendingDispatcherWork);
        public static int MaximumPendingDispatcherWork => Volatile.Read(ref _maximumPendingDispatcherWork);
        public static long StallsOver100Ms => Interlocked.Read(ref _stallOver100Count);
        public static long StallsOver500Ms => Interlocked.Read(ref _stallOver500Count);
        public static int UiBlockingViolationCount => Volatile.Read(ref _uiBlockingViolationCount);

        public static void Start()
        {
            lock (_lifecycleSync)
            {
                if (_heartbeat != null)
                    return;

                _lastHeartbeatTicks = Stopwatch.GetTimestamp();
                _lastReportTicks = 0;
                Interlocked.Exchange(ref _severeStallReported, 0);
                _heartbeat = new DispatcherTimer { Interval = HeartbeatInterval };
                _heartbeat.Tick += OnHeartbeat;
                _heartbeat.Start();
            }
        }

        public static void Stop()
        {
            lock (_lifecycleSync)
            {
                if (_heartbeat == null)
                    return;

                _heartbeat.Stop();
                _heartbeat.Tick -= OnHeartbeat;
                _heartbeat = null;
                _lastHeartbeatTicks = 0;
            }
        }

        public static void SetCurrentPage(string page)
        {
            if (string.IsNullOrWhiteSpace(page))
                page = "unknown";

            var separator = page.IndexOf(':');
            if (separator >= 0)
                page = page.Substring(0, separator);

            Volatile.Write(ref _currentPage, page);
        }

        public static IDisposable BeginOperation(string name, long generation = 0)
        {
            var id = Interlocked.Increment(ref _nextOperationId);
            var description = generation > 0
                ? $"{name}#{generation}"
                : $"{name}#{id}";
            _activeOperations[id] = description;
            return new OperationScope(id);
        }

        public static void DispatcherWorkQueued()
        {
            var pending = Interlocked.Increment(ref _pendingDispatcherWork);
            UpdateMaximum(ref _maximumPendingDispatcherWork, pending);
        }

        public static void DispatcherWorkCompleted()
        {
            if (Interlocked.Decrement(ref _pendingDispatcherWork) < 0)
                Interlocked.Exchange(ref _pendingDispatcherWork, 0);
        }

        public static void VerifyBackgroundThread(string operation)
        {
            var dispatcher = Window.Current?.Dispatcher;
            if (dispatcher == null || !dispatcher.HasThreadAccess)
                return;

            Interlocked.Increment(ref _uiBlockingViolationCount);
            LogService.Telemetry(
                "ui-thread-work:" + operation,
                $"UI-thread audit violation: {operation} executed on the dispatcher. page={CurrentPage}, active={FormatActiveOperations()}, pendingDispatcher={PendingDispatcherWork}.",
                warning: true);
        }

        public static UiResponsivenessSnapshot GetSnapshot()
        {
            return new UiResponsivenessSnapshot
            {
                CurrentPage = CurrentPage,
                ActiveOperations = FormatActiveOperations(),
                PendingDispatcherWork = PendingDispatcherWork,
                MaximumPendingDispatcherWork = MaximumPendingDispatcherWork,
                StallsOver100Ms = StallsOver100Ms,
                StallsOver500Ms = StallsOver500Ms,
                MaximumStallMs = TicksToMilliseconds(Interlocked.Read(ref _maximumStallTicks)),
                UiBlockingViolationCount = UiBlockingViolationCount
            };
        }

        private static void OnHeartbeat(object sender, object args)
        {
            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Exchange(ref _lastHeartbeatTicks, now);
            if (previous == 0)
                return;

            var elapsedTicks = now - previous;
            var expectedTicks = (long)(HeartbeatInterval.TotalSeconds * Stopwatch.Frequency);
            var stallTicks = Math.Max(0, elapsedTicks - expectedTicks);
            var stallMs = TicksToMilliseconds(stallTicks);
            if (stallMs < 100)
                return;

            Interlocked.Increment(ref _stallOver100Count);
            if (stallMs >= 500)
                Interlocked.Increment(ref _stallOver500Count);
            UpdateMaximum(ref _maximumStallTicks, stallTicks);

            var lastReport = Interlocked.Read(ref _lastReportTicks);
            var firstSevereStall = stallMs >= 500 &&
                Interlocked.CompareExchange(ref _severeStallReported, 1, 0) == 0;
            var reportDue = firstSevereStall ||
                now - lastReport >= ReportInterval.TotalSeconds * Stopwatch.Frequency;
            if (!reportDue && lastReport != 0)
                return;

            if (Interlocked.CompareExchange(ref _lastReportTicks, now, lastReport) != lastReport)
                return;

            var snapshot = GetSnapshot();
            LogService.Warn(
                $"[UiResponsiveness] dispatcherStallMs={stallMs:F0}, maximumStallMs={snapshot.MaximumStallMs:F0}, " +
                $"over100={snapshot.StallsOver100Ms}, over500={snapshot.StallsOver500Ms}, page={snapshot.CurrentPage}, " +
                $"active={snapshot.ActiveOperations}, pendingDispatcher={snapshot.PendingDispatcherWork}, " +
                $"maximumPendingDispatcher={snapshot.MaximumPendingDispatcherWork}.");
        }

        private static string FormatActiveOperations()
        {
            var operations = _activeOperations.Values.OrderBy(value => value).Take(8).ToArray();
            if (operations.Length == 0)
                return "none";

            var suffix = _activeOperations.Count > operations.Length
                ? $"+{_activeOperations.Count - operations.Length}"
                : string.Empty;
            return string.Join(",", operations) + suffix;
        }

        private static double TicksToMilliseconds(long ticks)
        {
            return ticks <= 0 ? 0 : ticks * 1000.0 / Stopwatch.Frequency;
        }

        private static void UpdateMaximum(ref int target, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref target)) &&
                   Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }

        private static void UpdateMaximum(ref long target, long value)
        {
            long current;
            while (value > (current = Interlocked.Read(ref target)) &&
                   Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }

    public sealed class UiResponsivenessSnapshot
    {
        public string CurrentPage { get; set; }
        public string ActiveOperations { get; set; }
        public int PendingDispatcherWork { get; set; }
        public int MaximumPendingDispatcherWork { get; set; }
        public long StallsOver100Ms { get; set; }
        public long StallsOver500Ms { get; set; }
        public double MaximumStallMs { get; set; }
        public int UiBlockingViolationCount { get; set; }
    }

    public static class UiWorkScheduler
    {
        private static readonly ConditionalWeakTable<object, LatestWorkCoalescer> _coalescers =
            new ConditionalWeakTable<object, LatestWorkCoalescer>();

        public static void RunLatest(object owner, CoreDispatcher dispatcher, Action action)
        {
            if (owner == null || dispatcher == null || action == null)
                return;

            if (dispatcher.HasThreadAccess)
            {
                action();
                return;
            }

            var coalescer = _coalescers.GetValue(owner, key =>
                new LatestWorkCoalescer(work =>
                {
                    UiResponsivenessTelemetry.DispatcherWorkQueued();
                    try
                    {
                        var ignored = dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                        {
                            try { work(); }
                            finally { UiResponsivenessTelemetry.DispatcherWorkCompleted(); }
                        });
                    }
                    catch
                    {
                        UiResponsivenessTelemetry.DispatcherWorkCompleted();
                        throw;
                    }
                }));

            coalescer.Post(action);
        }
    }
}
