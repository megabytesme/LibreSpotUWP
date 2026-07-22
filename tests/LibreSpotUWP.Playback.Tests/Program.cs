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
            ReconnectDuringLoading
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
