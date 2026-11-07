// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Abstractions.Timing;
using SinePlayer.Core.Playback.Selection;
using SinePlayer.Core.Playback.Timing;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class VideoChain : IVideoChain, IPictureFrameSource, IChainFeed, IPipelineWakeTarget
{
    private const int FrameQueueCapacity = 8;
    private const int VideoStarvationMilliseconds = 200;
    private const int VideoStarvationResumeMilliseconds = 100;
    private const int VideoIdleTimeoutMilliseconds = 50;
    private const int FallbackFrameMilliseconds = 40;

    public const int DecoderBufferedFrames = FrameQueueCapacity + 4;

    private readonly PipelineTimeline _timeline;
    private readonly PipelineEvents _events;
    private readonly Action _surfaceSizeChanged;

    private IVideoRenderTarget? _target;
    private volatile bool _starved;
    private long _endedEpoch = -1;
    private long _presentedFrameCount;
    private long _droppedFrameCount;
    private long _decodedFrameCount;
    private long _decodeTicks;
    private long _renderTicks;
    private long _pictureTicks = -1;
    private PipelineThread? _decodeThread;
    private PipelineThread? _renderThread;
    private TimeSpan _lastPacketSpan = TimeSpan.Zero;

    public VideoChain(PipelineTimeline timeline, PipelineEvents events, Action surfaceSizeChanged)
    {
        _timeline = timeline;
        _events = events;
        _surfaceSizeChanged = surfaceSizeChanged;
    }

    public MediaStreamKind Kind => MediaStreamKind.Video;

    public MediaStreamInfo? SelectedStream => Selected.Stream;

    public MediaStreamInfo? PlayingStream => Source.CurrentStream;

    public bool IsActive => Decoder.HasComponent;

    public string? DecoderName => Decoder.CurrentComponent?.Name;

    public string? RendererName => Renderer.CurrentComponent?.Name;

    public bool IsHardwareAccelerated => Decoder.CurrentComponent?.IsHardwareAccelerated ?? false;

    public ISynchronizer Synchronizer { get; set; } = new DefaultSynchronizer();

    public IVideoRenderTarget? Target
    {
        get => _target;
        set
        {
            _target?.SurfaceSizeChanged -= _surfaceSizeChanged;
            _target = value;
            _target?.SurfaceSizeChanged += _surfaceSizeChanged;
        }
    }

    public long PresentedFrameCount => Interlocked.Read(ref _presentedFrameCount);

    public long DroppedFrameCount => Interlocked.Read(ref _droppedFrameCount);

    public long DecodedFrameCount => Interlocked.Read(ref _decodedFrameCount);

    public double AverageDecodeMilliseconds
    {
        get
        {
            long frames = Interlocked.Read(ref _decodedFrameCount);
            return frames <= 0 ? 0 : Interlocked.Read(ref _decodeTicks) * 1000.0 / Stopwatch.Frequency / frames;
        }
    }

    public double AverageRenderMilliseconds
    {
        get
        {
            long frames = Interlocked.Read(ref _presentedFrameCount);
            return frames <= 0 ? 0 : Interlocked.Read(ref _renderTicks) * 1000.0 / Stopwatch.Frequency / frames;
        }
    }

    public TimeSpan PictureLag
    {
        get
        {
            long ticks = Interlocked.Read(ref _pictureTicks);
            if (ticks < 0 || PresentedFrameCount == 0)
            {
                return TimeSpan.Zero;
            }

            TimeSpan lag = _timeline.Position - TimeSpan.FromTicks(ticks);
            return lag > TimeSpan.Zero ? lag : TimeSpan.Zero;
        }
    }

    public ComponentSlot<MediaSource> Source { get; } = new();

    public ComponentSlot<IVideoDecoder> Decoder { get; } = new();

    public ComponentSlot<IVideoRenderer> Renderer { get; } = new();

    public PacketQueue PacketQueue { get; } = new();

    public ConcurrentQueue<FrameEntry> FrameQueue { get; } = new();

    public PipelineNode DecodeNode { get; } = new();

    public PipelineNode RenderNode { get; } = new();

    public AtomicCell<VideoTransform> Transform { get; } = new(VideoTransform.Identity);

    public StreamBinding<VideoStreamInfo> Selected { get; set; }

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

    public ChainTail ReadTail { get; } = new();

    public bool IsConsuming => Decoder.HasComponent;

    public TimeSpan LastPresentedTimestamp
    {
        get
        {
            long ticks = Interlocked.Read(ref _pictureTicks);
            return ticks < 0 ? TimeSpan.Zero : TimeSpan.FromTicks(ticks);
        }
    }

    public void Start(Action wakeReader, Func<long, TimeSpan, bool> isStreamSpent, Action reportEnded)
    {
        _decodeThread = new PipelineThread("Pipeline - Video decode", stop => Decode(stop, wakeReader));
        _renderThread = new PipelineThread("Pipeline - Video render", stop => Render(stop, isStreamSpent, reportEnded));
        _decodeThread.Start();
        _renderThread.Start();
    }

    public void RequestStop()
    {
        _decodeThread?.RequestStop();
        _renderThread?.RequestStop();
        DecodeNode.Wake();
        RenderNode.Wake();
    }

    public void Join()
    {
        _decodeThread?.Join();
        _decodeThread = null;
        _renderThread?.Join();
        _renderThread = null;
    }

    public void Reset()
    {
        PacketQueue.Clear();

        while (FrameQueue.TryDequeue(out FrameEntry entry))
        {
            entry.Frame?.Dispose();
        }

        Source.Reset();
        Decoder.Reset();
        Renderer.Reset();
        Target = null;
        Selected = default;
        Starved = false;
        EndedEpoch = -1;
        ReadTail.Reset();

        Interlocked.Exchange(ref _presentedFrameCount, 0);
        Interlocked.Exchange(ref _droppedFrameCount, 0);
        Interlocked.Exchange(ref _decodedFrameCount, 0);
        Interlocked.Exchange(ref _decodeTicks, 0);
        Interlocked.Exchange(ref _renderTicks, 0);
        Interlocked.Exchange(ref _pictureTicks, -1);
    }

    public OperationResult<ChainChange> Reconcile(VideoComponentResolver resolver)
    {
        OperationResultCreator<ChainChange> result = new();

        StreamBinding<VideoStreamInfo> selected = Selected;
        bool sourceChanged = !selected.Matches(
            Source.Latest?.Component, (VideoStreamInfo?)Source.Latest?.Stream);
        ChainChange changed = sourceChanged ? ChainChange.Source : ChainChange.None;
        PipelineComponent<IVideoRenderer>? renderer = null;
        PipelineComponent<IVideoDecoder>? decoder = null;
        try
        {
            if (selected.Source is { } videoSource && selected.Stream is { } videoStream)
            {
                IVideoRenderTarget? videoTarget = Target;
                if (videoTarget is null)
                {
                    return result.Failure("The file has video but no presentation surface was supplied.");
                }

                // Resolve video renderer
                bool rebuildVideoRenderer = sourceChanged;
                PipelineComponent<IVideoRenderer>? latestRenderer = Renderer.Latest;
                OperationResult<PipelineComponent<IVideoRenderer>?> rendererResolved = ComponentResolution.Resolve(
                    resolver.ResolveRenderers(),
                    rebuildVideoRenderer ? null : latestRenderer?.Signature,
                    videoStream,
                    "video renderer");
                if (!rendererResolved.IsSuccessful)
                {
                    return result.Failure(rendererResolved.Message);
                }

                renderer = rendererResolved.Result;
                if (renderer is not null)
                {
                    OperationResult configured = renderer.Component.Configure(videoStream, videoTarget);
                    if (!configured.IsSuccessful)
                    {
                        return result.Failure(configured.Message);
                    }

                    changed |= ChainChange.Renderer;
                }

                // Resolve video decoder
                bool rebuildVideoDecoder = renderer is not null || sourceChanged;
                PipelineComponent<IVideoDecoder>? latestDecoder = Decoder.Latest;
                IGpuDevice? gpuDevice = (renderer ?? latestRenderer)?.Component?.GpuDevice;
                OperationResult<PipelineComponent<IVideoDecoder>?> decoderResolved = ComponentResolution.Resolve(
                    resolver.ResolveDecoders(videoSource.Source, videoStream, gpuDevice, DecoderBufferedFrames),
                    rebuildVideoDecoder ? null : latestDecoder?.Signature,
                    videoStream,
                    "video decoder");
                if (!decoderResolved.IsSuccessful)
                {
                    return result.Failure(decoderResolved.Message);
                }

                decoder = decoderResolved.Result;
                if (decoder is not null)
                {
                    changed |= ChainChange.Decoder;
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
        var videoStream = (VideoStreamInfo)stream;
        TimeSpan span = SpanOf(packet, videoStream.FrameRate);
        _lastPacketSpan = span;
        PacketQueue.Enqueue(new PacketEntry(packet, versions, epoch, span));
        WakeDecode();
    }

    public void SignalEnd(ChainVersion versions, long epoch)
    {
        PacketQueue.Enqueue(new PacketEntry(null, versions, epoch, TimeSpan.Zero));
        WakeDecode();
    }

    private void Decode(Func<bool> stopRequested, Action wakeReader)
    {
        bool awaitingKeyFrame = false;
        long workingEpoch = -1;

        while (true)
        {
            long stamp = DecodeNode.Stamp;
            if (stopRequested())
            {
                return;
            }

            if (Decoder.Update(out IVideoDecoder? oldVideoDecoder))
            {
                oldVideoDecoder?.Dispose();
                awaitingKeyFrame = true;
            }

            IVideoDecoder? videoDecoder = Decoder.CurrentComponent;

            long epoch = _timeline.Epoch;
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;

                try
                {
                    videoDecoder?.Flush();
                }
                catch (Exception exception)
                {
                    _events.PostFailure(exception.Message, exception);
                    return;
                }

                continue;
            }

            if (!HasRoomForFrame())
            {
                DecodeNode.Wait(stamp);
                continue;
            }

            if (!PacketQueue.TryPeek(out PacketEntry entry))
            {
                DecodeNode.Wait(stamp);
                continue;
            }

            ChainVersion videoDecoderVersions = Decoder.CurrentVersions;
            VersionVerdict verdict = ChainVersion.CompareDecoder(entry.Versions, videoDecoderVersions);
            if (verdict == VersionVerdict.Block)
            {
                DecodeNode.Wait(stamp);
                continue;
            }

            if (!PacketQueue.TryDequeue(out _))
            {
                DecodeNode.Wait(stamp);
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

                var videoFrameVersions = ChainVersion.Max(entry.Versions, videoDecoderVersions);
                if (packet is null)
                {
                    if (videoDecoder is not null)
                    {
                        OperationResult drained = DrainDecoder(videoDecoder, videoFrameVersions, packetEpoch);
                        if (!drained.IsSuccessful)
                        {
                            _events.PostWarning(drained.Message);
                        }
                    }

                    FrameQueue.Enqueue(new FrameEntry(null, videoFrameVersions, epoch));
                    WakeRender();
                    DecodeNode.Wait(stamp);
                    continue;
                }

                if (awaitingKeyFrame)
                {
                    if (!packet.IsKeyFrame)
                    {
                        continue;
                    }

                    awaitingKeyFrame = false;
                }

                if (videoDecoder is null)
                {
                    continue;
                }

                OperationResult decoded = DecodePacket(videoDecoder, packet, out IReadOnlyList<RawVideoFrame>? frames);
                if (!decoded.IsSuccessful)
                {
                    _events.PostFailure(decoded);
                    return;
                }

                if (frames is null)
                {
                    _events.PostFailure("The video decoder returned no frames.");
                    return;
                }

                QueueFrames(frames, videoFrameVersions, epoch);
            }
        }
    }

    private void Render(Func<bool> stopRequested, Func<long, TimeSpan, bool> isStreamSpent, Action reportEnded)
    {
        bool deviceAvailable = false;
        long workingEpoch = -1;
        long endingEpoch = -1;
        TimeSpan? startTime = null;
        TimeSpan? firstFrameTime = null;

        void DequeueAndDispose()
        {
            if (!FrameQueue.TryDequeue(out FrameEntry entry))
            {
                return;
            }

            entry.Frame?.Dispose();
            WakeDecode();
        }

        while (true)
        {
            long stamp = RenderNode.Stamp;
            if (stopRequested())
            {
                return;
            }

            if (Renderer.Update(out IVideoRenderer? oldVideoRenderer))
            {
                if (oldVideoRenderer is not null)
                {
                    if (Renderer.CurrentComponent is null)
                    {
                        OperationResult cleared;
                        try
                        {
                            cleared = oldVideoRenderer.ClearSurface();
                        }
                        catch (Exception ex)
                        {
                            cleared = OperationResult.Failure(ex);
                        }

                        if (!cleared.IsSuccessful)
                        {
                            _events.PostWarning(cleared.Message);
                        }

                        Interlocked.Exchange(ref _pictureTicks, -1);
                    }

                    oldVideoRenderer.Dispose();
                }

                deviceAvailable = Renderer.CurrentComponent is not null;
                continue;
            }

            IVideoRenderer? videoRenderer = Renderer.CurrentComponent;

            EpochGate epochGate = _timeline.Gate;
            long epoch = epochGate.Epoch;
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;
                startTime = epochGate.SeekMode == SeekMode.Precise ? epochGate.SeekPosition : null;
                firstFrameTime = null;
                Interlocked.Exchange(ref _pictureTicks, -1);
            }

            bool playing = _events.CheckState(out MediaEngineState state, MediaEngineState.Playing);
            VideoTransform transform = Transform.Read();

            VideoOrientation containerTransform = ((VideoStreamInfo?)Renderer.CurrentStream)?.Orientation ?? VideoOrientation.Identity;
            if (!containerTransform.IsIdentity)
            {
                transform = transform with { Orientation = transform.Orientation.Combine(containerTransform) };
            }

            if (deviceAvailable && videoRenderer is not null && state is MediaEngineState.Paused or MediaEngineState.Ended)
            {
                OperationResult refreshed;
                try
                {
                    refreshed = videoRenderer.RefreshSurface(transform);
                }
                catch (Exception ex)
                {
                    refreshed = OperationResult.Failure(ex);
                }

                if (!refreshed.IsSuccessful)
                {
                    _events.PostWarning(refreshed.Message);
                    deviceAvailable = false;
                }
            }

            FrameEntry? entry = FrameQueue.TryPeek(out FrameEntry head) ? head : null;
            TimeSpan position = _timeline.Position;
            bool offeringTheStill = state == MediaEngineState.Paused && !firstFrameTime.HasValue && endingEpoch != epoch;

            bool starved = Decoder.HasComponent && endingEpoch != epoch && entry is null;
            starved = starved && !isStreamSpent(epoch, position);
            starved = starved && (!firstFrameTime.HasValue || IsStarved(position));

            lock (_timeline.ClockLock)
            {
                Starved = starved;
                _timeline.StartOrPause(epoch, state);
            }

            if (!playing && !offeringTheStill)
            {
                RenderNode.Wait(stamp);
                continue;
            }

            if (entry is null)
            {
                RenderNode.Wait(stamp, playing ? VideoIdleTimeoutMilliseconds : Timeout.Infinite);
                continue;
            }

            if (entry.Value.Epoch != epoch)
            {
                DequeueAndDispose();
                continue;
            }

            VersionVerdict verdict = ChainVersion.CompareRenderer(entry.Value.Versions, Renderer.CurrentVersions);
            if (verdict == VersionVerdict.Block)
            {
                RenderNode.Wait(stamp);
                continue;
            }

            if (verdict == VersionVerdict.Drop)
            {
                DequeueAndDispose();
                continue;
            }

            RawVideoFrame? frame = entry.Value.Frame;
            if (frame is null)
            {
                if (endingEpoch != entry.Value.Epoch)
                {
                    endingEpoch = entry.Value.Epoch;

                    long epochCopy = entry.Value.Epoch;
                    _events.Submit(() =>
                    {
                        EndedEpoch = epochCopy;
                        reportEnded();
                    });
                }

                DequeueAndDispose();
                continue;
            }

            if (startTime.HasValue && frame.Timestamp < startTime)
            {
                DequeueAndDispose();
                continue;
            }

            FrameSyncAction syncAction = Synchronizer.Evaluate(frame.Timestamp, position);

            if (playing)
            {
                TimeSpan anchor = firstFrameTime ?? frame.Timestamp;

                lock (_timeline.ClockLock)
                {
                    if (_timeline.Anchor(epoch, anchor))
                    {
                        position = _timeline.Position;
                        syncAction = Synchronizer.Evaluate(frame.Timestamp, position);
                    }

                    _timeline.StartOrPause(epoch, state);
                }
            }

            if (playing && syncAction == FrameSyncAction.Wait)
            {
                int remaining = (int)Math.Clamp((frame.Timestamp - position).TotalMilliseconds, 1, VideoIdleTimeoutMilliseconds);
                RenderNode.Wait(stamp, remaining);
                continue;
            }

            if (!FrameQueue.TryDequeue(out _))
            {
                continue;
            }

            WakeDecode();

            using (frame)
            {
                if (playing
                    && syncAction == FrameSyncAction.Drop
                    && FrameQueue.TryPeek(out FrameEntry closer)
                    && closer.Frame is not null
                    && closer.Epoch == epoch)
                {
                    Interlocked.Increment(ref _droppedFrameCount);
                    continue;
                }

                if (deviceAvailable && videoRenderer is not null)
                {
                    if (playing && firstFrameTime.HasValue && !videoRenderer.TryAcquireFrameSlot())
                    {
                        Interlocked.Increment(ref _droppedFrameCount);
                        continue;
                    }

                    OperationResult shown = RenderFrame(videoRenderer, frame, transform);
                    if (!shown.IsSuccessful)
                    {
                        _events.PostWarning(shown.Message);
                        deviceAvailable = false;
                    }
                }

                if (!firstFrameTime.HasValue)
                {
                    firstFrameTime = frame.Timestamp;
                }
            }
        }
    }

    private OperationResult DecodePacket(IVideoDecoder videoDecoder, MediaPacket packet, out IReadOnlyList<RawVideoFrame>? frames)
    {
        frames = null;

        long startTicks = Stopwatch.GetTimestamp();
        OperationResult decoded;
        IReadOnlyList<RawVideoFrame>? produced = null;
        try
        {
            decoded = videoDecoder.TryDecode(packet, out produced);
        }
        catch (Exception ex)
        {
            decoded = OperationResult.Failure(ex);
        }

        Interlocked.Add(ref _decodeTicks, Stopwatch.GetTimestamp() - startTicks);

        if (!decoded.IsSuccessful || produced is null)
        {
            return decoded;
        }

        frames = produced;
        Interlocked.Add(ref _decodedFrameCount, produced.Count);
        return OperationResult.Success();
    }

    private OperationResult DrainDecoder(IVideoDecoder decoder, ChainVersion versions, long epoch)
    {
        OperationResult drained;
        IReadOnlyList<RawVideoFrame> frames;
        try
        {
            drained = decoder.Drain(out frames);
        }
        catch (Exception ex)
        {
            return OperationResult.Failure(ex.Message);
        }

        if (!drained.IsSuccessful)
        {
            return drained;
        }

        if (frames is null)
        {
            return OperationResult.Failure("The video decoder returned no frames.");
        }

        QueueFrames(frames, versions, epoch);
        return OperationResult.Success();
    }

    private void QueueFrames(IReadOnlyList<RawVideoFrame> frames, ChainVersion versions, long epoch)
    {
        bool fastExit = false;

        foreach (RawVideoFrame frame in frames)
        {
            if (fastExit || _timeline.Epoch != epoch)
            {
                fastExit = true;
                frame.Dispose();
                continue;
            }

            FrameQueue.Enqueue(new FrameEntry(frame, versions, epoch));
        }

        if (!fastExit)
        {
            WakeRender();
        }
    }

    private bool HasRoomForFrame()
    {
        return FrameQueue.Count < FrameQueueCapacity;
    }

    private OperationResult RenderFrame(IVideoRenderer renderer, RawVideoFrame frame, VideoTransform transform)
    {
        long startTicks = Stopwatch.GetTimestamp();
        OperationResult rendered;
        try
        {
            rendered = renderer.Render(frame, transform);
        }
        catch (Exception ex)
        {
            rendered = OperationResult.Failure(ex.Message);
        }

        Interlocked.Add(ref _renderTicks, Stopwatch.GetTimestamp() - startTicks);

        if (!rendered.IsSuccessful)
        {
            return rendered;
        }

        Interlocked.Increment(ref _presentedFrameCount);
        Interlocked.Exchange(ref _pictureTicks, frame.Timestamp.Ticks);

        return OperationResult.Success();
    }

    private bool IsStarved(TimeSpan position)
    {
        long presentedTicks = Interlocked.Read(ref _pictureTicks);
        if (presentedTicks < 0)
        {
            return false;
        }

        TimeSpan behind = position - TimeSpan.FromTicks(presentedTicks);
        return Starved
            ? behind > TimeSpan.FromMilliseconds(VideoStarvationResumeMilliseconds)
            : behind > TimeSpan.FromMilliseconds(VideoStarvationMilliseconds);
    }

    private void WakeDecode()
    {
        if (!HasRoomForFrame())
        {
            return;
        }

        if (!PacketQueue.IsEmpty)
        {
            DecodeNode.Wake();
        }
    }

    private void WakeRender()
    {
        if (!FrameQueue.IsEmpty)
        {
            RenderNode.Wake();
        }
    }

    private TimeSpan SpanOf(MediaPacket packet, double frameRate)
    {
        if (packet.Duration > TimeSpan.Zero)
        {
            return packet.Duration;
        }

        if (_lastPacketSpan > TimeSpan.Zero)
        {
            return _lastPacketSpan;
        }

        if (!double.IsFinite(frameRate) || frameRate <= 0 || frameRate > 1000)
        {
            return TimeSpan.FromMilliseconds(FallbackFrameMilliseconds);
        }

        return TimeSpan.FromSeconds(1 / frameRate);
    }

    public SubtitleFrame Frame
    {
        get
        {
            IVideoRenderTarget? target = Target;
            var stream = (VideoStreamInfo?)Source.CurrentStream;
            if (target is null || stream is null || stream.Width <= 0 || stream.Height <= 0)
            {
                return SubtitleFrame.Full;
            }

            int surfaceWidth = target.SurfaceWidth;
            int surfaceHeight = target.SurfaceHeight;
            if (surfaceWidth <= 0 || surfaceHeight <= 0)
            {
                return SubtitleFrame.Full;
            }

            // The placement maps the picture's own square onto the surface, so its four corners say where the picture ends:
            // the bounding box of that is the frame, whatever turning, zooming or mirroring was asked for.
            Matrix3x2 placement = Transform.Read().GetPlacement(stream.Width, stream.Height, surfaceWidth, surfaceHeight);
            var zero = Vector2.Transform(Vector2.Zero, placement);
            var unitX = Vector2.Transform(Vector2.UnitX, placement);
            var unitY = Vector2.Transform(Vector2.UnitY, placement);
            var one = Vector2.Transform(Vector2.One, placement);

            float left = Math.Clamp(MathF.Min(MathF.Min(zero.X, unitX.X), MathF.Min(unitY.X, one.X)), 0.0f, 1.0f);
            float top = Math.Clamp(MathF.Min(MathF.Min(zero.Y, unitX.Y), MathF.Min(unitY.Y, one.Y)), 0.0f, 1.0f);
            float right = Math.Clamp(MathF.Max(MathF.Max(zero.X, unitX.X), MathF.Max(unitY.X, one.X)), 0.0f, 1.0f);
            float bottom = Math.Clamp(MathF.Max(MathF.Max(zero.Y, unitX.Y), MathF.Max(unitY.Y, one.Y)), 0.0f, 1.0f);

            // A picture larger than the surface is cropped by it, so what is left of it is the whole surface.
            return right > left && bottom > top
                ? new SubtitleFrame(left, top, right - left, bottom - top)
                : SubtitleFrame.Full;
        }
    }
}
