using System;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LibreSpotUWP.Helpers;
using LibreSpotUWP.Models;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using static LibreSpotUWP.Interop.Librespot;

namespace LibreSpotUWP.Services
{
    public sealed class ProducerStalledEventArgs : EventArgs
    {
        public long GraphInstanceId { get; set; }
        public long SessionGeneration { get; set; }
        public ulong TrackGeneration { get; set; }
        public string Reason { get; set; }
        public ulong AvailableBytes { get; set; }
        public long LastPcmWriteMs { get; set; }
        public long LastSuccessfulStreamReadMs { get; set; }
    }

    public sealed class LibrespotRingBufferPlayer : IDisposable
    {
        private static long _nextGraphInstanceId;
        private static int _liveGraphCount;

        private readonly AudioEncodingProperties _props;
        private readonly string _outputDeviceId;
        private readonly long _graphInstanceId;
        private readonly AsyncOperationOnce _initialization = new AsyncOperationOnce();
        private readonly object _disposeSync = new object();
        private Task _disposeTask;
        private readonly CallbackLifetimeGate _callbackLifetime = new CallbackLifetimeGate();
        private readonly ProducerHealthMonitor _producerHealth = new ProducerHealthMonitor();
        private AudioGraph _graph;
        private AudioFrameInputNode _inputNode;
        private EchoEffectDefinition _echoEffect;
        private EqualizerEffectDefinition _equalizerEffect;
        private LimiterEffectDefinition _limiterEffect;
        private ReverbEffectDefinition _reverbEffect;
        private bool _audioEffectsConfigured;
        private double _outgoingGain = 1.0;
        private string _audioEffectsPreset = "None";
        private EqualizerBandRange[] _equalizerBandRanges = Array.Empty<EqualizerBandRange>();

        private IntPtr _bufferPtr;
        private int _capacityBytes;
        private int _frameSize;
        private long _readSequence;
        private long _activeGeneration;
        private long _activeBoundarySequence = -1;
        private int _consumerEnabled;
        private int _inputNodeRunning;
        private int _graphRunning;
        private int _transitionPollRunning;
        private int _watchdogRunning;
        private int _graphCounted;
        private int _disposed;
        private string _audioHealthTrackUri = "(unknown)";
        private Timer _telemetryTimer;
        private Timer _transitionTimer;
        private Timer _producerWatchdogTimer;
        private readonly object _transitionLock = new object();
        private readonly SemaphoreSlim _lifecycleGate = new SemaphoreSlim(1, 1);
        private AudioTransitionStateMachine _transitionState;
        private TaskCompletionSource<bool> _playbackReady;
        private TransitionTelemetry _transitionTelemetry;
        private long _transitionMinimumAvailableBytes = long.MaxValue;
        private long _transitionInsertedSilenceBytes;
        private long _lastQuantumTimestamp;
        private long _telemetryQuantumCount;
        private long _telemetryRequestedBytes;
        private long _telemetryCopiedBytes;
        private long _telemetryAvailableBytes;
        private long _telemetrySilenceFillQuantumCount;
        private long _telemetrySilenceFillBytes;
        private long _telemetryMaxSilenceFillBytes;
        private long _telemetryZeroAvailableQuantumCount;
        private long _telemetryLateQuantumCount;
        private long _telemetryMaxQuantumElapsedTicks;
        private long _telemetryFramePoolMissCount;
        private long _idleQuantumCallbackCount;
        private int _telemetryMinAvailableBytes = int.MaxValue;
        private int _telemetryMaxAvailableBytes;

        private const int PoolSize = 6;
        private readonly PooledFrame[] _framePool = new PooledFrame[PoolSize];
        private int _nextFramePoolIndex = -1;
        private const int TelemetryIntervalMs = 10000;
        private const int ProducerWatchdogIntervalMs = 500;
        internal const int ProducerStallIntervalMs = 6000;
        internal const int ProducerLowBufferMilliseconds = 120;
        private const int TransitionPollIntervalMs = 10;
        // 200 ms is long enough to absorb scheduling jitter seen on Lumia 830
        // class devices while leaving headroom in the ~495 ms native ring.
        public const int PreRollMilliseconds = 200;
        private const int SignificantLateCallbackCount = 10;
        private const double SignificantLateCallbackGapMs = 40.0;
        private const int DefaultEqualizerBandCount = 5;
        private const double EqualizerMinLinearGain = 0.126;
        private const double EqualizerMaxLinearGain = 7.94;
        private const double EqualizerDefaultLinearGain = 1.0;
        private uint _maxFrameBytes;

        public long GraphInstanceId => _graphInstanceId;
        public long IdleQuantumCallbackCount => Interlocked.Read(ref _idleQuantumCallbackCount);
        internal static int LiveGraphCount => Volatile.Read(ref _liveGraphCount);

        public event EventHandler<ProducerStalledEventArgs> ProducerStalled;

        private sealed class TransitionTelemetry
        {
            public long Id;
            public string OldTrackUri;
            public string NewTrackUri;
            public string Reason;
            public long StartedTimestamp;
            public long FirstPcmTimestamp;
            public double PreRollMilliseconds;
            public bool WasPreloaded;
        }

        private class PooledFrame : IDisposable
        {
            public AudioFrame Frame { get; }
            public uint Capacity { get; }
            public int InUse;

            public PooledFrame(uint capacity)
            {
                Frame = new AudioFrame(capacity);
                Capacity = capacity;
            }

            public void Dispose() => Frame.Dispose();
        }

        public LibrespotRingBufferPlayer(AudioEncodingProperties props, string outputDeviceId)
        {
            _props = props;
            _outputDeviceId = outputDeviceId ?? string.Empty;
            _graphInstanceId = Interlocked.Increment(ref _nextGraphInstanceId);
        }

        public Task InitializeAsync()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(LibrespotRingBufferPlayer));

            return _initialization.Run(InitializeCoreAsync);
        }

        private async Task InitializeCoreAsync()
        {
            await _lifecycleGate.WaitAsync();
            AudioGraph candidateGraph = null;
            AudioFrameInputNode candidateInputNode = null;
            PooledFrame[] candidateFrames = null;
            try
            {
                ThrowIfDisposed();
                using (var process = Process.GetCurrentProcess())
                    process.PriorityClass = ProcessPriorityClass.High;

                await WaitForRingBufferAsync();
                ThrowIfDisposed();

                _capacityBytes = (int)librespot_audio_get_capacity().ToUInt32();
                var producer = GetProducerState();
                _readSequence = (long)producer.WriteSequence;
                librespot_audio_set_read_sequence(producer.WriteSequence);
                _frameSize = (int)(_props.ChannelCount * (_props.BitsPerSample / 8));
                var preRollBytes = MillisecondsToAlignedBytes(PreRollMilliseconds);
                _transitionState = new AudioTransitionStateMachine((ulong)preRollBytes);

                var settings = new AudioGraphSettings(AudioRenderCategory.Media)
                {
                    EncodingProperties = _props,
                    QuantumSizeSelectionMode = QuantumSizeSelectionMode.SystemDefault
                };
                var outputDevice = await TryGetOutputDeviceAsync(_outputDeviceId);
                ThrowIfDisposed();
                if (outputDevice != null)
                    settings.PrimaryRenderDevice = outputDevice;

                var result = await AudioGraph.CreateAsync(settings);
                if (result.Status != AudioGraphCreationStatus.Success && outputDevice != null)
                {
                    result.Graph?.Dispose();
                    LogService.Warn($"[LibrespotRingBufferPlayer.InitializeAsync] graphId={_graphInstanceId}, selected output '{_outputDeviceId}' failed ({result.Status}); retrying default output.");
                    settings.PrimaryRenderDevice = null;
                    result = await AudioGraph.CreateAsync(settings);
                }

                ThrowIfDisposed();
                if (result.Status != AudioGraphCreationStatus.Success || result.Graph == null)
                {
                    result.Graph?.Dispose();
                    throw new InvalidOperationException($"AudioGraph creation failed: {result.Status}");
                }

                candidateGraph = result.Graph;
                uint samplesPerQuantum = (uint)Math.Max(1, candidateGraph.SamplesPerQuantum);
                _maxFrameBytes = samplesPerQuantum * (uint)_frameSize;
                candidateFrames = new PooledFrame[PoolSize];
                for (int i = 0; i < PoolSize; i++)
                    candidateFrames[i] = new PooledFrame(_maxFrameBytes);

                var outResult = await candidateGraph.CreateDeviceOutputNodeAsync();
                ThrowIfDisposed();
                if (outResult.Status != AudioDeviceNodeCreationStatus.Success || outResult.DeviceOutputNode == null)
                    throw new InvalidOperationException($"Audio output node creation failed: {outResult.Status}");

                candidateInputNode = candidateGraph.CreateFrameInputNode(_props);
                candidateInputNode.OutgoingGain = _outgoingGain;
                candidateInputNode.AddOutgoingConnection(outResult.DeviceOutputNode);
                candidateInputNode.Stop();
                candidateGraph.Stop();
                candidateInputNode.QuantumStarted += OnQuantumStarted;

                _graph = candidateGraph;
                _inputNode = candidateInputNode;
                for (int i = 0; i < PoolSize; i++)
                    _framePool[i] = candidateFrames[i];
                candidateGraph = null;
                candidateInputNode = null;
                candidateFrames = null;

                ApplyAudioEffectsPreset(_audioEffectsPreset);
                _telemetryTimer = new Timer(_ => FlushAudioTelemetry(), null, Timeout.Infinite, Timeout.Infinite);
                _transitionTimer = new Timer(_ => RunTransitionPoll(), null, Timeout.Infinite, Timeout.Infinite);
                _producerWatchdogTimer = new Timer(_ => CheckProducerHealth(), null, Timeout.Infinite, Timeout.Infinite);
                Interlocked.Exchange(ref _graphCounted, 1);
                int liveGraphs = Interlocked.Increment(ref _liveGraphCount);
                LogService.Info($"[LibrespotRingBufferPlayer.InitializeAsync] graphId={_graphInstanceId}, liveGraphs={liveGraphs}, AudioGraph initialized stopped and consumer gated, sampleRate={_props.SampleRate}, channels={_props.ChannelCount}, bits={_props.BitsPerSample}, samplesPerQuantum={_graph.SamplesPerQuantum}, frameBytes={_frameSize}, maxFrameBytes={_maxFrameBytes}, capacityBytes={_capacityBytes}, capacityMs={BytesToMilliseconds(_capacityBytes):F1}, preRollMs={PreRollMilliseconds}.");
            }
            catch
            {
                candidateInputNode?.Dispose();
                candidateGraph?.Dispose();
                if (candidateFrames != null)
                {
                    foreach (var frame in candidateFrames)
                        frame?.Dispose();
                }
                throw;
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        private unsafe void OnQuantumStarted(AudioFrameInputNode sender, FrameInputNodeQuantumStartedEventArgs args)
        {
            if (!_callbackLifetime.TryEnter())
                return;

            try
            {
                int samplesNeeded = args.RequiredSamples;
                if (samplesNeeded <= 0)
                    return;
                if (Volatile.Read(ref _consumerEnabled) == 0)
                {
                    Interlocked.Increment(ref _idleQuantumCallbackCount);
                    return;
                }

                int bytesRequested = samplesNeeded * _frameSize;
                var producer = GetProducerState();
                long readSequence = Volatile.Read(ref _readSequence);
                long writeSequence = (long)producer.WriteSequence;
                long availableLong = Math.Max(0L, Math.Min(_capacityBytes, writeSequence - readSequence));
                bool generationBoundaryLimited = false;

                long activeGeneration = Volatile.Read(ref _activeGeneration);
                long boundarySequence = Volatile.Read(ref _activeBoundarySequence);
                if (boundarySequence < 0 && producer.Generation > (ulong)Math.Max(0, activeGeneration))
                    boundarySequence = (long)producer.GenerationStartSequence;

                if (boundarySequence >= 0)
                {
                    if (readSequence >= boundarySequence)
                    {
                        Interlocked.Exchange(ref _consumerEnabled, 0);
                        return;
                    }

                    long bytesToBoundary = boundarySequence - readSequence;
                    generationBoundaryLimited = bytesToBoundary < bytesRequested;
                    if (availableLong > bytesToBoundary)
                        availableLong = bytesToBoundary;
                }

                int available = (int)availableLong;

                int bytesToCopy = Math.Min(available, bytesRequested);
                bytesToCopy -= bytesToCopy % _frameSize;

                RecordAudioTelemetry(samplesNeeded, bytesRequested, bytesToCopy, available, generationBoundaryLimited);

                PooledFrame pooled = TryAcquirePooledFrame();
                if (pooled == null || pooled.Capacity < bytesRequested)
                {
                    Interlocked.Increment(ref _telemetryFramePoolMissCount);
                    if (pooled != null)
                        Volatile.Write(ref pooled.InUse, 0);
                    return;
                }

                try
                {
                    using (AudioBuffer buffer = pooled.Frame.LockBuffer(AudioBufferAccessMode.Write))
                    using (IMemoryBufferReference reference = buffer.CreateReference())
                    {
                        if (reference is IMemoryBufferByteAccess byteAccess)
                        {
                            byteAccess.GetBuffer(out IntPtr dataInPtr, out uint capacity);
                            byte* dest = (byte*)dataInPtr;
                            byte* srcBase = (byte*)_bufferPtr;

                            if (bytesToCopy > 0)
                            {
                                int readPos = (int)((ulong)readSequence % (ulong)_capacityBytes);
                                int firstChunkSize = Math.Min(bytesToCopy, _capacityBytes - readPos);
                                Buffer.MemoryCopy(srcBase + readPos, dest, capacity, firstChunkSize);

                                if (bytesToCopy > firstChunkSize)
                                {
                                    Buffer.MemoryCopy(srcBase, dest + firstChunkSize, capacity - (uint)firstChunkSize, bytesToCopy - firstChunkSize);
                                }
                            }

                            for (int i = bytesToCopy; i < bytesRequested; i++)
                                dest[i] = 0;

                            buffer.Length = (uint)bytesRequested;
                        }
                    }

                    sender.AddFrame(pooled.Frame);
                }
                finally
                {
                    Volatile.Write(ref pooled.InUse, 0);
                }

                if (bytesToCopy > 0)
                {
                    long nextReadSequence = readSequence + bytesToCopy;
                    Interlocked.Exchange(ref _readSequence, nextReadSequence);
                    librespot_audio_set_read_sequence((ulong)nextReadSequence);
                    UpdateMin(ref _transitionMinimumAvailableBytes, available);

                    if (boundarySequence >= 0 && nextReadSequence >= boundarySequence)
                        Interlocked.Exchange(ref _consumerEnabled, 0);
                }
            }
            finally
            {
                _callbackLifetime.Exit();
            }
        }

        public long BeginTransition(string reason, string oldTrackUri, string newTrackUri, bool preserveCurrent, bool shouldPlay)
        {
            FlushAudioTelemetry(force: true);
            FlushTransitionTelemetry();

            long id;
            bool drainCurrentGeneration;
            lock (_transitionLock)
            {
                drainCurrentGeneration = AudioTransitionStateMachine.ShouldDrainCurrentGeneration(
                    preserveCurrent,
                    (ulong)Math.Max(0, Volatile.Read(ref _activeGeneration)),
                    Volatile.Read(ref _consumerEnabled) != 0);
                id = _transitionState.BeginTransition(drainCurrentGeneration, shouldPlay);
                ReplacePlaybackReadyLocked();
                _transitionTelemetry = new TransitionTelemetry
                {
                    Id = id,
                    OldTrackUri = NormalizeTrackUri(oldTrackUri),
                    NewTrackUri = NormalizeTrackUri(newTrackUri),
                    Reason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason,
                    StartedTimestamp = Stopwatch.GetTimestamp()
                };
                Interlocked.Exchange(ref _transitionMinimumAvailableBytes, long.MaxValue);
                Interlocked.Exchange(ref _transitionInsertedSilenceBytes, 0);
            }

            Interlocked.Exchange(ref _lastQuantumTimestamp, 0);
            if (!drainCurrentGeneration)
            {
                Interlocked.Exchange(ref _consumerEnabled, 0);
                _ = SetInputNodeRunningAsync(false, id);
            }

            ResetPlaybackExpectation(shouldPlay);

            QueueTransitionPoll();
            return id;
        }

        public long BeginAutomaticTransition(string oldTrackUri, bool shouldPlay)
        {
            lock (_transitionLock)
            {
                if (_transitionState.HasPendingTransition)
                    return _transitionState.CurrentTransitionId;
            }

            return BeginTransition("end-of-track", oldTrackUri, null, true, shouldPlay);
        }

        public bool ObserveLoading(ulong playRequestId)
        {
            bool hadPendingTransition;
            bool drainCurrentGeneration;
            bool accepted;
            long transitionId;
            lock (_transitionLock)
            {
                hadPendingTransition = _transitionState.HasPendingTransition;
                drainCurrentGeneration = AudioTransitionStateMachine.ShouldDrainCurrentGeneration(
                    preserveCurrent: false,
                    activeGeneration: _transitionState.ActiveGeneration,
                    consumerEnabled: Volatile.Read(ref _consumerEnabled) != 0);
                accepted = _transitionState.ObserveLoading(playRequestId, drainCurrentGeneration);
                transitionId = _transitionState.CurrentTransitionId;
                if (accepted && !hadPendingTransition)
                {
                    ReplacePlaybackReadyLocked();
                    _transitionTelemetry = new TransitionTelemetry
                    {
                        Id = transitionId,
                        OldTrackUri = NormalizeTrackUri(_audioHealthTrackUri),
                        NewTrackUri = "(unknown)",
                        Reason = "external-load",
                        StartedTimestamp = Stopwatch.GetTimestamp()
                    };
                    Interlocked.Exchange(ref _transitionMinimumAvailableBytes, long.MaxValue);
                    Interlocked.Exchange(ref _transitionInsertedSilenceBytes, 0);
                }
            }

            if (accepted && !hadPendingTransition)
            {
                if (!drainCurrentGeneration)
                {
                    Interlocked.Exchange(ref _consumerEnabled, 0);
                    _ = SetInputNodeRunningAsync(false, transitionId);
                }
                Interlocked.Exchange(ref _lastQuantumTimestamp, 0);
                QueueTransitionPoll();
            }

            return accepted;
        }

        public bool ObserveTrackChanged(string trackUri, ulong playRequestId, ulong audioGeneration, bool wasPreloaded)
        {
            var producer = GetProducerState();
            if (producer.Generation != audioGeneration)
                return false;

            bool needsImplicitTransition;
            lock (_transitionLock)
                needsImplicitTransition = !_transitionState.HasPendingTransition;
            if (needsImplicitTransition)
            {
                BeginTransition(
                    "automatic-track-change",
                    _audioHealthTrackUri,
                    trackUri,
                    preserveCurrent: true,
                    shouldPlay: true);
            }

            bool accepted;
            lock (_transitionLock)
            {
                accepted = _transitionState.ObserveTrack(
                    playRequestId,
                    audioGeneration,
                    producer.GenerationStartSequence);
                if (accepted && _transitionTelemetry != null)
                {
                    _transitionTelemetry.NewTrackUri = NormalizeTrackUri(trackUri);
                    _transitionTelemetry.WasPreloaded = wasPreloaded;
                }
            }

            if (accepted)
            {
                _audioHealthTrackUri = NormalizeTrackUri(trackUri);
                _producerHealth.SetTrackGeneration(
                    audioGeneration,
                    producer.WriteSequence,
                    GetMonotonicMilliseconds());
                QueueTransitionPoll();
            }
            return accepted;
        }

        public bool ObserveSeek(ulong playRequestId, ulong audioGeneration)
        {
            var producer = GetProducerState();
            if (producer.Generation != audioGeneration)
                return false;

            bool needsImplicitTransition;
            bool shouldPlay;
            lock (_transitionLock)
            {
                needsImplicitTransition = !_transitionState.HasPendingTransition;
                shouldPlay = _transitionState.DesiredPlaying ||
                    Volatile.Read(ref _consumerEnabled) != 0;
            }

            // Local UI seeks declare their transition before issuing the
            // command. A seek arriving from another Connect client does not,
            // so establish the missing PCM-generation handoff here instead of
            // rejecting and permanently gating its replacement audio.
            if (needsImplicitTransition)
            {
                BeginTransition(
                    "external-seek",
                    _audioHealthTrackUri,
                    _audioHealthTrackUri,
                    preserveCurrent: false,
                    shouldPlay: shouldPlay);
            }

            bool accepted;
            lock (_transitionLock)
                accepted = _transitionState.ObserveSeek(playRequestId, audioGeneration, producer.GenerationStartSequence);

            if (accepted)
                QueueTransitionPoll();
            return accepted;
        }

        public Task<bool> RequestPlaybackAsync(ulong playRequestId, ulong audioGeneration)
        {
            Task<bool> task;
            lock (_transitionLock)
            {
                if (!_transitionState.RequestPlayback(playRequestId, audioGeneration))
                    return Task.FromResult(false);

                if (!_transitionState.HasPendingTransition &&
                    audioGeneration != 0 &&
                    audioGeneration == _transitionState.ActiveGeneration &&
                    Volatile.Read(ref _consumerEnabled) != 0)
                {
                    return Task.FromResult(true);
                }

                if (_playbackReady == null || _playbackReady.Task.IsCompleted)
                    _playbackReady = new TaskCompletionSource<bool>();
                task = _playbackReady.Task;
            }

            ResetPlaybackExpectation(true);
            QueueTransitionPoll();
            return task;
        }

        public bool HasPendingTransition
        {
            get
            {
                lock (_transitionLock)
                    return _transitionState != null && _transitionState.HasPendingTransition;
            }
        }

        public bool IsEventForPendingTransition(ulong playRequestId, ulong audioGeneration)
        {
            lock (_transitionLock)
            {
                return _transitionState != null &&
                    _transitionState.IsPendingEvent(playRequestId, audioGeneration);
            }
        }

        public bool IsEventForActiveGeneration(ulong playRequestId, ulong audioGeneration)
        {
            lock (_transitionLock)
            {
                return _transitionState != null &&
                    _transitionState.IsActiveEvent(playRequestId, audioGeneration);
            }
        }

        public Task PauseAsync()
        {
            lock (_transitionLock)
            {
                _transitionState.Pause();
                CompletePlaybackReadyLocked(false);
            }
            Interlocked.Exchange(ref _consumerEnabled, 0);
            ResetPlaybackExpectation(false);

            // Keep the AudioGraph alive for an ordinary pause. Stopping a live UWP
            // graph can block while a quantum callback is in flight, which used to
            // hold the playback command gate before the native pause was issued.
            // The gated callback supplies silence, and RequestPlaybackAsync can
            // re-enable the consumer without rebuilding or restarting the graph.
            _telemetryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _producerWatchdogTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            Interlocked.Exchange(ref _lastQuantumTimestamp, 0);
            return Task.CompletedTask;
        }

        public void Stop()
        {
            long transitionId;
            lock (_transitionLock)
            {
                _transitionState?.Cancel();
                CompletePlaybackReadyLocked(false);
                transitionId = _transitionState?.CurrentTransitionId ?? 0;
            }
            Interlocked.Exchange(ref _consumerEnabled, 0);
            ResetPlaybackExpectation(false);
            _ = SetInputNodeRunningAsync(false, transitionId);
            Interlocked.Exchange(ref _lastQuantumTimestamp, 0);
            FlushAudioTelemetry(force: true);
            FlushTransitionTelemetry();
        }

        private void QueueTransitionPoll()
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                _transitionState == null)
            {
                return;
            }

            _transitionTimer?.Change(0, Timeout.Infinite);
        }

        private void RunTransitionPoll()
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                Interlocked.CompareExchange(ref _transitionPollRunning, 1, 0) != 0)
            {
                return;
            }

            _ = PollTransitionAsync();
        }

        private async Task PollTransitionAsync()
        {
            try
            {
                var producer = GetProducerState();
                var readSequence = (ulong)Math.Max(0, Volatile.Read(ref _readSequence));
                AudioTransitionEvaluation evaluation;

                lock (_transitionLock)
                {
                    if (_transitionTelemetry != null &&
                        _transitionTelemetry.FirstPcmTimestamp == 0 &&
                        _transitionState.PendingGeneration != 0 &&
                        producer.Generation == _transitionState.PendingGeneration &&
                        producer.WriteSequence > producer.GenerationStartSequence)
                    {
                        _transitionTelemetry.FirstPcmTimestamp = Stopwatch.GetTimestamp();
                    }

                    evaluation = _transitionState.Evaluate(producer, readSequence);
                    if (evaluation.ShouldResume && _transitionTelemetry != null)
                    {
                        _transitionTelemetry.PreRollMilliseconds = BytesToMilliseconds((long)evaluation.AvailableBytes);
                    }
                }

                if (evaluation.ShouldDiscard)
                {
                    Interlocked.Exchange(ref _readSequence, (long)evaluation.DiscardSequence);
                    librespot_audio_set_read_sequence(evaluation.DiscardSequence);
                    readSequence = evaluation.DiscardSequence;
                }

                Interlocked.Exchange(
                    ref _activeBoundarySequence,
                    evaluation.ActiveBoundarySequence == AudioTransitionStateMachine.NoBoundary
                        ? -1
                        : (long)evaluation.ActiveBoundarySequence);

                if (evaluation.ShouldResume && Volatile.Read(ref _consumerEnabled) == 0)
                {
                    if (evaluation.ActiveStartSequence != 0 || readSequence == 0)
                    {
                        Interlocked.Exchange(ref _readSequence, (long)evaluation.ActiveStartSequence);
                        librespot_audio_set_read_sequence(evaluation.ActiveStartSequence);
                    }

                    Interlocked.Exchange(ref _activeGeneration, (long)evaluation.ActiveGeneration);
                    Interlocked.Exchange(ref _activeBoundarySequence, -1);
                    UpdateMin(ref _transitionMinimumAvailableBytes, (long)evaluation.AvailableBytes);

                    if (await SetInputNodeRunningAsync(true, evaluation.TransitionId))
                    {
                        bool completedTransition = false;
                        lock (_transitionLock)
                        {
                            if (_transitionState.CurrentTransitionId == evaluation.TransitionId)
                            {
                                CompletePlaybackReadyLocked(true);
                                completedTransition = true;
                            }
                        }

                        if (completedTransition)
                            FlushTransitionTelemetry(evaluation.TransitionId);
                    }
                }
                else if (evaluation.ShouldGate || Volatile.Read(ref _consumerEnabled) == 0)
                {
                    await SetInputNodeRunningAsync(false, evaluation.TransitionId);
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"[LibrespotRingBufferPlayer.PollTransitionAsync] Unable to update transition state: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _transitionPollRunning, 0);
                bool shouldPollAgain;
                lock (_transitionLock)
                    shouldPollAgain = _transitionState != null && _transitionState.HasPendingTransition;

                if (shouldPollAgain && Volatile.Read(ref _disposed) == 0)
                    _transitionTimer?.Change(TransitionPollIntervalMs, Timeout.Infinite);
            }
        }

        private async Task<bool> SetInputNodeRunningAsync(bool running, long expectedTransitionId)
        {
            await _lifecycleGate.WaitAsync();
            try
            {
                if (Volatile.Read(ref _disposed) != 0 || _inputNode == null)
                    return false;

                lock (_transitionLock)
                {
                    if (expectedTransitionId != 0 &&
                        _transitionState.CurrentTransitionId != expectedTransitionId)
                    {
                        return false;
                    }
                }

                if (running)
                {
                    Interlocked.Exchange(ref _consumerEnabled, 1);
                    if (Interlocked.CompareExchange(ref _graphRunning, 1, 0) == 0)
                        _graph.Start();
                    if (Interlocked.CompareExchange(ref _inputNodeRunning, 1, 0) == 0)
                        _inputNode.Start();
                    _telemetryTimer?.Change(TelemetryIntervalMs, TelemetryIntervalMs);
                    _producerWatchdogTimer?.Change(ProducerWatchdogIntervalMs, ProducerWatchdogIntervalMs);
                }
                else
                {
                    Interlocked.Exchange(ref _consumerEnabled, 0);
                    if (Interlocked.CompareExchange(ref _inputNodeRunning, 0, 1) == 1)
                        _inputNode.Stop();
                    if (Interlocked.CompareExchange(ref _graphRunning, 0, 1) == 1)
                        _graph.Stop();
                    _telemetryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                    if (!_producerHealth.Snapshot.PlaybackExpected)
                        _producerWatchdogTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                }

                Interlocked.Exchange(ref _lastQuantumTimestamp, 0);
                return true;
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        private void ReplacePlaybackReadyLocked()
        {
            CompletePlaybackReadyLocked(false);
            _playbackReady = new TaskCompletionSource<bool>();
        }

        private void CompletePlaybackReadyLocked(bool result)
        {
            var ready = _playbackReady;
            if (ready != null && !ready.Task.IsCompleted)
                ready.TrySetResult(result);
        }

        public void SetSessionState(bool connected, long sessionGeneration)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            if (!_producerHealth.SetSessionState(
                connected,
                sessionGeneration,
                GetMonotonicMilliseconds()))
            {
                LogService.Info($"[LibrespotRingBufferPlayer.SetSessionState] graphId={_graphInstanceId}, ignoring stale sessionGeneration={sessionGeneration}, activeSessionGeneration={_producerHealth.Snapshot.SessionGeneration}.");
                return;
            }

            if (_producerHealth.Snapshot.PlaybackExpected)
                _producerWatchdogTimer?.Change(0, ProducerWatchdogIntervalMs);
        }

        public void ReportTransportFailure(long sessionGeneration, string reason)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            if (!_producerHealth.ReportFatal(sessionGeneration, reason))
            {
                LogService.Info($"[LibrespotRingBufferPlayer.ReportTransportFailure] graphId={_graphInstanceId}, ignoring stale failure sessionGeneration={sessionGeneration}, activeSessionGeneration={_producerHealth.Snapshot.SessionGeneration}, reason={reason}.");
                return;
            }

            if (_producerHealth.Snapshot.PlaybackExpected)
                _producerWatchdogTimer?.Change(0, ProducerWatchdogIntervalMs);
        }

        internal ProducerHealthSnapshot GetProducerHealthSnapshot()
        {
            return _producerHealth.Snapshot;
        }

        private void ResetPlaybackExpectation(bool expected)
        {
            var snapshot = _producerHealth.Snapshot;
            var producer = _transitionState == null ? default(AudioProducerState) : GetProducerState();
            long nowMs = GetMonotonicMilliseconds();

            // A new explicit transition is a new expectation window even if the
            // session and track marker have not changed yet.
            _producerHealth.SetPlaybackExpected(
                false,
                snapshot.SessionGeneration,
                snapshot.TrackGeneration,
                producer.WriteSequence,
                nowMs);
            _producerHealth.SetPlaybackExpected(
                expected,
                snapshot.SessionGeneration,
                snapshot.TrackGeneration,
                producer.WriteSequence,
                nowMs);

            if (expected)
                _producerWatchdogTimer?.Change(ProducerWatchdogIntervalMs, ProducerWatchdogIntervalMs);
            else
                _producerWatchdogTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void CheckProducerHealth()
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                Interlocked.CompareExchange(ref _watchdogRunning, 1, 0) != 0)
            {
                return;
            }

            try
            {
                var producer = GetProducerState();
                long nowMs = GetMonotonicMilliseconds();
                _producerHealth.ObserveProducer(producer.WriteSequence, nowMs);

                ulong readSequence = (ulong)Math.Max(0, Volatile.Read(ref _readSequence));
                ulong availableBytes = producer.WriteSequence > readSequence
                    ? Math.Min((ulong)Math.Max(0, _capacityBytes), producer.WriteSequence - readSequence)
                    : 0;
                ulong lowBufferBytes = (ulong)Math.Max(0, MillisecondsToAlignedBytes(ProducerLowBufferMilliseconds));
                var decision = _producerHealth.Evaluate(
                    nowMs,
                    availableBytes,
                    lowBufferBytes,
                    ProducerStallIntervalMs);
                if (decision.ShouldRecover)
                    _ = QuiesceForProducerStallAsync(decision, availableBytes);
            }
            catch (Exception ex)
            {
                LogService.Warn($"[LibrespotRingBufferPlayer.CheckProducerHealth] graphId={_graphInstanceId}, watchdog sample failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _watchdogRunning, 0);
            }
        }

        private async Task QuiesceForProducerStallAsync(
            ProducerHealthDecision decision,
            ulong availableBytes)
        {
            if (!_producerHealth.IsDecisionCurrent(decision.Snapshot))
                return;

            _producerWatchdogTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            await SetInputNodeRunningAsync(false, 0);

            if (!_producerHealth.IsDecisionCurrent(decision.Snapshot))
            {
                QueueTransitionPoll();
                if (_producerHealth.Snapshot.PlaybackExpected)
                    _producerWatchdogTimer?.Change(ProducerWatchdogIntervalMs, ProducerWatchdogIntervalMs);
                return;
            }

            var snapshot = decision.Snapshot;
            LogService.Warn($"[LibrespotRingBufferPlayer.ProducerStall] graphId={_graphInstanceId}, sessionGeneration={snapshot.SessionGeneration}, trackGeneration={snapshot.TrackGeneration}, reason={decision.Reason}, availableMs={BytesToMilliseconds((long)availableBytes):F1}, lastPcmWriteMs={snapshot.LastPcmWriteMs}, lastSuccessfulStreamReadMs={snapshot.LastSuccessfulStreamReadMs}.");
            try
            {
                ProducerStalled?.Invoke(this, new ProducerStalledEventArgs
                {
                    GraphInstanceId = _graphInstanceId,
                    SessionGeneration = snapshot.SessionGeneration,
                    TrackGeneration = snapshot.TrackGeneration,
                    Reason = decision.Reason,
                    AvailableBytes = availableBytes,
                    LastPcmWriteMs = snapshot.LastPcmWriteMs,
                    LastSuccessfulStreamReadMs = snapshot.LastSuccessfulStreamReadMs
                });
            }
            catch (Exception ex)
            {
                LogService.Error(ex, $"[LibrespotRingBufferPlayer.ProducerStall] graphId={_graphInstanceId}, observer failed");
            }
        }

        private AudioProducerState GetProducerState()
        {
            librespot_audio_get_state(out ulong generation, out ulong generationStart, out ulong writeSequence);
            return new AudioProducerState
            {
                Generation = generation,
                GenerationStartSequence = generationStart,
                WriteSequence = writeSequence
            };
        }

        private static long GetMonotonicMilliseconds()
        {
            return (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        }

        private int MillisecondsToAlignedBytes(int milliseconds)
        {
            long bytes = (long)_props.SampleRate * _frameSize * milliseconds / 1000;
            bytes -= bytes % _frameSize;
            return (int)Math.Max(_frameSize, bytes);
        }

        private PooledFrame TryAcquirePooledFrame()
        {
            int first = Interlocked.Increment(ref _nextFramePoolIndex) & int.MaxValue;
            for (int offset = 0; offset < PoolSize; offset++)
            {
                var frame = _framePool[((first % PoolSize) + offset) % PoolSize];
                if (frame != null && Interlocked.CompareExchange(ref frame.InUse, 1, 0) == 0)
                    return frame;
            }

            return null;
        }

        private static string NormalizeTrackUri(string trackUri)
        {
            return string.IsNullOrWhiteSpace(trackUri) ? "(unknown)" : trackUri;
        }

        private void FlushTransitionTelemetry(long expectedTransitionId = 0)
        {
            TransitionTelemetry telemetry;
            long minimumBytes;
            long insertedSilenceBytes;
            lock (_transitionLock)
            {
                telemetry = _transitionTelemetry;
                if (expectedTransitionId != 0 &&
                    (telemetry == null || telemetry.Id != expectedTransitionId))
                {
                    return;
                }
                _transitionTelemetry = null;
                minimumBytes = Interlocked.Exchange(ref _transitionMinimumAvailableBytes, long.MaxValue);
                insertedSilenceBytes = Interlocked.Exchange(ref _transitionInsertedSilenceBytes, 0);
            }

            if (telemetry == null)
                return;

            if (minimumBytes == long.MaxValue)
                minimumBytes = 0;
            double firstPcmMs = telemetry.FirstPcmTimestamp > 0
                ? TicksToMilliseconds(telemetry.FirstPcmTimestamp - telemetry.StartedTimestamp)
                : -1.0;

            var health = _producerHealth.Snapshot;
            LogService.Info($"[LibrespotRingBufferPlayer.Transition] graphId={_graphInstanceId}, sessionGeneration={health.SessionGeneration}, trackGeneration={health.TrackGeneration}, transitionId={telemetry.Id}, oldTrack={telemetry.OldTrackUri}, newTrack={telemetry.NewTrackUri}, reason={telemetry.Reason}, timeToFirstPcmMs={firstPcmMs:F1}, preRollMs={telemetry.PreRollMilliseconds:F1}, minimumBufferFillMs={BytesToMilliseconds(minimumBytes):F1}, insertedSilenceMs={BytesToMilliseconds(insertedSilenceBytes):F1}, preloaded={telemetry.WasPreloaded}.");
        }

        public void SetOutgoingGain(double gain)
        {
            _outgoingGain = Math.Max(0d, gain);

            if (_inputNode != null)
                _inputNode.OutgoingGain = _outgoingGain;
        }

        public void SetAudioEffectsPreset(string preset)
        {
            _audioEffectsPreset = NormalizePreset(preset);
            ApplyAudioEffectsPreset(_audioEffectsPreset);
        }

        public EqualizerBandRange[] GetEqualizerBandRanges()
        {
            if (_equalizerBandRanges.Length == 0)
                _equalizerBandRanges = BuildEqualizerBandRanges(DefaultEqualizerBandCount);

            return _equalizerBandRanges
                .Select(range => new EqualizerBandRange
                {
                    MinimumGain = range.MinimumGain,
                    MaximumGain = range.MaximumGain
                })
                .ToArray();
        }

        public void Dispose()
        {
            _ = DisposeAsync();
        }

        public Task DisposeAsync()
        {
            lock (_disposeSync)
            {
                if (_disposeTask != null)
                    return _disposeTask;

                Interlocked.Exchange(ref _disposed, 1);
                Interlocked.Exchange(ref _consumerEnabled, 0);
                _disposeTask = Task.Run(DisposeCore);
                return _disposeTask;
            }
        }

        private void DisposeCore()
        {
            _transitionTimer?.Dispose();
            _transitionTimer = null;
            _telemetryTimer?.Dispose();
            _telemetryTimer = null;
            _producerWatchdogTimer?.Dispose();
            _producerWatchdogTimer = null;
            FlushAudioTelemetry(force: true);
            FlushTransitionTelemetry();

            _lifecycleGate.Wait();
            try
            {
                Interlocked.Exchange(ref _consumerEnabled, 0);
                if (_inputNode != null)
                {
                    _inputNode.QuantumStarted -= OnQuantumStarted;
                    _inputNode.Stop();
                }
                _graph?.Stop();
                Interlocked.Exchange(ref _inputNodeRunning, 0);
                Interlocked.Exchange(ref _graphRunning, 0);
                bool callbacksDrained = _callbackLifetime.BeginDisposeAndWait(TimeSpan.FromSeconds(2));
                if (!callbacksDrained)
                {
                    LogService.Warn($"[LibrespotRingBufferPlayer.Dispose] graphId={_graphInstanceId}, still waiting for {_callbackLifetime.CallbacksInFlight} AudioGraph callbacks before releasing audio resources.");
                    _callbackLifetime.BeginDisposeAndWait(Timeout.InfiniteTimeSpan);
                }
                _inputNode?.Dispose();
                _graph?.Dispose();
                _inputNode = null;
                _graph = null;
                foreach (var frame in _framePool)
                    frame?.Dispose();

                if (Interlocked.Exchange(ref _graphCounted, 0) != 0)
                {
                    int liveGraphs = Interlocked.Decrement(ref _liveGraphCount);
                    LogService.Info($"[LibrespotRingBufferPlayer.Dispose] graphId={_graphInstanceId}, disposed, liveGraphs={liveGraphs}, idleQuantumCallbacks={IdleQuantumCallbackCount}.");
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }

            ProducerStalled = null;
            _callbackLifetime.Dispose();
        }

        private void ApplyAudioEffectsPreset(string preset)
        {
            if (_inputNode == null || _graph == null)
                return;

            var normalized = NormalizePreset(preset);
            var hasEqualizer = !string.Equals(normalized, "None", StringComparison.OrdinalIgnoreCase);
            var hasEcho = UserSettings.AudioEchoEffectEnabled;
            var hasReverb = UserSettings.AudioReverbEffectEnabled;
            var hasLimiter = UserSettings.AudioLimiterEffectEnabled;

            if (!hasEqualizer && !hasEcho && !hasReverb && !hasLimiter)
            {
                DisableAllAudioEffects();
                return;
            }

            EnsureAudioEffectsCreated();
            DisableAllAudioEffects();

            try
            {
                switch (normalized)
                {
                    case "Equalizer":
                    case "BassBoost":
                    case "VocalBoost":
                    case "Warm":
                        ConfigureEqualizerPreset(preset);
                        _inputNode.EnableEffectsByDefinition(_equalizerEffect);
                        break;
                    default:
                        break;
                }

                if (hasEcho)
                {
                    ConfigureEchoEffect();
                    _inputNode.EnableEffectsByDefinition(_echoEffect);
                }

                if (hasReverb)
                {
                    ConfigureReverbEffect();
                    _inputNode.EnableEffectsByDefinition(_reverbEffect);
                }

                if (hasLimiter)
                {
                    ConfigureLimiterEffect();
                    _inputNode.EnableEffectsByDefinition(_limiterEffect);
                }
            }
            catch (ArgumentException ex)
            {
                LogService.Warn($"Failed to apply audio effects preset '{preset}': {ex.Message}");
                DisableAllAudioEffects();
            }
        }

        private static string NormalizePreset(string preset)
        {
            if (string.IsNullOrWhiteSpace(preset))
                return "None";

            if (string.Equals(preset, "BassBoost", StringComparison.OrdinalIgnoreCase))
                return "BassBoost";

            if (string.Equals(preset, "VocalBoost", StringComparison.OrdinalIgnoreCase))
                return "VocalBoost";

            if (string.Equals(preset, "Warm", StringComparison.OrdinalIgnoreCase))
                return "Warm";

            if (string.Equals(preset, "Equalizer", StringComparison.OrdinalIgnoreCase))
                return "Equalizer";

            return "None";
        }

        private static async Task<DeviceInformation> TryGetOutputDeviceAsync(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return null;

            try
            {
                var defaultId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
                if (string.Equals(deviceId, defaultId, StringComparison.OrdinalIgnoreCase))
                    return null;

                return await DeviceInformation.CreateFromIdAsync(deviceId);
            }
            catch (Exception ex)
            {
                LogService.Warn($"Unable to select audio output device '{deviceId}': {ex.Message}");
                return null;
            }
        }

        private void EnsureAudioEffectsCreated()
        {
            if (_inputNode == null || _graph == null || _audioEffectsConfigured)
                return;

            _equalizerEffect = new EqualizerEffectDefinition(_graph);
            _echoEffect = new EchoEffectDefinition(_graph);
            _reverbEffect = new ReverbEffectDefinition(_graph);
            _limiterEffect = new LimiterEffectDefinition(_graph);

            _inputNode.EffectDefinitions.Add(_equalizerEffect);
            _inputNode.EffectDefinitions.Add(_echoEffect);
            _inputNode.EffectDefinitions.Add(_reverbEffect);
            _inputNode.EffectDefinitions.Add(_limiterEffect);
            _equalizerBandRanges = BuildEqualizerBandRanges(Math.Max(DefaultEqualizerBandCount, _equalizerEffect.Bands?.Count ?? 0));
            _audioEffectsConfigured = true;
        }

        private void DisableAllAudioEffects()
        {
            if (_inputNode == null)
                return;

            if (_equalizerEffect != null)
                _inputNode.DisableEffectsByDefinition(_equalizerEffect);
            if (_echoEffect != null)
                _inputNode.DisableEffectsByDefinition(_echoEffect);
            if (_reverbEffect != null)
                _inputNode.DisableEffectsByDefinition(_reverbEffect);
            if (_limiterEffect != null)
                _inputNode.DisableEffectsByDefinition(_limiterEffect);
        }

        private void ConfigureEchoEffect()
        {
            if (_echoEffect == null)
                return;

            var strength = Clamp01(UserSettings.AudioEffectsStrength);
            try { _echoEffect.Delay = 80.0 + (220.0 * strength); } catch (ArgumentException) { }
            try { _echoEffect.Feedback = 0.08 + (0.36 * strength); } catch (ArgumentException) { }
            try { _echoEffect.WetDryMix = 8.0 + (32.0 * strength); } catch (ArgumentException) { }
        }

        private void ConfigureReverbEffect()
        {
            if (_reverbEffect == null)
                return;

            var strength = Clamp01(UserSettings.AudioEffectsStrength);
            try { _reverbEffect.WetDryMix = 10.0 + (25.0 * strength); } catch (ArgumentException) { }
            try { _reverbEffect.ReverbGain = 0.1 + (0.5 * strength); } catch (ArgumentException) { }
            try { _reverbEffect.RoomSize = 0.35 + (0.45 * strength); } catch (ArgumentException) { }
        }

        private void ConfigureLimiterEffect()
        {
            if (_limiterEffect == null)
                return;

            try { _limiterEffect.Loudness = 0; } catch (ArgumentException) { }
        }

        private void ConfigureEqualizerPreset(string preset)
        {
            if (_equalizerEffect == null)
                return;

            var normalized = NormalizePreset(preset);
            var strength = Clamp01(UserSettings.AudioEffectsStrength);
            var custom = UserSettings.GetEqualizerBandGains();

            int index = 0;
            foreach (var band in _equalizerEffect.Bands)
            {
                double targetGainDb;
                if (string.Equals(normalized, "Equalizer", StringComparison.OrdinalIgnoreCase))
                {
                    targetGainDb = index < custom.Length ? ClampGainDb(custom[index]) : 0.0;
                }
                else
                {
                    targetGainDb = ClampGainDb(GetBandGainDb(normalized, band.FrequencyCenter, strength));
                }

                if (!TryApplyBandGain(band, DecibelsToLinearGain(targetGainDb)))
                    LogService.Warn($"Skipping equalizer band at {band.FrequencyCenter}Hz for preset '{preset}' ({targetGainDb} dB): Value does not fall within the expected range.");

                index++;
            }
        }

        private static bool TryApplyBandGain(EqualizerBand band, double targetGain)
        {
            try
            {
                band.Gain = ClampLinearGain(targetGain);
                return true;
            }
            catch (ArgumentException)
            {
            }

            try
            {
                band.Gain = EqualizerDefaultLinearGain;
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static double GetBandGainDb(string preset, double frequencyCenter, double strength)
        {
            strength = Clamp01(strength);

            switch (NormalizePreset(preset))
            {
                case "BassBoost":
                    if (frequencyCenter <= 125)
                        return 8.0 * strength;
                    if (frequencyCenter <= 500)
                        return 5.0 * strength;
                    if (frequencyCenter <= 2000)
                        return 2.0 * strength;
                    if (frequencyCenter <= 6000)
                        return -1.0 * strength;
                    return -2.0 * strength;

                case "VocalBoost":
                    if (frequencyCenter <= 125)
                        return -3.0 * strength;
                    if (frequencyCenter <= 500)
                        return -1.0 * strength;
                    if (frequencyCenter <= 4000)
                        return 5.0 * strength;
                    if (frequencyCenter <= 8000)
                        return 4.0 * strength;
                    return 2.0 * strength;

                case "Warm":
                    if (frequencyCenter <= 125)
                        return 4.0 * strength;
                    if (frequencyCenter <= 500)
                        return 3.0 * strength;
                    if (frequencyCenter <= 2000)
                        return 1.0 * strength;
                    if (frequencyCenter <= 6000)
                        return -1.0 * strength;
                    return -2.0 * strength;

                default:
                    return 0.0;
            }
        }

        private static EqualizerBandRange[] BuildEqualizerBandRanges(int bandCount)
        {
            bandCount = Math.Max(DefaultEqualizerBandCount, bandCount);
            return Enumerable.Range(0, bandCount)
                .Select(_ => new EqualizerBandRange
                {
                    MinimumGain = UserSettings.EqualizerMinGainDb,
                    MaximumGain = UserSettings.EqualizerMaxGainDb
                })
                .ToArray();
        }

        private static double DecibelsToLinearGain(double decibels)
        {
            return ClampLinearGain(Math.Pow(10.0, ClampGainDb(decibels) / 20.0));
        }

        private static double ClampLinearGain(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return EqualizerDefaultLinearGain;

            return Math.Max(EqualizerMinLinearGain, Math.Min(EqualizerMaxLinearGain, value));
        }

        private static double ClampGainDb(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0.0;

            return Math.Max(UserSettings.EqualizerMinGainDb, Math.Min(UserSettings.EqualizerMaxGainDb, value));
        }

        private static double Clamp01(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 1.0;

            return Math.Max(0.0, Math.Min(1.0, value));
        }

        private void RecordAudioTelemetry(
            int samplesNeeded,
            int bytesRequested,
            int bytesCopied,
            int availableBytes,
            bool generationBoundaryLimited)
        {
            Interlocked.Increment(ref _telemetryQuantumCount);
            Interlocked.Add(
                ref _telemetryRequestedBytes,
                generationBoundaryLimited ? bytesCopied : bytesRequested);
            Interlocked.Add(ref _telemetryCopiedBytes, bytesCopied);
            Interlocked.Add(ref _telemetryAvailableBytes, availableBytes);
            UpdateMin(ref _telemetryMinAvailableBytes, availableBytes);
            UpdateMax(ref _telemetryMaxAvailableBytes, availableBytes);

            if (availableBytes == 0 && !generationBoundaryLimited)
                Interlocked.Increment(ref _telemetryZeroAvailableQuantumCount);

            int silenceBytes = bytesRequested - bytesCopied;
            if (silenceBytes > 0)
            {
                if (generationBoundaryLimited)
                {
                    Interlocked.Add(ref _transitionInsertedSilenceBytes, silenceBytes);
                }
                else
                {
                    Interlocked.Increment(ref _telemetrySilenceFillQuantumCount);
                    Interlocked.Add(ref _telemetrySilenceFillBytes, silenceBytes);
                    UpdateMax(ref _telemetryMaxSilenceFillBytes, silenceBytes);
                }
            }

            RecordQuantumTiming(samplesNeeded);
        }

        private void RecordQuantumTiming(int samplesNeeded)
        {
            if (_props.SampleRate == 0)
                return;

            long now = Stopwatch.GetTimestamp();
            long previous = Interlocked.Exchange(ref _lastQuantumTimestamp, now);
            if (previous == 0)
                return;

            long elapsedTicks = now - previous;
            long expectedTicks = (long)(samplesNeeded * (double)Stopwatch.Frequency / _props.SampleRate);
            if (expectedTicks <= 0)
                return;

            long lateThresholdTicks = expectedTicks + Math.Max(expectedTicks / 2, Stopwatch.Frequency / 200);
            if (elapsedTicks <= lateThresholdTicks)
                return;

            Interlocked.Increment(ref _telemetryLateQuantumCount);
            UpdateMax(ref _telemetryMaxQuantumElapsedTicks, elapsedTicks);
        }

        private void FlushAudioTelemetry(bool force = false)
        {
            long quantumCount = Interlocked.Exchange(ref _telemetryQuantumCount, 0);
            if (quantumCount == 0)
                return;

            long requestedBytes = Interlocked.Exchange(ref _telemetryRequestedBytes, 0);
            long copiedBytes = Interlocked.Exchange(ref _telemetryCopiedBytes, 0);
            long availableBytes = Interlocked.Exchange(ref _telemetryAvailableBytes, 0);
            long silenceFillQuantumCount = Interlocked.Exchange(ref _telemetrySilenceFillQuantumCount, 0);
            long silenceFillBytes = Interlocked.Exchange(ref _telemetrySilenceFillBytes, 0);
            long maxSilenceFillBytes = Interlocked.Exchange(ref _telemetryMaxSilenceFillBytes, 0);
            long zeroAvailableQuantumCount = Interlocked.Exchange(ref _telemetryZeroAvailableQuantumCount, 0);
            long lateQuantumCount = Interlocked.Exchange(ref _telemetryLateQuantumCount, 0);
            long maxQuantumElapsedTicks = Interlocked.Exchange(ref _telemetryMaxQuantumElapsedTicks, 0);
            long framePoolMissCount = Interlocked.Exchange(ref _telemetryFramePoolMissCount, 0);
            int minAvailableBytes = Interlocked.Exchange(ref _telemetryMinAvailableBytes, int.MaxValue);
            int maxAvailableBytes = Interlocked.Exchange(ref _telemetryMaxAvailableBytes, 0);

            if (minAvailableBytes == int.MaxValue)
                minAvailableBytes = 0;

            double avgAvailableBytes = quantumCount > 0 ? availableBytes / (double)quantumCount : 0;
            double fillPercent = requestedBytes > 0 ? copiedBytes * 100.0 / requestedBytes : 100.0;
            double maxCallbackGapMs = TicksToMilliseconds(maxQuantumElapsedTicks);

            bool hasUnderrun = silenceFillQuantumCount > 0 || zeroAvailableQuantumCount > 0 || fillPercent < 99.9;
            bool hasSignificantLateCallbacks = lateQuantumCount >= SignificantLateCallbackCount || maxCallbackGapMs >= SignificantLateCallbackGapMs;
            bool hasPoolPressure = framePoolMissCount > 0;
            if (!hasUnderrun && !hasSignificantLateCallbacks && !hasPoolPressure)
                return;

            string reason = BuildAudioHealthReason(hasUnderrun, hasSignificantLateCallbacks, hasPoolPressure);

            var health = _producerHealth.Snapshot;
            string message = $"[LibrespotRingBufferPlayer.AudioHealth] graphId={_graphInstanceId}, sessionGeneration={health.SessionGeneration}, trackGeneration={health.TrackGeneration}, reason={reason}, track={_audioHealthTrackUri}, window={TelemetryIntervalMs / 1000}s, force={force}, quantum={quantumCount}, fill={fillPercent:F1}%, silenceFills={silenceFillQuantumCount}, zeroAvailable={zeroAvailableQuantumCount}, silenceMs={BytesToMilliseconds(silenceFillBytes):F1}, maxSilenceMs={BytesToMilliseconds(maxSilenceFillBytes):F1}, lateCallbacks={lateQuantumCount}, maxCallbackGapMs={maxCallbackGapMs:F1}, availableMs(avg/min/max)={BytesToMilliseconds((long)avgAvailableBytes):F1}/{BytesToMilliseconds(minAvailableBytes):F1}/{BytesToMilliseconds(maxAvailableBytes):F1}, poolMisses={framePoolMissCount}, capacityMs={BytesToMilliseconds(_capacityBytes):F1}.";
            LogService.Telemetry(
                "audio-health:" + reason,
                message,
                warning: hasUnderrun || hasPoolPressure);
        }

        private double BytesToMilliseconds(long byteCount)
        {
            if (_frameSize <= 0 || _props.SampleRate == 0)
                return 0;

            return byteCount * 1000.0 / _frameSize / _props.SampleRate;
        }

        private static string BuildAudioHealthReason(bool hasUnderrun, bool hasSignificantLateCallbacks, bool hasPoolPressure)
        {
            string reason = null;

            if (hasUnderrun)
                reason = "underrun";

            if (hasSignificantLateCallbacks)
                reason = string.IsNullOrEmpty(reason) ? "late-callback" : reason + "+late-callback";

            if (hasPoolPressure)
                reason = string.IsNullOrEmpty(reason) ? "pool-miss" : reason + "+pool-miss";

            return reason ?? "unknown";
        }

        private static double TicksToMilliseconds(long ticks)
        {
            if (ticks <= 0)
                return 0;

            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        private static void UpdateMin(ref int target, int value)
        {
            int current;
            while (value < (current = Volatile.Read(ref target)) &&
                   Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }

        private static void UpdateMin(ref long target, long value)
        {
            long current;
            while (value < (current = Interlocked.Read(ref target)) &&
                   Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }

        private static void UpdateMax(ref int target, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref target)) &&
                   Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }

        private static void UpdateMax(ref long target, long value)
        {
            long current;
            while (value > (current = Volatile.Read(ref target)) &&
                   Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }

        [ComImport]
        [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMemoryBufferByteAccess
        {
            void GetBuffer(out IntPtr buffer, out uint capacity);
        }

        private async Task WaitForRingBufferAsync()
        {
            int waited = 0;
            while (librespot_audio_get_buffer() == IntPtr.Zero)
            {
                ThrowIfDisposed();
                if (waited >= 5000) throw new InvalidOperationException("Ring Buffer timeout.");
                await Task.Delay(50);
                waited += 50;
            }
            _bufferPtr = librespot_audio_get_buffer();
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(LibrespotRingBufferPlayer));
        }
    }
}
