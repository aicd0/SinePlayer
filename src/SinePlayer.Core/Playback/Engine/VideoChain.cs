// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Concurrent;
using System.Diagnostics;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Abstractions.Timing;
using SinePlayer.Core.Playback.Hosting;
using SinePlayer.Core.Playback.Selection;
using SinePlayer.Core.Playback.Timing;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Engine;

internal sealed class VideoChain : IVideoChain, IChainFeed, IPipelineWakeTarget
{
    private const int FrameQueueCapacity = 8;
    private const int VideoStarvationMilliseconds = 200;
    private const int VideoStarvationResumeMilliseconds = 100;
    private const int VideoIdleTimeoutMilliseconds = 50;
    private const int FallbackFrameMilliseconds = 40;

    public const int DecoderBufferedFrames = FrameQueueCapacity + 4;

    private readonly IChainHost _host;
    private readonly PipelineTimeline _timeline;
    private readonly PipelineEvents _events;

    private readonly AtomicCell<VideoTransform> _transform = new(VideoTransform.Identity);
    private IVideoRenderTarget? _target;
    private volatile bool _starved;

    private long _endedEpoch = -1;
    private long _presentedFrameCount;
    private long _droppedFrameCount;
    private long _decodedFrameCount;
    private long _decodeTicks;
    private long _renderTicks;
    private long _pictureTicks = -1;
    private long _offsetTicks;
    private PipelineThread? _decodeThread;
    private PipelineThread? _renderThread;
    private TimeSpan _lastPacketSpan = TimeSpan.Zero;

    public VideoChain(IChainHost host, PipelineTimeline timeline, PipelineEvents events)
    {
        _host = host;
        _timeline = timeline;
        _events = events;
    }

    public MediaStreamKind Kind => MediaStreamKind.Video;

    public VideoStreamInfo? SelectedStream => Selected?.Stream;

    public VideoStreamInfo? PlayingStream => (VideoStreamInfo?)Source.CurrentStream;

    public bool IsRunning => _decodeThread is not null || _renderThread is not null;

    public string? DecoderName => Decoder.CurrentComponent?.Name;

    public string? RendererName => Renderer.CurrentComponent?.Name;

    public bool IsHardwareAccelerated => Decoder.CurrentComponent?.IsHardwareAccelerated ?? false;

    public ISynchronizer Synchronizer { get; set; } = new DefaultSynchronizer();

    public IVideoRenderTarget? Target
    {
        get => _target;
        set
        {
            _target?.SurfaceSizeChanged -= OnSurfaceSizeChanged;
            _target = value;
            _target?.SurfaceSizeChanged += OnSurfaceSizeChanged;
        }
    }

    public VideoComponentResolver Components { get; } = new();

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

    public VideoTransform Transform
    {
        get => _transform.Read();
        set
        {
            if (_transform.Read() == value)
            {
                return;
            }

            _transform.Write(_ => value);
            RenderNode.Wake();
            _host.PictureLayoutChanged(this);
        }
    }

    public StreamBinding<VideoStreamInfo>? Selected { get; set; }

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

    public TimeSpan Offset
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
        set
        {
            if (Interlocked.Exchange(ref _offsetTicks, value.Ticks) == value.Ticks)
            {
                return;
            }

            _host.OffsetChanged(this);
        }
    }

    public TimeSpan LastPresentedTimestamp
    {
        get
        {
            long ticks = Interlocked.Read(ref _pictureTicks);
            return ticks < 0 ? TimeSpan.Zero : TimeSpan.FromTicks(ticks);
        }
    }

    public Task<OperationResult> SelectStream(MediaStreamInfo? stream)
    {
        return _host.Select(this, stream);
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
        Selected = null;
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

    public OperationResult<ChainChange> Reconcile()
    {
        VideoComponentResolver resolver = Components.Clone();

        OperationResultCreator<ChainChange> result = new();

        StreamBinding<VideoStreamInfo>? chosen = Selected;
        bool sourceChanged = chosen?.Source != Source.Latest?.Component || chosen?.Stream != Source.Latest?.Stream;
        ChainChange changed = sourceChanged ? ChainChange.Source : ChainChange.None;
        PipelineComponent<IVideoRenderer>? renderer = null;
        PipelineComponent<IVideoDecoder>? decoder = null;
        try
        {
            if (chosen is StreamBinding<VideoStreamInfo> selected)
            {
                MediaSource videoSource = selected.Source;
                VideoStreamInfo videoStream = selected.Stream;

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
                    Source.Publish(chosen is { } bound ? new(bound.Source, bound.Stream, "source") : null, versions);
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

    public void Select(StreamBinding<MediaStreamInfo>? binding)
    {
        Selected = binding is { } chosen ? new(chosen.Source, (VideoStreamInfo)chosen.Stream) : null;
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
                if (verdict == VersionVerdict.Drop)
                {
                    continue;
                }

                if (packetEpoch != _timeline.Epoch)
                {
                    continue;
                }

                if (workingEpoch != packetEpoch)
                {
                    workingEpoch = packetEpoch;
                    awaitingKeyFrame = true;

                    try
                    {
                        videoDecoder?.Flush();
                    }
                    catch (Exception exception)
                    {
                        _events.PostFailure(exception.Message, exception);
                        return;
                    }
                }

                var videoFrameVersions = ChainVersion.Max(entry.Versions, videoDecoderVersions);
                if (packet is null)
                {
                    if (videoDecoder is not null)
                    {
                        OperationResult drained = DrainDecoder(videoDecoder, videoFrameVersions, packetEpoch);
                        if (!drained.IsSuccessful)
                        {
                            _events.PostWarning(drained.Message, this);
                        }
                    }

                    FrameQueue.Enqueue(new FrameEntry(null, videoFrameVersions, packetEpoch));
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

                QueueFrames(frames, videoFrameVersions, packetEpoch);
            }
        }
    }

    private void Render(Func<bool> stopRequested, Func<long, TimeSpan, bool> isStreamSpent, Action reportEnded)
    {
        bool deviceAvailable = false;
        long workingEpoch = -1;
        long endingEpoch = -1;
        TimeSpan workingOffset = TimeSpan.Zero;
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
                            _events.PostWarning(cleared.Message, this);
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
            TimeSpan offset = epochGate.OffsetOf(this);
            bool restarted = workingEpoch != epoch || workingOffset != offset;
            if (restarted)
            {
                workingEpoch = epoch;
                workingOffset = offset;
                startTime = epochGate.SeekMode == SeekMode.Precise ? epochGate.SeekPosition : null;
                firstFrameTime = null;
                Interlocked.Exchange(ref _pictureTicks, -1);
            }

            bool playing = _events.CheckState(out MediaEngineState state, MediaEngineState.Playing);
            VideoTransform transform = GetStreamTransform((VideoStreamInfo?)Renderer.CurrentStream);

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
                    _events.PostWarning(refreshed.Message, this);
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

            TimeSpan timestamp = frame.Timestamp + offset;
            if (startTime.HasValue && timestamp < startTime)
            {
                DequeueAndDispose();
                continue;
            }

            FrameSyncAction syncAction = Synchronizer.Evaluate(timestamp, position);

            if (playing)
            {
                TimeSpan anchor = (firstFrameTime ?? frame.Timestamp) + offset;

                lock (_timeline.ClockLock)
                {
                    if (_timeline.Anchor(epoch, anchor))
                    {
                        position = _timeline.Position;
                        syncAction = Synchronizer.Evaluate(timestamp, position);
                    }

                    _timeline.StartOrPause(epoch, state);
                }
            }

            if (playing && syncAction == FrameSyncAction.Wait)
            {
                int remaining = Math.Min(_timeline.ToIdleTime(timestamp - position), VideoIdleTimeoutMilliseconds);
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

                    OperationResult shown = RenderFrame(videoRenderer, frame, timestamp, transform);
                    if (!shown.IsSuccessful)
                    {
                        _events.PostWarning(shown.Message, this);
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

    private OperationResult RenderFrame(IVideoRenderer renderer, RawVideoFrame frame, TimeSpan timestamp, VideoTransform transform)
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
        Interlocked.Exchange(ref _pictureTicks, timestamp.Ticks);

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

    private void OnSurfaceSizeChanged()
    {
        RenderNode.Wake();
        _host.PictureLayoutChanged(this);
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

    public SubtitleFrame SubtitleFrame
    {
        get
        {
            IVideoRenderTarget? target = Target;
            var stream = (VideoStreamInfo?)Source.CurrentStream;
            if (target is null || stream is null || stream.Width <= 0 || stream.Height <= 0)
            {
                return SubtitleFrame.Full;
            }

            // The surface in the pixels the picture is drawn in, which is what the transform's own maths uses: the window's
            // size is reported in device independent pixels, and the picture and the surface it is drawn on are physical
            // ones. A picture of the stream's own size — OriginalSize — is the case where the two spaces differ; every other
            // mode scales the picture with the surface, so the scale cancels out of the fraction it produces.
            double scale = target.RasterizationScale > 0 && double.IsFinite(target.RasterizationScale) ? target.RasterizationScale : 1.0;
            double surfaceWidth = Math.Max(1.0, Math.Round(target.SurfaceWidth * scale));
            double surfaceHeight = Math.Max(1.0, Math.Round(target.SurfaceHeight * scale));

            VideoTransform transform = GetStreamTransform(stream);
            (double pictureWidth, double pictureHeight) = transform.GetPictureSize(stream.Width, stream.Height);

            // The picture as the app's transform places it, uncropped by the surface.
            PlacedPicture placed = transform.GetPlacedPicture(stream.Width, stream.Height, (int)surfaceWidth, (int)surfaceHeight);

            // And what the surface leaves of it, which is the picture the viewer ends up with: cropped by the surface like
            // the picture itself is, so a picture panned partly off the surface is only as tall as the part still on it.
            double visibleTop = Math.Max(placed.CenterY - (placed.Height / 2.0), 0.0);
            double visibleBottom = Math.Min(placed.CenterY + (placed.Height / 2.0), surfaceHeight);
            double outputHeight = Math.Max(visibleBottom - visibleTop, 0.0);

            // The box is as tall as the picture the viewer ends up with, and takes its width from the picture's own shape
            // rather than from the width it was drawn at: libass scales a script onto the box one axis at a time, so a box
            // of another shape than the picture's draws every glyph stretched. That shape may make the box wider than the
            // surface — a picture stretched to a surface of another shape is as tall as the surface — and then its sides are
            // cropped with the picture's.
            double height = Math.Min(surfaceHeight, outputHeight);
            if (!(height > 0.0) || !double.IsFinite(height))
            {
                return SubtitleFrame.Full;
            }

            double width = height * pictureWidth / pictureHeight;
            double left = placed.CenterX - (width / 2.0);
            double top = placed.CenterY - (height / 2.0);
            return new SubtitleFrame(left / surfaceWidth, top / surfaceHeight, width / surfaceWidth, height / surfaceHeight);
        }
    }

    private VideoTransform GetStreamTransform(VideoStreamInfo? stream)
    {
        VideoTransform transform = _transform.Read();
        VideoOrientation container = stream?.Orientation ?? VideoOrientation.Identity;
        return container.IsIdentity
            ? transform
            : transform with { Orientation = transform.Orientation.Combine(container) };
    }
}
