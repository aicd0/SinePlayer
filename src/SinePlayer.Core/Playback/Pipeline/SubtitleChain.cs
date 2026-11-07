// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Selection;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class SubtitleChain : ISubtitleChain, IChainFeed, IPipelineWakeTarget
{
    private const int SubtitleChangeIntervalMilliseconds = 16;
    private const int FallbackFrameMilliseconds = 40;

    private readonly PipelineTimeline _timeline;
    private readonly PipelineEvents _events;
    private readonly Action _surfaceSizeChanged;

    private PipelineCoroutine? _decodeCoroutine;
    private PipelineThread? _renderThread;
    private TimeSpan _lastPacketSpan = TimeSpan.Zero;

    private readonly AtomicCell<SubtitlePresentation> _presentation = new(SubtitlePresentation.Identity);
    private IVideoRenderTarget? _target;
    private volatile bool _starved;
    private long _endedEpoch = -1;

    public SubtitleChain(PipelineTimeline timeline, PipelineEvents events, Action<SubtitleChain> surfaceSizeChanged)
    {
        _timeline = timeline;
        _events = events;
        _surfaceSizeChanged = () => surfaceSizeChanged(this);
    }

    public MediaStreamKind Kind => MediaStreamKind.Subtitle;

    public SubtitleStreamInfo? SelectedStream => Selected.Stream;

    public SubtitleStreamInfo? PlayingStream => (SubtitleStreamInfo?)Source.CurrentStream;

    public bool IsRunning => _decodeCoroutine is not null || _renderThread is not null;

    public string? DecoderName => Decoder.CurrentComponent?.Name;

    public string? RendererName => Renderer.CurrentComponent?.Name;

    public bool IsHardwareAccelerated => Decoder.CurrentComponent?.IsHardwareAccelerated ?? false;

    public ChainTail ReadTail { get; } = new();

    public bool IsConsuming => Decoder.HasComponent;

    public SubtitleComponentResolver Components { get; } = new();

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

    public ComponentSlot<MediaSource> Source { get; } = new();

    public ComponentSlot<ISubtitleDecoder> Decoder { get; } = new();

    public ComponentSlot<ISubtitleRenderer> Renderer { get; } = new();

    public PacketQueue PacketQueue { get; } = new();

    public SubtitleCueWindow CueWindow { get; } = new();

    public PipelineNodeAsync DecodeNode { get; } = new();

    public PipelineNode RenderNode { get; } = new();

    public SubtitlePresentation Presentation
    {
        get => _presentation.Read();
        set
        {
            if (_presentation.Read() == value)
            {
                return;
            }

            _presentation.Write(_ => value);
            RenderNode.Wake();
        }
    }

    public IPictureFrameSource? LayoutSource { get; set; }

    public long SurfaceVersion;

    public StreamBinding<SubtitleStreamInfo> Selected { get; set; }

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

    public void Start(CoroutineHost host, Action wakeReader)
    {
        _decodeCoroutine = host.Register(stop => Decode(stop, wakeReader));
        _renderThread = new PipelineThread("Pipeline - Subtitle render", Render);
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

        Source.Reset();
        Decoder.Reset();
        Renderer.Reset();
        Selected = default;
        ReadTail.Reset();
    }

    public OperationResult<ChainChange> Reconcile()
    {
        SubtitleComponentResolver resolver = Components.Clone();

        OperationResultCreator<ChainChange> result = new();

        StreamBinding<SubtitleStreamInfo> selected = Selected;
        bool sourceChanged = !selected.Matches(Source.Latest?.Component, (SubtitleStreamInfo?)Source.Latest?.Stream);
        ChainChange changed = sourceChanged ? ChainChange.Source : ChainChange.None;
        PipelineComponent<ISubtitleDecoder>? decoder = null;
        PipelineComponent<ISubtitleRenderer>? renderer = null;
        try
        {
            if (selected.Source is { } subtitleSource && selected.Stream is { } subtitleStream)
            {
                IVideoRenderTarget? subtitleTarget = Target;
                if (subtitleTarget is null)
                {
                    return result.Failure("The file has a subtitle track but no subtitle surface was supplied.");
                }

                // Resolve subtitle decoder
                bool rebuildSubtitleDecoder = sourceChanged;
                PipelineComponent<ISubtitleDecoder>? latestDecoder = Decoder.Latest;
                OperationResult<PipelineComponent<ISubtitleDecoder>?> decoderResolved = ComponentResolution.Resolve(
                    resolver.ResolveDecoders(subtitleSource.Source, subtitleStream),
                    rebuildSubtitleDecoder ? null : latestDecoder?.Signature,
                    subtitleStream,
                    "subtitle decoder");
                if (!decoderResolved.IsSuccessful)
                {
                    return result.Failure(decoderResolved.Message);
                }

                decoder = decoderResolved.Result;
                if (decoder is not null)
                {
                    changed |= ChainChange.Decoder;
                }

                // Resolve subtitle renderer
                PipelineComponent<ISubtitleDecoder>? runningDecoder = decoder ?? latestDecoder;
                if (runningDecoder is null)
                {
                    return result.Failure("The track has no decoder to say what its cues are made of.");
                }

                SubtitleCueFormat format = runningDecoder.Component.CueFormat;
                bool rebuildSubtitleRenderer = decoder is not null || sourceChanged;
                PipelineComponent<ISubtitleRenderer>? latestRenderer = Renderer.Latest;
                OperationResult<PipelineComponent<ISubtitleRenderer>?> rendererResolved = ComponentResolution.Resolve(
                    resolver.ResolveRenderers(subtitleStream, format),
                    rebuildSubtitleRenderer ? null : latestRenderer?.Signature,
                    subtitleStream,
                    "subtitle renderer");
                if (!rendererResolved.IsSuccessful)
                {
                    return result.Failure(rendererResolved.Message);
                }

                renderer = rendererResolved.Result;
                if (renderer is not null)
                {
                    OperationResult configured = renderer.Component.Configure(subtitleStream, subtitleSource.Info.Attachments, subtitleTarget);
                    if (!configured.IsSuccessful)
                    {
                        return result.Failure(configured.Message);
                    }

                    changed |= ChainChange.Renderer;
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
            decoder?.Dispose();
            renderer?.Dispose();
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

            if (Decoder.Update(out ISubtitleDecoder? oldSubtitleDecoder))
            {
                oldSubtitleDecoder?.Dispose();
                CueWindow.Reset();
            }

            EpochGate epochGate = _timeline.Gate;
            long epoch = epochGate.Epoch;
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;

                try
                {
                    Decoder.CurrentComponent?.Flush();
                }
                catch (Exception ex)
                {
                    _events.PostWarning($"The subtitle decoder could not be flushed: {ex.Message}", this);
                }

                CueWindow.SeekTo(epoch, epochGate.SeekPosition);
                continue;
            }

            if (!PacketQueue.TryPeek(out PacketEntry entry))
            {
                await DecodeNode.WaitAsync(stamp);
                continue;
            }

            VersionVerdict verdict = ChainVersion.CompareDecoder(entry.Versions, Decoder.CurrentVersions);
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

                ISubtitleDecoder? subtitleDecoder = Decoder.CurrentComponent;
                if (packet is null || subtitleDecoder is null)
                {
                    continue;
                }

                OperationResult routed = DecodeCues(epoch, subtitleDecoder, packet);
                if (!routed.IsSuccessful)
                {
                    _events.PostFailure(routed);
                    return;
                }
            }

            await Task.Yield();
        }
    }

    private void Render(Func<bool> stopRequested)
    {
        void ReportOnFailure(string message, OperationResult result)
        {
            if (!result.IsSuccessful)
            {
                _events.PostWarning($"{message}: {result.Message}", this);
            }
        }

        bool drawnInvalidated = true;
        long workingEpoch = -1;
        long drawnSurfaceVersion = -1;
        TimeSpan drawnPosition = TimeSpan.MinValue;
        SubtitlePresentation drawnPresentation = default;
        SubtitleCueSnapshot drawnCues = SubtitleCueSnapshot.Empty;

        while (true)
        {
            long stamp = RenderNode.Stamp;
            if (stopRequested())
            {
                break;
            }

            if (Renderer.Update(out ISubtitleRenderer? oldRenderer))
            {
                if (oldRenderer is not null)
                {
                    if (Renderer.CurrentComponent is null)
                    {
                        ReportOnFailure("The subtitles could not be cleared",
                            PipelineEvents.SafeInvoke(() => oldRenderer.Show([], TimeSpan.Zero, SubtitlePresentation.Identity)));
                    }

                    oldRenderer.Dispose();
                }

                drawnInvalidated = true;
                continue;
            }

            ISubtitleRenderer? renderer = Renderer.CurrentComponent;
            if (renderer is null)
            {
                RenderNode.Wait(stamp);
                continue;
            }

            if (!_events.CheckState(out MediaEngineState state, MediaEngineState.Playing, MediaEngineState.Paused))
            {
                RenderNode.Wait(stamp);
                continue;
            }

            long epoch = _timeline.Epoch;
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;
                drawnInvalidated = true;
            }

            TimeSpan position = _timeline.Position;
            SubtitleCueSnapshot cues = CueWindow.GetVisibleCues(epoch, position);
            long surfaceVersion = Interlocked.Read(ref SurfaceVersion);

            SubtitlePresentation presentation = _presentation.Read() with
            {
                Frame = LayoutSource?.Frame ?? SubtitleFrame.Full
            };

            if (drawnInvalidated ||
                surfaceVersion != drawnSurfaceVersion ||
                presentation != drawnPresentation ||
                cues.Cues.Any(cue => cue.HasChanges(drawnPosition, position)) ||
                !SameCues(cues, drawnCues))
            {
                ReportOnFailure("The subtitles could not be drawn",
                    PipelineEvents.SafeInvoke(() => renderer.Show(cues.Cues, position, presentation)));
                drawnCues.Dispose();
                drawnCues = cues;
                drawnInvalidated = false;
                drawnSurfaceVersion = surfaceVersion;
                drawnPosition = position;
                drawnPresentation = presentation;
            }
            else
            {
                cues.Dispose();
            }

            int timeout = Timeout.Infinite;
            if (state == MediaEngineState.Playing)
            {
                TimeSpan? next = CueWindow.TimeUntilNextChange(position);
                if (next.HasValue)
                {
                    timeout = next.Value <= TimeSpan.Zero
                        ? SubtitleChangeIntervalMilliseconds
                        : (int)Math.Clamp(Math.Ceiling(next.Value.TotalMilliseconds), 1, int.MaxValue);
                }
            }

            RenderNode.Wait(stamp, timeout);
        }

        drawnCues.Dispose();
    }

    private OperationResult DecodeCues(long epoch, ISubtitleDecoder subtitleDecoder, MediaPacket packet)
    {
        OperationResult decoded;
        IReadOnlyList<SubtitleCue> cues;
        try
        {
            decoded = subtitleDecoder.TryDecode(packet, out cues);
        }
        catch (Exception ex)
        {
            return OperationResult.Failure(ex.Message);
        }

        if (!decoded.IsSuccessful)
        {
            return decoded;
        }

        if (cues is null)
        {
            return OperationResult.Failure("The subtitle decoder returned no cues.");
        }

        CueWindow.Add(epoch, cues, _timeline.Position, discardStale: !Renderer.HasComponent);
        if (cues.Count > 0)
        {
            RenderNode.Wake();
        }

        return OperationResult.Success();
    }

    private static bool SameCues(SubtitleCueSnapshot x, SubtitleCueSnapshot y)
    {
        IReadOnlyList<SubtitleCue> xCues = x.Cues;
        IReadOnlyList<SubtitleCue> yCues = y.Cues;
        if (xCues.Count != yCues.Count)
        {
            return false;
        }

        for (int index = 0; index < xCues.Count; index++)
        {
            if (!ReferenceEquals(xCues[index], yCues[index]))
            {
                return false;
            }
        }

        return true;
    }

    private void WakeDecode()
    {
        if (!PacketQueue.IsEmpty)
        {
            DecodeNode.Wake();
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
