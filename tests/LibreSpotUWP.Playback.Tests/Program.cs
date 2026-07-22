using System;
using LibreSpotUWP.Services;

internal static class Program
{
    private const ulong QuantumBytes = 2646; // 10 ms: 44.1 kHz, stereo, packed 24-bit PCM.
    private const ulong PreRollBytes = QuantumBytes * 20; // 200 ms.

    private static int Main()
    {
        var tests = new Action[]
        {
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
            LumiaStyleSequenceDoesNotUnderrun
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
