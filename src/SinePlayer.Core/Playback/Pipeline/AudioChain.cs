// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Abstractions.Timing;
using SinePlayer.Core.Playback.Selection;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class AudioChain : IAudioChain, IChainFeed, IPipelineWakeTarget
{
    private const int AudioIdleTimeoutMilliseconds = 5;
    private const int AudioDeviceLeewayMilliseconds = 3000;
    private const int FallbackFrameMilliseconds = 40;

    private readonly PipelineTimeline _timeline;
    private readonly PipelineEvents _events;

    private PipelineCoroutine? _decodeCoroutine;
    private PipelineThread? _renderThread;
    private TimeSpan _lastPacketSpan = TimeSpan.Zero;
    private volatile bool _starved;
    private long _endedEpoch = -1;

    public AudioChain(PipelineTimeline timeline, PipelineEvents events)
    {
        _timeline = timeline;
        _events = events;
    }

    public MediaStreamKind Kind => MediaStreamKind.Audio;

    public MediaStreamInfo? SelectedStream => Selected.Stream;

    public MediaStreamInfo? PlayingStream => Source.CurrentStream;

    public bool IsActive => Decoder.HasComponent;

    public string? DecoderName => Decoder.CurrentComponent?.Name;

    public string? RendererName => Renderer.CurrentComponent?.Name;

    public bool IsHardwareAccelerated => Decoder.CurrentComponent?.IsHardwareAccelerated ?? false;

    public AudioComponentResolver Components { get; } = new();

    public AudioFilterChain Filters { get; } = new();

    public long Underruns => Renderer.CurrentComponent?.UnderrunCount ?? 0;

    public ChainTail ReadTail { get; } = new();

    public bool IsConsuming => Decoder.HasComponent;

    public ComponentSlot<MediaSource> Source { get; } = new();

    public ComponentSlot<IAudioDecoder> Decoder { get; } = new();

    public ComponentSlot<IAudioRenderer> Renderer { get; } = new();

    public PacketQueue PacketQueue { get; } = new();

    public AudioQueue BufferQueue { get; } = new();

    public PipelineNodeAsync DecodeNode { get; } = new();

    public PipelineNode RenderNode { get; } = new();

    public StreamBinding<AudioStreamInfo> Selected { get; set; }

    public bool Starved
    {
        get => _starved;
        set => _starved = value;
    }

    public long EndedEpoch
    {
        get => Interlocked.Read(ref _endedEpoch);
        set => Interlocked.Exchange(ref _endedEpoch, value);
    }

    public void Start(CoroutineHost host, Action wakeReader, Func<long, TimeSpan, bool> isStreamSpent, Action reportEnded)
    {
        _decodeCoroutine = host.Register(stop => Decode(stop, wakeReader));
        _renderThread = new PipelineThread("Pipeline - Audio render", stop => Render(stop, isStreamSpent, reportEnded));
        _renderThread.Start();
    }

    public void RequestStop()
    {
        _decodeCoroutine?.RequestStop();
        _renderThread?.RequestStop();
        DecodeNode.Wake();
        RenderNode.Wake();
    }

    public void Join()
    {
        _decodeCoroutine = null;
        _renderThread?.Join();
        _renderThread = null;
    }

    public void Reset()
    {
        PacketQueue.Clear();
        BufferQueue.Clear();

        Source.Reset();
        Decoder.Reset();
        Renderer.CurrentComponent?.Flush();
        Renderer.Reset();
        Selected = default;
        Starved = false;
        EndedEpoch = -1;
        ReadTail.Reset();
    }

    public OperationResult<ChainChange> Reconcile()
    {
        AudioComponentResolver resolver = Components.Clone();

        OperationResultCreator<ChainChange> result = new();

        StreamBinding<AudioStreamInfo> selected = Selected;
        bool sourceChanged = !selected.Matches(Source.Latest?.Component, (AudioStreamInfo?)Source.Latest?.Stream);
        ChainChange changed = sourceChanged ? ChainChange.Source : ChainChange.None;
        PipelineComponent<IAudioRenderer>? renderer = null;
        PipelineComponent<IAudioDecoder>? decoder = null;
        AudioStreamInfo? negotiatedFormat = null;
        try
        {
            if (selected.Source is { } audioSource && selected.Stream is { } audioStream)
            {
                // Resolve audio renderer
                bool rebuildAudioRenderer = sourceChanged;
                PipelineComponent<IAudioRenderer>? latestRenderer = Renderer.Latest;
                OperationResult<PipelineComponent<IAudioRenderer>?> rendererResolved = ComponentResolution.Resolve(
                    resolver.ResolveRenderers(),
                    rebuildAudioRenderer ? null : latestRenderer?.Signature,
                    audioStream,
                    "audio renderer");
                if (!rendererResolved.IsSuccessful)
                {
                    return result.Failure(rendererResolved.Message);
                }

                renderer = rendererResolved.Result;
                if (renderer is not null)
                {
                    OperationResult<AudioStreamInfo> negotiated = renderer.Component.Negotiate(audioStream);
                    if (!negotiated.IsSuccessful)
                    {
                        return result.Failure(negotiated.Message);
                    }

                    negotiatedFormat = negotiated.Result;
                    changed |= ChainChange.Renderer;
                }

                // Resolve audio decoder
                bool rebuildAudioDecoder = sourceChanged;
                PipelineComponent<IAudioDecoder>? latestDecoder = Decoder.Latest;
                OperationResult<PipelineComponent<IAudioDecoder>?> decoderResolved = ComponentResolution.Resolve(
                    resolver.ResolveDecoders(audioSource.Source, audioStream),
                    rebuildAudioDecoder ? null : latestDecoder?.Signature,
                    audioStream,
                    "audio decoder");
                if (!decoderResolved.IsSuccessful)
                {
                    return result.Failure(decoderResolved.Message);
                }

                decoder = decoderResolved.Result;
                if (decoder is not null)
                {
                    changed |= ChainChange.Decoder;
                }

                if (negotiatedFormat is not null)
                {
                    Filters.Configure(audioStream, negotiatedFormat);
                }
            }
            else
            {
                if (Decoder.HasComponent)
                {
                    changed |= sourceChanged ? ChainChange.Decoder : ChainChange.None;
                }

                if (Renderer.HasComponent)
                {
                    changed |= sourceChanged ? ChainChange.Renderer : ChainChange.None;
                }
            }

            if (changed != ChainChange.None)
            {
                ChainVersion current = new(
                    Source.LastestVersions.Source,
                    Decoder.LastestVersions.Decoder,
                    Renderer.LastestVersions.Renderer);
                long next = Math.Max(current.Source, Math.Max(current.Decoder, current.Renderer)) + 1;
                ChainVersion versions = current with
                {
                    Source = changed.HasFlag(ChainChange.Source) ? next : current.Source,
                    Decoder = changed.HasFlag(ChainChange.Decoder) ? next : current.Decoder,
                    Renderer = changed.HasFlag(ChainChange.Renderer) ? next : current.Renderer,
                };

                if (changed.HasFlag(ChainChange.Source))
                {
                    Source.Publish(selected.Source is { } source && selected.Stream is { } stream ? new(source, stream, "source") : null, versions);
                }

                if (changed.HasFlag(ChainChange.Decoder))
                {
                    Decoder.Publish(decoder, versions);
                    decoder = null;
                }

                if (changed.HasFlag(ChainChange.Renderer))
                {
                    Renderer.Publish(renderer, versions);
                    renderer = null;
                }
            }

            return result.Success(changed);
        }
        finally
        {
            renderer?.Dispose();
            decoder?.Dispose();
        }
    }

    public void Wake()
    {
        DecodeNode.Wake();
        RenderNode.Wake();
    }

    public void Accept(MediaStreamInfo stream, MediaPacket packet, ChainVersion versions, long epoch)
    {
        TimeSpan span = SpanOf(packet);
        _lastPacketSpan = span;
        PacketQueue.Enqueue(new PacketEntry(packet, versions, epoch, span));
        WakeDecode();
    }

    public void SignalEnd(ChainVersion versions, long epoch)
    {
        PacketQueue.Enqueue(new PacketEntry(null, versions, epoch, TimeSpan.Zero));
        WakeDecode();
    }

    private async Task Decode(Func<bool> stopRequested, Action wakeReader)
    {
        long workingEpoch = -1;

        while (true)
        {
            long stamp = DecodeNode.Stamp;
            if (stopRequested())
            {
                return;
            }

            if (Decoder.Update(out IAudioDecoder? oldAudioDecoder))
            {
                oldAudioDecoder?.Dispose();
            }

            long epoch = _timeline.Epoch;
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;

                try
                {
                    Decoder.CurrentComponent?.Flush();
                }
                catch (Exception ex)
                {
                    _events.PostWarning($"The audio decoder could not be flushed: {ex.Message}");
                }

                continue;
            }

            if (!PacketQueue.TryPeek(out PacketEntry entry))
            {
                await DecodeNode.WaitAsync(stamp);
                continue;
            }

            ChainVersion audioDecoderVersions = Decoder.CurrentVersions;
            VersionVerdict verdict = ChainVersion.CompareDecoder(entry.Versions, audioDecoderVersions);
            if (verdict == VersionVerdict.Block)
            {
                await DecodeNode.WaitAsync(stamp);
                continue;
            }

            if (!PacketQueue.TryDequeue(out _))
            {
                await DecodeNode.WaitAsync(stamp);
                continue;
            }

            wakeReader();

            MediaPacket? packet = entry.Packet;
            long packetEpoch = entry.Epoch;
            using (packet)
            {
                if (verdict == VersionVerdict.Drop || epoch != packetEpoch)
                {
                    continue;
                }

                var audioBufferVersions = ChainVersion.Max(entry.Versions, audioDecoderVersions);
                IAudioDecoder? audioDecoder = Decoder.CurrentComponent;
                if (packet is null)
                {
                    OperationResult drained = DrainDecoder(audioDecoder, audioBufferVersions, packetEpoch);
                    if (!drained.IsSuccessful)
                    {
                        _events.PostWarning(drained.Message);
                    }

                    BufferQueue.Enqueue(new AudioEntry(null, audioBufferVersions, epoch));
                    WakeRender();
                    continue;
                }

                if (audioDecoder is null)
                {
                    continue;
                }

                OperationResult routed = DecodePacket(audioDecoder, packet, audioBufferVersions, epoch);
                if (!routed.IsSuccessful)
                {
                    _events.PostFailure(routed);
                    return;
                }
            }

            await Task.Yield();
        }
    }

    private OperationResult DecodePacket(IAudioDecoder audioDecoder, MediaPacket packet, ChainVersion versions, long epoch)
    {
        OperationResult decoded;
        IReadOnlyList<RawAudioBuffer> buffers;
        try
        {
            decoded = audioDecoder.TryDecode(packet, out buffers);
        }
        catch (Exception ex)
        {
            return OperationResult.Failure(ex.Message);
        }

        if (!decoded.IsSuccessful)
        {
            return decoded;
        }

        if (buffers is null)
        {
            return OperationResult.Failure("The audio decoder returned no buffers.");
        }

        foreach (RawAudioBuffer buffer in buffers)
        {
            BufferQueue.Enqueue(new AudioEntry(buffer, versions, epoch));
        }

        if (buffers.Count > 0)
        {
            WakeRender();
        }

        return OperationResult.Success();
    }

    private OperationResult DrainDecoder(IAudioDecoder? decoder, ChainVersion versions, long epoch)
    {
        if (decoder is null)
        {
            return OperationResult.Success();
        }

        OperationResult drained;
        IReadOnlyList<RawAudioBuffer> buffers;
        try
        {
            drained = decoder.Drain(out buffers);
        }
        catch (Exception ex)
        {
            return OperationResult.Failure(ex.Message);
        }

        if (!drained.IsSuccessful)
        {
            return drained;
        }

        if (buffers is null)
        {
            return OperationResult.Failure("The audio decoder returned no buffers.");
        }

        bool handedOver = false;
        foreach (RawAudioBuffer buffer in buffers)
        {
            BufferQueue.Enqueue(new AudioEntry(buffer, versions, epoch));
            handedOver = true;
        }

        if (handedOver)
        {
            WakeRender();
        }

        return OperationResult.Success();
    }

    private void Render(Func<bool> stopRequested, Func<long, TimeSpan, bool> isStreamSpent, Action reportEnded)
    {
        bool deviceAvailable = false;
        bool reportedRunningAhead = false;
        long workingEpoch = -1;
        long endingEpoch = -1;
        long playedOutEpoch = -1;
        TimeSpan? clockOffset = null;
        TimeSpan? startTime = null;
        TimeSpan? tailBufferPosition = null;
        AudioStreamInfo? deviceFormat = null;
        AudioFilterChainSnapshot? filters = null;

        IAudioSubmit? inFlightSubmit = null;
        RawAudioBuffer? inFlightBuffer = null;

        void ReleaseInFlightAudio()
        {
            inFlightSubmit?.Dispose();
            inFlightSubmit = null;
            inFlightBuffer?.Dispose();
            inFlightBuffer = null;
        }

        bool AbandonFailedDevice(IAudioRenderer? renderer)
        {
            deviceAvailable = false;

            if (renderer is null || renderer.IsDeviceAvailable)
            {
                return false;
            }

            Renderer.Publish(null, Renderer.CurrentVersions);
            return true;
        }

        while (true)
        {
            long stamp = RenderNode.Stamp;
            if (stopRequested())
            {
                break;
            }

            if (Renderer.Update(out IAudioRenderer? oldAudioRenderer))
            {
                ReleaseInFlightAudio();
                oldAudioRenderer?.Flush();
                oldAudioRenderer?.Dispose();

                deviceAvailable = false;
                reportedRunningAhead = false;
                clockOffset = null;
                deviceFormat = null;
                filters = null;
                continue;
            }

            IAudioRenderer? audioRenderer = Renderer.CurrentComponent;

            if (filters is null || filters.Version != Filters.Version)
            {
                filters = Filters.Snapshot();

                AudioStreamInfo? outputFormat = filters.OutputFormat;
                if (audioRenderer is not null && outputFormat is not null && (deviceFormat is null || !AudioFilterChain.IsSameFormat(outputFormat, deviceFormat)))
                {
                    ReleaseInFlightAudio();

                    OperationResult opened = audioRenderer.Configure(outputFormat);
                    if (opened.IsSuccessful)
                    {
                        deviceFormat = outputFormat;
                        deviceAvailable = true;
                    }
                    else
                    {
                        deviceFormat = null;
                        _events.PostWarning($"The audio device could not be opened: {opened.Message}");
                        if (AbandonFailedDevice(audioRenderer))
                        {
                            continue;
                        }
                    }

                    clockOffset = null;
                }
            }

            EpochGate epochGate = _timeline.Gate;
            long epoch = epochGate.Epoch;
            bool epochChanged = workingEpoch != epoch;

            if (epochChanged)
            {
                workingEpoch = epoch;
                startTime = epochGate.SeekMode == SeekMode.Precise ? epochGate.SeekPosition : null;
                tailBufferPosition = null;
                reportedRunningAhead = false;
                audioRenderer?.Flush();
            }

            if (epochChanged || !deviceAvailable)
            {
                clockOffset = null;
                ReleaseInFlightAudio();
            }

            bool playing = _events.CheckState(out MediaEngineState state, MediaEngineState.Playing);
            lock (_timeline.ClockLock)
            {
                if (epochChanged)
                {
                    Starved = true;
                }

                _timeline.StartOrPause(epoch, state);

                if (deviceAvailable && audioRenderer is not null)
                {
                    IPlaybackClock deviceClock = audioRenderer.Clock;
                    bool clockRunning = _timeline.IsRunning;
                    if (clockRunning != deviceClock.IsRunning)
                    {
                        try
                        {
                            if (clockRunning)
                            {
                                deviceClock.Start();
                            }
                            else
                            {
                                deviceClock.Pause();
                            }
                        }
                        catch (Exception ex)
                        {
                            _events.PostWarning(ex.Message);
                            AbandonFailedDevice(audioRenderer);
                            continue;
                        }
                    }
                }
            }

            if (!playing)
            {
                RenderNode.Wait(stamp);
                continue;
            }

            TimeSpan position = _timeline.Position;
            if (deviceAvailable && tailBufferPosition is TimeSpan consumed
                && consumed - position > TimeSpan.FromMilliseconds(AudioDeviceLeewayMilliseconds))
            {
                if (!reportedRunningAhead)
                {
                    reportedRunningAhead = true;
                    _events.PostWarning("The audio device is not playing what it is given.");
                }

                lock (_timeline.ClockLock)
                {
                    // A device that is running ahead of the clock is not starved.
                    Starved = false;
                }

                RenderNode.Wait(stamp, AudioIdleTimeoutMilliseconds);
                continue;
            }

            bool hasDeviceAudio = deviceAvailable && audioRenderer is not null && audioRenderer.QueuedSampleCount > 0;
            if (deviceAvailable && audioRenderer is not null)
            {
                IPlaybackClock deviceClock = audioRenderer.Clock;

                if (hasDeviceAudio && clockOffset is TimeSpan offset)
                {
                    _timeline.SyncClock(this, deviceClock, offset);
                }

                if (inFlightSubmit is not null)
                {
                    if (!inFlightSubmit.MoveNext())
                    {
                        OperationResult submitted = inFlightSubmit.Result;
                        ReleaseInFlightAudio();

                        if (!submitted.IsSuccessful)
                        {
                            _events.PostWarning(submitted.Message);
                            AbandonFailedDevice(audioRenderer);
                        }

                        continue;
                    }

                    RenderNode.Wait(stamp, AudioIdleTimeoutMilliseconds);
                    continue;
                }
            }

            if (BufferQueue.IsEmpty)
            {
                bool ending = endingEpoch == epoch;
                bool starved = deviceAvailable && !hasDeviceAudio && !ending && !isStreamSpent(epoch, position);
                lock (_timeline.ClockLock)
                {
                    Starved = starved;
                    _timeline.StartOrPause(epoch, state);
                }

                if (ending && !hasDeviceAudio && playedOutEpoch != endingEpoch)
                {
                    playedOutEpoch = endingEpoch;

                    long epochCopy = endingEpoch;
                    _events.Submit(() =>
                    {
                        EndedEpoch = epochCopy;
                        reportEnded();
                    });
                }

                RenderNode.Wait(stamp, AudioIdleTimeoutMilliseconds);
                continue;
            }

            // Idle on failing device if the clock is behind, since the device clock is no longer reliable
            if (!deviceAvailable && tailBufferPosition.HasValue && position < tailBufferPosition.Value)
            {
                RenderNode.Wait(stamp, AudioIdleTimeoutMilliseconds);
                continue;
            }

            if (!BufferQueue.TryPeek(out AudioEntry entry))
            {
                continue;
            }

            VersionVerdict verdict = ChainVersion.CompareRenderer(entry.Versions, Renderer.CurrentVersions);
            if (verdict == VersionVerdict.Block)
            {
                RenderNode.Wait(stamp);
                continue;
            }

            if (!BufferQueue.TryDequeue(out _))
            {
                continue;
            }

            RawAudioBuffer? buffer = entry.Buffer;
            long bufferEpoch = entry.Epoch;

            if (verdict == VersionVerdict.Drop)
            {
                buffer?.Dispose();
                continue;
            }

            if (epoch != bufferEpoch)
            {
                buffer?.Dispose();
                continue;
            }

            if (buffer is null)
            {
                endingEpoch = entry.Epoch;
                continue;
            }

            if (startTime.HasValue && buffer.Timestamp < startTime)
            {
                buffer.Dispose();
                continue;
            }

            OperationResult<RawAudioBuffer> filtersApplied = filters.TryApply(buffer);
            if (!filtersApplied.IsSuccessful)
            {
                buffer.Dispose();
                _events.PostFailure(filtersApplied.Message, filtersApplied.Exception);
                return;
            }

            buffer = filtersApplied.Result;

            tailBufferPosition = buffer.Timestamp + buffer.Duration;

            lock (_timeline.ClockLock)
            {
                Starved = false;

                bool clockChanged = _timeline.Anchor(epoch, buffer.Timestamp);
                if (deviceAvailable && audioRenderer is not null && (clockChanged || clockOffset is null))
                {
                    clockOffset = audioRenderer.Clock.Position - buffer.Timestamp;
                }

                _timeline.StartOrPause(epoch, state);
            }

            if (deviceAvailable && audioRenderer is not null)
            {
                inFlightSubmit = audioRenderer.Submit(buffer);
                inFlightBuffer = buffer;
            }
            else
            {
                buffer.Dispose();
            }
        }

        ReleaseInFlightAudio();
    }

    private void WakeDecode()
    {
        if (!PacketQueue.IsEmpty)
        {
            DecodeNode.Wake();
        }
    }

    private void WakeRender()
    {
        if (!BufferQueue.IsEmpty)
        {
            RenderNode.Wake();
        }
    }

    private TimeSpan SpanOf(MediaPacket packet)
    {
        if (packet.Duration > TimeSpan.Zero)
        {
            return packet.Duration;
        }

        return _lastPacketSpan > TimeSpan.Zero ? _lastPacketSpan : TimeSpan.FromMilliseconds(FallbackFrameMilliseconds);
    }
}
