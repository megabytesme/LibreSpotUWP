using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibreSpotUWP.Services;

internal static class Program
{
    private const ulong QuantumBytes = 2646; // 10 ms: 44.1 kHz, stereo, packed 24-bit PCM.
    private const ulong PreRollBytes = QuantumBytes * 20; // 200 ms.

    private static int Main()
    {
        var tests = new Action[]
        {
            HomeRequestsUseArmSafeBoundedConcurrency,
            NavigationCancelsOldHomeGeneration,
            StaleHomeResultsCannotUpdateReplacementGeneration,
            TileRefreshBurstProducesOneUpdate,
            RepetitiveTelemetryProducesOneSinkEntry,
            PlaybackEventStormCreatesOnePendingDispatcherItem,
            MetadataAndLyricsCompletionCannotOutliveNavigation,
            UiPathStaticAuditFindsNoBlockingIoOrTaskWaits,
            InitialPlaybackWithDelayedProducer,
            ManualNextWithThreeSecondProducerDelay,
            AutomaticHandoffToPreloadedTrack,
            SeekWithDelayedPcm,
            StalePcmFromPreviousGeneration,
            RapidNextPrevious,
            CancellationDuringTransition,
            ReconnectDuringLoading,
            UserDragCommitsOneSeek,
            RemoteCorrectionAfterLocalSeekDoesNotReseek,
            RepeatedCorrectionsInsideToleranceAreIgnored,
            LargeLegitimateCorrectionIsApplied,
            ProgrammaticUiUpdatesNeverSeek,
            RemoteConnectPositionRemainsSynchronized,
            CorrectionStormKeepsDispatcherAndAudioWorkBounded,
            LumiaStyleSequenceDoesNotUnderrun,
            TwoConcurrentInitializationCallsShareOneOperation,
            RepeatedActivationInitializesOnce,
            StopFollowedByPlayRearmsProducerHealth,
            EndOfQueueDisarmsProducerHealth,
            ProducerStopsWhileWindowsWouldRemainOnline,
            UnexpectedEofTriggersTransportRecovery,
            SessionDisconnectDuringPlaybackTriggersRecovery,
            RapidReconnectRequestsShareOneOperation,
            CancellationDuringReconnectIsObserved,
            OldSessionEventsCannotChangeReplacementSession,
            GraphDisposalWaitsForCallbacksInFlight,
            SuccessfulNormalPreloadAndHandoff,
            UnavailableSpotifyContextUsesApplicationFallback,
            EmptyInternalQueueUsesApplicationFallback,
            NoTrackChangedAfterEndUsesFallback,
            DelayedNormalHandoffCancelsFallback,
            UserNextDuringGraceCancelsFallback,
            ShuffleUsesStoredOccurrenceOrder,
            RepeatOneReloadsCurrentOccurrence,
            RepeatContextWraps,
            FinalTrackWithoutRepeatCompletes,
            UnavailableNextTrackIsSkipped,
            DuplicateTrackUrisAdvanceByOccurrence,
            ReconnectImmediatelyBeforeEndRebasesGeneration,
            StaleEndFromPreviousQueueGenerationIsIgnored
        };

        try
        {
            foreach (var test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("All simulated 10 ms AudioGraph quantum tests passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL " + ex.Message);
            return 1;
        }
    }

    private static void HomeRequestsUseArmSafeBoundedConcurrency()
    {
        using (var gate = new BoundedAsyncGate(3))
        {
            var active = 0;
            var maximum = 0;
            var tasks = Enumerable.Range(0, 30).Select(_ => gate.RunAsync(async cancellationToken =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximum, current);
                try
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                    return current;
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }, CancellationToken.None)).ToArray();

            Task.WhenAll(tasks).GetAwaiter().GetResult();
            Assert(maximum == 3, "Home request gate did not exercise the configured ARM-safe concurrency");
            Assert(gate.MaximumObserved == 3, "Home request concurrency diagnostics were incorrect");
        }
    }

    private static void NavigationCancelsOldHomeGeneration()
    {
        using (var generations = new OperationGeneration())
        {
            var oldLoad = generations.Begin(CancellationToken.None);
            var newLoad = generations.Begin(CancellationToken.None);

            Assert(oldLoad.Token.IsCancellationRequested, "navigating away did not cancel the old Home generation");
            Assert(!generations.IsCurrent(oldLoad.Generation), "cancelled Home generation remained current");
            Assert(generations.IsCurrent(newLoad.Generation), "replacement Home generation was not current");
        }
    }

    private static void StaleHomeResultsCannotUpdateReplacementGeneration()
    {
        using (var generations = new OperationGeneration())
        {
            var oldLoad = generations.Begin(CancellationToken.None);
            var oldApplyCount = 0;
            var replacement = generations.Begin(CancellationToken.None);

            if (generations.IsCurrent(oldLoad.Generation))
                oldApplyCount++;

            Assert(oldApplyCount == 0, "stale Home results updated the replacement page");
            Assert(generations.IsCurrent(replacement.Generation), "replacement generation lost ownership");
        }
    }

    private static void TileRefreshBurstProducesOneUpdate()
    {
        var coalescer = new RefreshRequestCoalescer();
        var due = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        var workerStarts = 0;
        for (var i = 0; i < 100; i++)
        {
            if (coalescer.Enqueue(i == 20, "reason-" + (i % 4), due))
                workerStarts++;
        }

        Assert(workerStarts == 1, "tile burst started more than one refresh worker");
        CoalescedRefreshRequest request;
        TimeSpan remaining;
        Assert(coalescer.TryTake(due, out request, out remaining), "tile burst did not produce a refresh");
        Assert(request.Force, "tile burst lost its merged force-refresh flag");
        Assert(request.Reasons.Split('+').Length == 4, "tile burst did not merge refresh reasons");
        Assert(!coalescer.CompleteOrContinue(), "tile burst left a second OS update pending");
    }

    private static void RepetitiveTelemetryProducesOneSinkEntry()
    {
        var limiter = new RepetitiveEventLimiter(TimeSpan.FromSeconds(30));
        var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        var sinkEntries = 0;
        long suppressed;
        for (var i = 0; i < 1000; i++)
        {
            if (limiter.TryEmit("audio-health", now.AddMilliseconds(i), out suppressed))
                sinkEntries++;
        }

        Assert(sinkEntries == 1, "repetitive telemetry enqueued one sink write per event");
        Assert(limiter.TryEmit("audio-health", now.AddSeconds(31), out suppressed),
            "telemetry did not reopen after the rate-limit interval");
        Assert(suppressed == 999, "telemetry summary lost its suppressed-event count");
    }

    private static void PlaybackEventStormCreatesOnePendingDispatcherItem()
    {
        var scheduled = new List<Action>();
        var applied = -1;
        var coalescer = new LatestWorkCoalescer(action => scheduled.Add(action));
        for (var i = 0; i < 1000; i++)
        {
            var snapshot = i;
            coalescer.Post(() => applied = snapshot);
        }

        Assert(scheduled.Count == 1, "1,000 playback events created unbounded dispatcher work");
        Assert(coalescer.HasPendingWork, "coalesced playback UI work was not pending");
        scheduled[0]();
        Assert(applied == 999, "coalesced playback work did not apply the latest state");
        Assert(!coalescer.HasPendingWork, "dispatcher work remained pending after the latest update");
    }

    private static void MetadataAndLyricsCompletionCannotOutliveNavigation()
    {
        using (var generations = new OperationGeneration())
        {
            var oldPage = generations.Begin(CancellationToken.None);
            var scheduled = new List<Action>();
            var dispatcher = new LatestWorkCoalescer(action => scheduled.Add(action));
            var completion = Task.Run(async () =>
            {
                await Task.Delay(20).ConfigureAwait(false); // simulated parsing/network completion
                if (generations.IsCurrent(oldPage.Generation))
                    dispatcher.Post(() => { });
            });

            generations.Begin(CancellationToken.None);
            completion.GetAwaiter().GetResult();
            Assert(scheduled.Count == 0, "metadata or lyrics completion queued UI work after navigation");
        }
    }

    private static void UiPathStaticAuditFindsNoBlockingIoOrTaskWaits()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appRoot = Path.Combine(repositoryRoot, "LibreSpotUWP");
        var sourceFiles = Directory.GetFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => file.IndexOf(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0)
            .Where(file => file.IndexOf(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0)
            .ToArray();
        foreach (var file in sourceFiles)
        {
            var source = File.ReadAllText(file);
            var relative = file.Substring(appRoot.Length).TrimStart(Path.DirectorySeparatorChar);
            var allowsBackgroundOnlyWait =
                relative.EndsWith("PlaybackLifecycleCoordinator.cs", StringComparison.OrdinalIgnoreCase) ||
                relative.EndsWith("LibrespotRingBufferPlayer.cs", StringComparison.OrdinalIgnoreCase) ||
                relative.EndsWith("LogService.cs", StringComparison.OrdinalIgnoreCase);
            if (!allowsBackgroundOnlyWait)
            {
                Assert(!source.Contains(".Wait();") && !source.Contains(".Wait("),
                    "blocking wait remains in application source: " + relative);
            }

            Assert(!source.Contains(".Result"), "blocking Task.Result remains in application source: " + relative);
            Assert(!source.Contains("File.ReadAllText(") &&
                   !source.Contains("File.WriteAllText(") &&
                   !source.Contains("File.AppendAllText("),
                "synchronous file I/O remains in application source: " + relative);
        }

        var home = File.ReadAllText(Path.Combine(appRoot, "ViewModels", "HomePageViewModel.cs"));
        Assert(home.Contains("Task.Run(") && home.Contains("MaximumRequestConcurrency = 3"),
            "Home snapshot work is not explicitly off-dispatcher and bounded");
        var lyrics = File.ReadAllText(Path.Combine(appRoot, "Views", "Win11", "LyricsPage_Win11.xaml.cs"));
        Assert(lyrics.Contains("GetLyricsAsync") && !lyrics.Contains("JsonConvert.DeserializeObject"),
            "lyrics JSON parsing remains on the page dispatcher");
        var metadata = File.ReadAllText(Path.Combine(appRoot, "Services", "FileMetadataCache.cs"));
        Assert(metadata.Contains("Task.Run(() =>") && metadata.Contains("metadata JSON parsing"),
            "metadata JSON parsing is not explicitly off-dispatcher");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "LibreSpotUWP")) &&
                Directory.Exists(Path.Combine(current.FullName, "tests")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root for static UI audit");
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private static void InitialPlaybackWithDelayedProducer()
    {
        var machine = NewMachine();
        machine.BeginTransition(false, true);
        machine.ObserveLoading(1);
        Assert(machine.ObserveTrack(1, 1, 0), "initial marker should be accepted");
        Assert(machine.RequestPlayback(1, 1), "initial PlaybackResumed should bind");

        for (ulong quantum = 0; quantum < 20; quantum++)
        {
            var evaluation = Evaluate(machine, 1, 0, quantum * QuantumBytes, 0);
            Assert(!evaluation.ShouldResume, "initial playback resumed before 200 ms pre-roll");
            Assert(evaluation.ShouldGate, "initial consumer was not gated during producer delay");
        }

        Assert(Evaluate(machine, 1, 0, PreRollBytes, 0).ShouldResume,
            "initial playback did not resume at the pre-roll threshold");
    }

    private static void ManualNextWithThreeSecondProducerDelay()
    {
        var machine = ActiveMachine(1, 1);
        const ulong nextStart = QuantumBytes * 50;
        machine.BeginTransition(false, true);
        machine.ObserveLoading(2);
        Assert(machine.ObserveTrack(2, 2, nextStart), "Next marker should be accepted");
        Assert(machine.RequestPlayback(2, 2), "Next PlaybackResumed should bind");

        for (var quantum = 0; quantum < 300; quantum++)
        {
            var evaluation = Evaluate(machine, 2, nextStart, nextStart, nextStart);
            Assert(evaluation.ShouldGate && !evaluation.ShouldResume,
                "manual Next consumed while its producer was delayed");
        }

        var ready = Evaluate(machine, 2, nextStart, nextStart + PreRollBytes, nextStart);
        Assert(ready.ShouldResume && ready.ActiveGeneration == 2,
            "manual Next did not resume the replacement generation");
    }

    private static void AutomaticHandoffToPreloadedTrack()
    {
        var machine = ActiveMachine(1, 10);
        ulong boundary = QuantumBytes * 100;
        machine.BeginAutomaticTransition(true);
        Assert(machine.ObserveTrack(11, 2, boundary), "preloaded marker should be accepted");
        Assert(machine.RequestPlayback(11, 2), "preloaded PlaybackResumed should bind");

        var draining = Evaluate(
            machine,
            2,
            boundary,
            boundary + PreRollBytes,
            boundary - (2 * QuantumBytes));
        Assert(!draining.ShouldGate && !draining.ShouldResume,
            "automatic handoff did not preserve the current PCM tail");
        Assert(draining.ActiveBoundarySequence == boundary,
            "automatic handoff lost the generation boundary");

        var handoff = Evaluate(machine, 2, boundary, boundary + PreRollBytes, boundary);
        Assert(handoff.ShouldResume && !handoff.ShouldGate,
            "preloaded handoff was not ready at the boundary");
        Assert(handoff.AvailableBytes >= PreRollBytes,
            "preloaded handoff resumed without pre-roll");
    }

    private static void SeekWithDelayedPcm()
    {
        var machine = ActiveMachine(4, 40);
        ulong seekStart = QuantumBytes * 80;
        machine.BeginTransition(false, true);
        Assert(machine.ObserveSeek(40, 5, seekStart), "seek generation should be accepted");
        Assert(machine.RequestPlayback(40, 5), "seek resume should bind");

        Assert(!Evaluate(machine, 5, seekStart, seekStart + (10 * QuantumBytes), seekStart).ShouldResume,
            "seek resumed with only 100 ms buffered");
        Assert(Evaluate(machine, 5, seekStart, seekStart + PreRollBytes, seekStart).ShouldResume,
            "seek did not resume at 200 ms");
    }

    private static void StalePcmFromPreviousGeneration()
    {
        var machine = ActiveMachine(7, 70);
        Assert(machine.IsActiveEvent(70, 7), "active event identity was not retained");
        Assert(!machine.IsActiveEvent(69, 6), "stale event matched the active generation");
        machine.BeginTransition(false, true);
        Assert(!machine.ObserveLoading(70), "stale Loading rebound the pending transition");
        machine.ObserveLoading(71);

        Assert(!machine.ObserveTrack(70, 8, QuantumBytes * 100),
            "stale TrackChanged bound to the new transition");
        Assert(machine.ObserveTrack(71, 9, QuantumBytes * 120),
            "current TrackChanged was rejected");
        Assert(!machine.RequestPlayback(70, 8),
            "stale PlaybackResumed bound to the active transition");
        Assert(machine.RequestPlayback(71, 9), "current PlaybackResumed was rejected");
    }

    private static void RapidNextPrevious()
    {
        var machine = ActiveMachine(10, 100);
        long next = machine.BeginTransition(false, true);
        machine.ObserveLoading(101);
        long previous = machine.BeginTransition(false, true);
        machine.ObserveLoading(102);

        Assert(previous > next, "transition identifiers are not monotonic");
        Assert(!machine.ObserveLoading(101), "older Loading superseded the latest request");
        Assert(!machine.ObserveTrack(101, 11, QuantumBytes * 150),
            "cancelled Next marker won the Next/Previous race");
        Assert(machine.ObserveTrack(102, 12, QuantumBytes * 160),
            "latest Previous marker did not win");
    }

    private static void CancellationDuringTransition()
    {
        var machine = ActiveMachine(20, 200);
        machine.BeginTransition(false, true);
        machine.ObserveLoading(201);
        machine.Cancel();

        Assert(!machine.ObserveTrack(201, 21, QuantumBytes * 200),
            "cancelled transition accepted late PCM");
        Assert(!machine.RequestPlayback(201, 21),
            "cancelled transition accepted late PlaybackResumed");
        Assert(!machine.RequestPlayback(200, 20),
            "cancelled transition restarted the formerly active generation");
        Assert(Evaluate(machine, 21, QuantumBytes * 200, QuantumBytes * 250, QuantumBytes * 200).ShouldGate,
            "cancelled transition ungated the consumer");
    }

    private static void ReconnectDuringLoading()
    {
        var machine = ActiveMachine(30, 300);
        machine.BeginTransition(false, true);
        machine.ObserveLoading(301);
        machine.BeginTransition(false, true); // reconnect supersedes the load
        machine.ObserveLoading(302);

        Assert(!machine.ObserveTrack(301, 31, QuantumBytes * 300),
            "pre-reconnect marker affected the replacement session");
        Assert(machine.ObserveTrack(302, 32, QuantumBytes * 320),
            "reconnected session marker was rejected");
        Assert(machine.RequestPlayback(302, 32), "reconnected PlaybackResumed was rejected");
        Assert(Evaluate(
            machine,
            32,
            QuantumBytes * 320,
            (QuantumBytes * 320) + PreRollBytes,
            QuantumBytes * 320).ShouldResume,
            "reconnected generation did not resume after pre-roll");
    }

    private static void UserDragCommitsOneSeek()
    {
        var interaction = new PositionSeekInteraction();
        interaction.BeginDrag();
        Assert(interaction.IsDragging, "slider did not enter preview mode");

        uint positionMs;
        Assert(interaction.TryCommit(84_374, out positionMs), "slider release did not commit");
        Assert(positionMs == 84_374, "slider committed the wrong preview position");
        Assert(!interaction.TryCommit(84_374, out positionMs),
            "capture loss committed a second seek after pointer release");

        long now = 0;
        var synchronizer = new PlaybackPositionSynchronizer(() => now);
        var request = synchronizer.BeginUserSeek(84_374);
        Assert(synchronizer.TryRecordSeekIssued(request), "user seek token was not issued");
        Assert(!synchronizer.TryRecordSeekIssued(request), "user seek token was issued twice");
    }

    private static void RemoteCorrectionAfterLocalSeekDoesNotReseek()
    {
        long now = 0;
        var synchronizer = new PlaybackPositionSynchronizer(() => now);
        synchronizer.Reset(10_000);

        var request = synchronizer.BeginUserSeek(84_374);
        Assert(synchronizer.TryRecordSeekIssued(request), "local seek was not issued");

        now = 5;
        synchronizer.ObserveAuthoritative(
            84_374,
            PlaybackPositionOrigin.SeekAcknowledgement,
            isPlaying: true);
        now = 10;
        var correction = synchronizer.ObserveAuthoritative(
            84_384,
            PlaybackPositionOrigin.LibrespotCorrection,
            isPlaying: true);

        Assert(!correction.AppliedHardCorrection, "seek acknowledgement caused a hard correction");
        Assert(!synchronizer.TryRecordSeekIssued(request),
            "a correction recursively reissued the original seek");

        now += PlaybackPositionSynchronizer.CorrectionBurstQuietPeriodMs;
        PositionCorrectionBurstSummary summary;
        Assert(synchronizer.TryTakeCorrectionBurstSummary(out summary), "correction burst was not summarized");
        Assert(summary.ActualSeeksIssued == 1, "correction burst did not retain the one originating seek");
    }

    private static void RepeatedCorrectionsInsideToleranceAreIgnored()
    {
        long now = 0;
        var synchronizer = new PlaybackPositionSynchronizer(() => now);
        synchronizer.Reset(20_000);

        for (var i = 1; i <= 100; i++)
        {
            now += 10;
            var observation = synchronizer.ObserveAuthoritative(
                (uint)(20_000 + now + 100),
                PlaybackPositionOrigin.LibrespotCorrection,
                isPlaying: true);
            Assert(!observation.AppliedHardCorrection, "sub-tolerance drift caused a hard correction");
        }
    }

    private static void LargeLegitimateCorrectionIsApplied()
    {
        long now = 0;
        var synchronizer = new PlaybackPositionSynchronizer(() => now);
        synchronizer.Reset(10_000);

        now = 100;
        var observation = synchronizer.ObserveAuthoritative(
            15_000,
            PlaybackPositionOrigin.LibrespotCorrection,
            isPlaying: true);

        Assert(observation.AppliedHardCorrection, "large correction was ignored");
        Assert(synchronizer.GetEstimatedPosition(30_000, isPlaying: true) == 15_000,
            "large correction did not replace the monotonic position anchor");
    }

    private static void ProgrammaticUiUpdatesNeverSeek()
    {
        var interaction = new PositionSeekInteraction();
        uint ignored;
        Assert(!interaction.TryCommit(42_000, out ignored),
            "programmatic slider value was interpreted as a user seek");

        long now = 0;
        var synchronizer = new PlaybackPositionSynchronizer(() => now);
        synchronizer.Reset(42_000);
        synchronizer.ObserveAuthoritative(
            42_100,
            PlaybackPositionOrigin.LibrespotCorrection,
            isPlaying: true);

        now += PlaybackPositionSynchronizer.CorrectionBurstQuietPeriodMs;
        PositionCorrectionBurstSummary summary;
        Assert(synchronizer.TryTakeCorrectionBurstSummary(out summary), "UI correction was not summarized");
        Assert(summary.ActualSeeksIssued == 0, "programmatic position update issued a seek");
    }

    private static void RemoteConnectPositionRemainsSynchronized()
    {
        long now = 0;
        var synchronizer = new PlaybackPositionSynchronizer(() => now);
        synchronizer.Reset(5_000);

        now = 250;
        var remote = synchronizer.ObserveAuthoritative(
            30_000,
            PlaybackPositionOrigin.RemoteConnect,
            isPlaying: true);
        Assert(remote.AppliedHardCorrection, "large remote Connect update was not accepted");

        now = 750;
        Assert(synchronizer.GetEstimatedPosition(60_000, isPlaying: true) == 30_500,
            "remote Connect position did not progress from monotonic time");
    }

    private static void CorrectionStormKeepsDispatcherAndAudioWorkBounded()
    {
        long now = 0;
        var synchronizer = new PlaybackPositionSynchronizer(() => now);
        var seek = synchronizer.BeginUserSeek(84_374);
        Assert(synchronizer.TryRecordSeekIssued(seek), "storm setup seek was not issued");

        var uiDispatches = 0;
        var audioBufferQuanta = 20;
        var underruns = 0;
        for (var i = 0; i < 1000; i++)
        {
            now += 10;
            synchronizer.ObserveAuthoritative(
                (uint)(84_374 + now),
                PlaybackPositionOrigin.LibrespotCorrection,
                isPlaying: true);

            uint visible;
            if (synchronizer.TryGetVisiblePosition(240_960, true, false, out visible))
                uiDispatches++;

            // Correction ingestion performs no dispatcher work, so the simulated
            // 10 ms producer and AudioGraph consumer continue at equal cadence.
            audioBufferQuanta++;
            if (audioBufferQuanta == 0)
                underruns++;
            else
                audioBufferQuanta--;
        }

        Assert(uiDispatches <= 41, "1,000 corrections created unbounded UI dispatcher work");
        Assert(underruns == 0, "correction storm starved the simulated audio producer");

        now += PlaybackPositionSynchronizer.CorrectionBurstQuietPeriodMs;
        PositionCorrectionBurstSummary summary;
        Assert(synchronizer.TryTakeCorrectionBurstSummary(out summary), "storm summary was not emitted");
        Assert(summary.Count == 1000, "storm summary lost correction events");
        Assert(summary.ActualSeeksIssued == 1, "storm recursively issued additional seeks");
        Assert(summary.HardCorrections == 0, "in-tolerance Lumia sequence hard-corrected repeatedly");
    }

    private static void LumiaStyleSequenceDoesNotUnderrun()
    {
        const int correctionCount = 772;
        const long burstDurationMs = 45_808;
        const uint firstPositionMs = 84_374;
        const uint positionAdvanceMs = 9_257;

        long now = 0;
        var synchronizer = new PlaybackPositionSynchronizer(() => now);
        var seek = synchronizer.BeginUserSeek(firstPositionMs);
        Assert(synchronizer.TryRecordSeekIssued(seek), "Lumia seek was not issued");

        var uiDispatches = 0;
        var audioBufferQuanta = 20;
        var underruns = 0;
        for (var i = 0; i < correctionCount; i++)
        {
            now = i * burstDurationMs / (correctionCount - 1);
            var position = firstPositionMs +
                (uint)(i * (long)positionAdvanceMs / (correctionCount - 1));

            synchronizer.ObserveAuthoritative(
                position,
                PlaybackPositionOrigin.LibrespotCorrection,
                isPlaying: true);

            uint visible;
            if (synchronizer.TryGetVisiblePosition(240_960, true, false, out visible))
                uiDispatches++;

            // Position correction observation consumes no AudioGraph quantum and
            // therefore cannot drain the producer's existing pre-roll.
            audioBufferQuanta++;
            if (audioBufferQuanta == 0)
                underruns++;
            else
                audioBufferQuanta--;
        }

        Assert(uiDispatches <= 185, "Lumia burst exceeded the 4 Hz UI update bound");
        Assert(underruns == 0, "Lumia burst drained the simulated ring buffer");

        now += PlaybackPositionSynchronizer.CorrectionBurstQuietPeriodMs;
        PositionCorrectionBurstSummary summary;
        Assert(synchronizer.TryTakeCorrectionBurstSummary(out summary), "Lumia burst summary was not emitted");
        Assert(summary.Count == correctionCount, "Lumia burst summary count was incorrect");
        Assert(summary.ActualSeeksIssued == 1, "Lumia burst issued more than the user seek");
        Assert(summary.HardCorrections < correctionCount / 10,
            "Lumia burst hard-corrected close to every packet");
    }

    private static void TwoConcurrentInitializationCallsShareOneOperation()
    {
        var once = new AsyncOperationOnce();
        var release = new TaskCompletionSource<bool>();
        var starts = 0;
        Func<Task> initialize = async () =>
        {
            Interlocked.Increment(ref starts);
            await release.Task.ConfigureAwait(false);
        };

        var first = once.Run(initialize);
        var second = once.Run(initialize);
        Assert(ReferenceEquals(first, second), "concurrent initialization did not share one task");
        release.SetResult(true);
        Task.WhenAll(first, second).GetAwaiter().GetResult();
        Assert(starts == 1, "concurrent initialization created more than one graph operation");
    }

    private static void RepeatedActivationInitializesOnce()
    {
        var once = new AsyncOperationOnce();
        var starts = 0;
        Task first = null;
        for (var activation = 0; activation < 20; activation++)
        {
            var current = once.Run(() =>
            {
                starts++;
                return Task.CompletedTask;
            });
            if (first == null)
                first = current;
            else
                Assert(ReferenceEquals(first, current), "repeated activation replaced the initialization task");
        }

        Assert(starts == 1, "repeated activation initialized media services more than once");
    }

    private static void StopFollowedByPlayRearmsProducerHealth()
    {
        var health = ConnectedExpectedHealth();
        health.SetPlaybackExpected(false, 1, 1, 100, 200);
        Assert(!health.Evaluate(20_000, 0, 10, 6_000).ShouldRecover,
            "deliberate stop was treated as a producer stall");

        health.SetPlaybackExpected(true, 1, 1, 100, 20_000);
        health.ObserveProducer(200, 20_100);
        var snapshot = health.Snapshot;
        Assert(snapshot.PlaybackExpected, "play did not rearm producer expectation");
        Assert(snapshot.LastPcmWriteMs == 20_100, "play did not observe replacement PCM");
        Assert(snapshot.LastSuccessfulStreamReadMs == 20_100,
            "successful stream-read health was not tracked separately");
    }

    private static void EndOfQueueDisarmsProducerHealth()
    {
        var health = ConnectedExpectedHealth();
        health.SetPlaybackExpected(false, 1, 1, 100, 500);
        var decision = health.Evaluate(60_000, 0, 10, 6_000);
        Assert(!decision.ShouldRecover, "completed queue entered recovery");
        Assert(!decision.Snapshot.PlaybackExpected, "completed queue left producer expectation armed");
    }

    private static void ProducerStopsWhileWindowsWouldRemainOnline()
    {
        var health = ConnectedExpectedHealth();
        // The watchdog deliberately has no Windows connectivity input. A healthy
        // connectivity flag therefore cannot mask absent PCM.
        var decision = health.Evaluate(6_101, 0, 10, 6_000);
        Assert(decision.ShouldRecover && decision.Reason == "pcm-stalled",
            "stopped producer was masked by session/network health");

        health.ObserveProducer(200, 6_150);
        Assert(!health.IsDecisionCurrent(decision.Snapshot),
            "new PCM did not invalidate a queued stall decision");
    }

    private static void UnexpectedEofTriggersTransportRecovery()
    {
        var health = ConnectedExpectedHealth();
        Assert(health.ReportFatal(1, "unexpected-eof"), "UnexpectedEof was not accepted for the active session");
        var decision = health.Evaluate(200, 0, 10, 6_000);
        Assert(decision.ShouldRecover && decision.Reason == "unexpected-eof",
            "UnexpectedEof did not trigger bounded recovery at low buffer");
    }

    private static void SessionDisconnectDuringPlaybackTriggersRecovery()
    {
        var health = ConnectedExpectedHealth();
        Assert(health.SetSessionState(false, 1, 200), "active disconnect was rejected");
        var decision = health.Evaluate(200, 0, 10, 6_000);
        Assert(decision.ShouldRecover && decision.Reason == "session-disconnected",
            "session disconnect did not trigger recovery after local PCM drained");

        Assert(health.SetSessionState(true, 1, 250), "same-generation reconnect was rejected");
        var reconnected = health.Snapshot;
        Assert(reconnected.SessionConnected && !reconnected.FatalTransportFailure,
            "same-generation reconnect left the disconnect failure latched");
    }

    private static void RapidReconnectRequestsShareOneOperation()
    {
        using (var gate = new RecoveryOperationGate())
        using (var lifetime = new CancellationTokenSource())
        {
            var release = new TaskCompletionSource<bool>();
            var starts = 0;
            Func<CancellationToken, Task> reconnect = async cancellationToken =>
            {
                Interlocked.Increment(ref starts);
                await release.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            };

            var requests = new Task[25];
            for (var i = 0; i < requests.Length; i++)
                requests[i] = gate.RunAsync(reconnect, lifetime.Token);

            for (var i = 1; i < requests.Length; i++)
                Assert(ReferenceEquals(requests[0], requests[i]), "rapid recovery request created another operation");
            Assert(starts == 1, "rapid recovery requests started multiple reconnects");
            release.SetResult(true);
            Task.WhenAll(requests).GetAwaiter().GetResult();
        }
    }

    private static void CancellationDuringReconnectIsObserved()
    {
        using (var gate = new RecoveryOperationGate())
        using (var lifetime = new CancellationTokenSource())
        {
            var started = new TaskCompletionSource<bool>();
            var reconnect = gate.RunAsync(async cancellationToken =>
            {
                started.SetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }, lifetime.Token);

            started.Task.GetAwaiter().GetResult();
            gate.CancelActive();
            try
            {
                reconnect.GetAwaiter().GetResult();
                throw new InvalidOperationException("cancelled reconnect completed successfully");
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static void OldSessionEventsCannotChangeReplacementSession()
    {
        var health = new ProducerHealthMonitor();
        health.SetPlaybackExpected(true, 2, 7, 100, 100);
        Assert(health.SetSessionState(true, 2, 100), "replacement session was rejected");
        Assert(!health.SetSessionState(false, 1, 200), "old disconnect changed replacement session");
        Assert(!health.ReportFatal(1, "unexpected-eof"), "old stream failure changed replacement session");
        var snapshot = health.Snapshot;
        Assert(snapshot.SessionGeneration == 2 && snapshot.SessionConnected,
            "stale session event altered the replacement session");
        Assert(!snapshot.FatalTransportFailure, "stale session failure poisoned replacement health");
    }

    private static void GraphDisposalWaitsForCallbacksInFlight()
    {
        using (var callbacks = new CallbackLifetimeGate())
        {
            Assert(callbacks.TryEnter(), "callback could not enter live graph");
            var dispose = Task.Run(() => callbacks.BeginDisposeAndWait(TimeSpan.FromSeconds(2)));
            Assert(SpinWait.SpinUntil(() => callbacks.IsDisposing, 1000),
                "graph disposal did not begin");
            Assert(!dispose.Wait(20), "graph disposal completed while callback was in flight");
            callbacks.Exit();
            Assert(dispose.GetAwaiter().GetResult(), "graph disposal did not observe callback drain");
            Assert(!callbacks.TryEnter(), "callback entered after graph disposal began");
        }
    }

    private static void SuccessfulNormalPreloadAndHandoff()
    {
        var queue = ReadyQueue(new[] { "a", "b", "c" }, "a", 10, 1);
        var preload = queue.ObservePreloadRequested("a", 10, 1);
        Assert(preload.ExpectedUri == "b", "preload did not calculate the application-owned next URI");
        Assert(queue.ObservePreloaded("b", 1), "matching librespot preload was not recorded");

        var started = DateTimeOffset.UtcNow;
        var transition = queue.BeginEndOfTrack("a", 10, 1, started);
        var result = queue.ObserveTrackChanged("b", 11, 1, started.AddMilliseconds(80));
        Assert(result != null && result.ActualChangedUri == "b", "normal handoff was not completed");
        Assert(result.PreloadedUri == "b" && !result.FallbackUsed, "normal preload was mislabeled as fallback");
        ApplicationQueueTransition ignored;
        Assert(!queue.TryClaimFallback(transition.QueueGenerationId, transition.TransitionId, out ignored),
            "fallback remained armed after normal TrackChanged");
    }

    private static void UnavailableSpotifyContextUsesApplicationFallback()
    {
        var queue = ReadyQueue(new[] { "a", "b" }, "a", 10, 1);
        queue.RecordInternalQueueFailure("spotify-context-unavailable");
        var transition = queue.BeginEndOfTrack("a", 10, 1, DateTimeOffset.UtcNow);
        ApplicationQueueTransition claimed;
        Assert(queue.TryClaimFallback(transition.QueueGenerationId, transition.TransitionId, out claimed),
            "context loss did not arm fallback");
        Assert(claimed.ExpectedUri == "b" && claimed.FailureReason == "spotify-context-unavailable",
            "context-loss fallback lost the expected URI or reason");
    }

    private static void EmptyInternalQueueUsesApplicationFallback()
    {
        var queue = ReadyQueue(new[] { "a", "b" }, "a", 10, 1);
        queue.RecordInternalQueueFailure("internal-librespot-queue-empty");
        var transition = queue.BeginEndOfTrack("a", 10, 1, DateTimeOffset.UtcNow);
        ApplicationQueueTransition claimed;
        Assert(queue.TryClaimFallback(transition.QueueGenerationId, transition.TransitionId, out claimed),
            "empty librespot queue did not use the application queue");
        Assert(claimed.FailureReason == "internal-librespot-queue-empty", "empty-queue reason was not retained");
    }

    private static void NoTrackChangedAfterEndUsesFallback()
    {
        var queue = ReadyQueue(new[] { "a", "b" }, "a", 10, 1);
        var started = DateTimeOffset.UtcNow;
        var transition = queue.BeginEndOfTrack("a", 10, 1, started);
        ApplicationQueueTransition claimed;
        Assert(queue.TryClaimFallback(transition.QueueGenerationId, transition.TransitionId, out claimed),
            "missing TrackChanged did not claim fallback");
        Assert(claimed.ExpectedUri == "b" && claimed.FallbackUsed, "fallback target was incorrect");
        var result = queue.ObserveTrackChanged("b", 11, 1, started.AddSeconds(3));
        Assert(result != null && result.FallbackUsed && result.ActualChangedUri == "b",
            "fallback TrackChanged did not produce the structured transition result");
        queue.ObserveTrackChanged("b", 11, 1, started.AddSeconds(3).AddMilliseconds(1));
        Assert(queue.Snapshot.CurrentIndex == 1, "duplicate fallback confirmation double-advanced the queue");
    }

    private static void DelayedNormalHandoffCancelsFallback()
    {
        var queue = ReadyQueue(new[] { "a", "b" }, "a", 10, 1);
        var started = DateTimeOffset.UtcNow;
        var transition = queue.BeginEndOfTrack("a", 10, 1, started);
        var result = queue.ObserveTrackChanged("b", 11, 1, started.AddMilliseconds(2_900));
        Assert(result != null && !result.FallbackUsed, "delayed normal handoff was not accepted");
        ApplicationQueueTransition ignored;
        Assert(!queue.TryClaimFallback(transition.QueueGenerationId, transition.TransitionId, out ignored),
            "delayed successful handoff did not cancel fallback");
    }

    private static void UserNextDuringGraceCancelsFallback()
    {
        var queue = ReadyQueue(new[] { "a", "b", "c" }, "a", 10, 1);
        var automatic = queue.BeginEndOfTrack("a", 10, 1, DateTimeOffset.UtcNow);
        var cancelled = queue.CancelPendingTransition("user-next", DateTimeOffset.UtcNow);
        Assert(cancelled != null && cancelled.InternalQueueFailureReason.Contains("user-next"),
            "user Next did not produce a cancellation result for the automatic transition");
        var manual = queue.BeginManualMove(1, 10, 1);
        Assert(manual.ExpectedUri == "b", "manual Next did not retain the expected application target");
        ApplicationQueueTransition ignored;
        Assert(!queue.TryClaimFallback(automatic.QueueGenerationId, automatic.TransitionId, out ignored),
            "automatic fallback survived user Next");
        Assert(!queue.BeginEndOfTrack("a", 10, 1, DateTimeOffset.UtcNow).IsValid,
            "late end event superseded the user Next transition");
        queue.ObserveTrackChanged("b", 11, 1, DateTimeOffset.UtcNow);
        Assert(queue.Snapshot.CurrentIndex == 1, "user Next did not synchronize the queue index");
    }

    private static void ShuffleUsesStoredOccurrenceOrder()
    {
        var tracks = new[] { "a", "b", "c", "d" };
        var queue = new ApplicationPlaybackQueue();
        queue.Replace("ctx", tracks, "a", 0, true, 1, 8675309);
        var order = queue.Snapshot.ShuffleOrder;
        queue.ObserveTrackChanged("a", 10, 1, DateTimeOffset.UtcNow);
        var transition = queue.BeginEndOfTrack("a", 10, 1, DateTimeOffset.UtcNow);
        Assert(transition.ExpectedIndex == order[1], "shuffle did not follow its stored occurrence order");
        Assert(queue.Snapshot.ShuffleSeed == 8675309, "shuffle seed was not retained in the snapshot");
    }

    private static void RepeatOneReloadsCurrentOccurrence()
    {
        var queue = ReadyQueue(new[] { "a", "b" }, "a", 10, 1, repeatMode: 2);
        var transition = queue.BeginEndOfTrack("a", 10, 1, DateTimeOffset.UtcNow);
        Assert(transition.ExpectedIndex == 0 && transition.ExpectedUri == "a",
            "repeat-one did not select the current occurrence");
    }

    private static void RepeatContextWraps()
    {
        var queue = ReadyQueue(new[] { "a", "b", "c" }, "c", 10, 1, repeatMode: 1, startIndex: 2);
        var transition = queue.BeginEndOfTrack("c", 10, 1, DateTimeOffset.UtcNow);
        Assert(transition.ExpectedIndex == 0 && transition.ExpectedUri == "a",
            "repeat-context did not wrap to the first occurrence");
    }

    private static void FinalTrackWithoutRepeatCompletes()
    {
        var queue = ReadyQueue(new[] { "a", "b" }, "b", 10, 1, startIndex: 1);
        var transition = queue.BeginEndOfTrack("b", 10, 1, DateTimeOffset.UtcNow);
        Assert(transition.ExpectedUri == null && transition.FailureReason == "end-of-queue",
            "final track produced a continuation with repeat disabled");
        ApplicationQueueTransition ignored;
        Assert(!queue.TryClaimFallback(transition.QueueGenerationId, transition.TransitionId, out ignored),
            "final track incorrectly claimed fallback");
    }

    private static void UnavailableNextTrackIsSkipped()
    {
        var queue = ReadyQueue(new[] { "a", "b", "c" }, "a", 10, 1);
        var transition = queue.BeginEndOfTrack("a", 10, 1, DateTimeOffset.UtcNow);
        var next = queue.MarkPendingExpectedUnavailable("b", 1);
        Assert(next != null && next.ExpectedUri == "c", "unavailable next track was not skipped");
        ApplicationQueueTransition claimed;
        Assert(queue.TryClaimFallback(transition.QueueGenerationId, transition.TransitionId, out claimed) &&
            claimed.ExpectedUri == "c", "fallback did not advance to the next playable occurrence");
    }

    private static void DuplicateTrackUrisAdvanceByOccurrence()
    {
        var queue = ReadyQueue(new[] { "same", "same", "after" }, "same", 10, 1, startIndex: 0);
        var first = queue.BeginEndOfTrack("same", 10, 1, DateTimeOffset.UtcNow);
        Assert(first.ExpectedIndex == 1 && first.ExpectedUri == "same",
            "duplicate URI did not retain the next occurrence index");
        queue.ObserveTrackChanged("same", 11, 1, DateTimeOffset.UtcNow);
        Assert(queue.Snapshot.CurrentIndex == 1, "duplicate TrackChanged collapsed to the first occurrence");
        var second = queue.BeginEndOfTrack("same", 11, 1, DateTimeOffset.UtcNow);
        Assert(second.ExpectedIndex == 2 && second.ExpectedUri == "after",
            "queue did not advance beyond the confirmed duplicate occurrence");
    }

    private static void ReconnectImmediatelyBeforeEndRebasesGeneration()
    {
        var queue = ReadyQueue(new[] { "a", "b" }, "a", 10, 1);
        var oldTransition = queue.BeginEndOfTrack("a", 10, 1, DateTimeOffset.UtcNow);
        var replacementGeneration = queue.RebaseForReconnect(2);
        ApplicationQueueTransition ignored;
        Assert(!queue.TryClaimFallback(oldTransition.QueueGenerationId, oldTransition.TransitionId, out ignored),
            "pre-reconnect transition survived generation rebase");
        var current = queue.BeginEndOfTrack("a", 20, 2, DateTimeOffset.UtcNow);
        Assert(current.IsValid && current.ExpectedUri == "b" && current.QueueGenerationId == replacementGeneration,
            "reconnected queue could not continue before TrackChanged confirmation");
        var result = queue.ObserveTrackChanged("b", 21, 2, DateTimeOffset.UtcNow);
        Assert(result != null && result.ActualChangedUri == "b",
            "reconnect-boundary transition did not produce a result");
        Assert(queue.Snapshot.CurrentIndex == 1, "reconnected queue did not synchronize its next track");
    }

    private static void StaleEndFromPreviousQueueGenerationIsIgnored()
    {
        var queue = ReadyQueue(new[] { "a", "b" }, "a", 10, 1);
        queue.Replace("new", new[] { "a", "c" }, "a", 0, false, 0, 7);
        var stale = queue.BeginEndOfTrack("a", 10, 1, DateTimeOffset.UtcNow);
        Assert(!stale.IsValid && stale.FailureReason == "stale-end-of-track",
            "stale end event from the prior queue was accepted");
        Assert(queue.Snapshot.CurrentIndex == 0, "stale end event moved the replacement queue index");
        queue.ObserveTrackChanged("a", 20, 2, DateTimeOffset.UtcNow);
        Assert(queue.BeginEndOfTrack("a", 20, 2, DateTimeOffset.UtcNow).IsValid,
            "replacement queue did not accept its confirmed end event");
    }

    private static ApplicationPlaybackQueue ReadyQueue(
        string[] tracks,
        string startUri,
        ulong playRequestId,
        long sessionGeneration,
        int repeatMode = 0,
        int startIndex = 0)
    {
        var queue = new ApplicationPlaybackQueue();
        queue.Replace("ctx", tracks, startUri, startIndex, false, repeatMode, 1234);
        queue.ObserveTrackChanged(startUri, playRequestId, sessionGeneration, DateTimeOffset.UtcNow);
        return queue;
    }

    private static ProducerHealthMonitor ConnectedExpectedHealth()
    {
        var health = new ProducerHealthMonitor();
        health.SetPlaybackExpected(true, 1, 1, 100, 100);
        Assert(health.SetSessionState(true, 1, 100), "health setup session was rejected");
        return health;
    }

    private static AudioTransitionStateMachine ActiveMachine(ulong generation, ulong request)
    {
        var machine = NewMachine();
        machine.BeginTransition(false, true);
        machine.ObserveLoading(request);
        Assert(machine.ObserveTrack(request, generation, 0), "active marker setup failed");
        Assert(machine.RequestPlayback(request, generation), "active playback setup failed");
        Assert(Evaluate(machine, generation, 0, PreRollBytes, 0).ShouldResume,
            "active playback setup did not resume");
        return machine;
    }

    private static AudioTransitionStateMachine NewMachine()
    {
        return new AudioTransitionStateMachine(PreRollBytes);
    }

    private static AudioTransitionEvaluation Evaluate(
        AudioTransitionStateMachine machine,
        ulong generation,
        ulong generationStart,
        ulong writeSequence,
        ulong readSequence)
    {
        return machine.Evaluate(new AudioProducerState
        {
            Generation = generation,
            GenerationStartSequence = generationStart,
            WriteSequence = writeSequence
        }, readSequence);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
