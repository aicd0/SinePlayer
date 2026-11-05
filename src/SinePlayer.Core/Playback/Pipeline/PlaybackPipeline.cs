// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Diagnostics;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Abstractions.Timing;
using SinePlayer.Core.Playback.Selection;

namespace SinePlayer.Core.Playback.Pipeline;

public sealed partial class PlaybackPipeline : IDisposable
{
    #region Variables

    private readonly PipelineEvents _events;
    private readonly PipelineTimeline _timeline;
    private readonly SubtitleChain _subtitle;
    private readonly VideoChain _video;
    private readonly AudioChain _audio;

    private volatile MediaSourceHolder _selection = new([], null, null, null);
    private readonly ComponentSlot<MediaSourceHolder> _source = new();
    private CoroutineHost? _host;
    private readonly IPipelineWakeTarget[] _wakeTargets;
    private readonly IChainFeed[] _feeds;
    private readonly MediaReader _reader;

    private bool _disposed;

    private long _videoRenderEndedEpoch = -1;
    private long _audioRenderEndedEpoch = -1;

    #endregion

    #region Construction

    public PlaybackPipeline()
    {
        _events = new PipelineEvents(WakeAll);
        _timeline = new PipelineTimeline(NotifyClockMoved);
        _video = new VideoChain(_timeline, _events);
        _audio = new AudioChain(_timeline, _events);
        _subtitle = new SubtitleChain(_timeline, _events);
        _feeds = [_video, _audio, _subtitle];
        _reader = new MediaReader(_source, _feeds, _timeline, _events, ChooseSeekLanding);
        _wakeTargets = [
            _reader,
            _video.DecodeNode,
            _audio.DecodeNode,
            _subtitle.DecodeNode,
            _video.RenderNode,
            _audio.RenderNode,
            _subtitle.RenderNode,
        ];
    }

    #endregion

    #region Public API

    public event Action<MediaEngineState, string?>? StateChanged
    {
        add => _events.StateChanged += value;
        remove => _events.StateChanged -= value;
    }

    public event Action<string>? WarningRaised
    {
        add => _events.WarningRaised += value;
        remove => _events.WarningRaised -= value;
    }

    public ComponentResolver Components { get; set; } = new();

    public AudioFilterChain AudioFilters => _audio.Filters;

    public ISourceProviderFactory? SourceFactory { get; set; }

    public ISynchronizer Synchronizer
    {
        get => _video.Synchronizer;
        set => _video.Synchronizer = value;
    }

    public VideoTransform VideoTransform
    {
        get => _video.Transform.Read();
        set
        {
            if (_video.Transform.Read() == value)
            {
                return;
            }

            _video.Transform.Write(_ => value);
            _video.RenderNode.Wake();
            _subtitle.RenderNode.Wake();
        }
    }

    public SubtitlePresentation SubtitlePresentation
    {
        get => _subtitle.Presentation.Read();
        set
        {
            if (_subtitle.Presentation.Read() == value)
            {
                return;
            }

            _subtitle.Presentation.Write(_ => value);
            _subtitle.RenderNode.Wake();
        }
    }

    public MediaEngineState State => _events.State;

    public string? Error => _events.Error;

    public IReadOnlyList<MediaSource> Sources => _selection.Sources;

    public VideoStreamInfo? SelectedVideoStream => _selection.VideoStream;

    public AudioStreamInfo? SelectedAudioStream => _selection.AudioStream;

    public SubtitleStreamInfo? SelectedSubtitleStream => _selection.SubtitleStream;

    public string? VideoDecoderName => _video.Decoder.CurrentComponent?.Name;

    public string? AudioDecoderName => _audio.Decoder.CurrentComponent?.Name;

    public string? VideoRendererName => _video.Renderer.CurrentComponent?.Name;

    public string? AudioRendererName => _audio.Renderer.CurrentComponent?.Name;

    public string? SubtitleDecoderName => _subtitle.Decoder.CurrentComponent?.Name;

    public string? SubtitleRendererName => _subtitle.Renderer.CurrentComponent?.Name;

    public bool IsVideoHardwareAccelerated => _video.Decoder.CurrentComponent?.IsHardwareAccelerated ?? false;

    public bool IsAudioHardwareAccelerated => _audio.Decoder.CurrentComponent?.IsHardwareAccelerated ?? false;

    public TimeSpan Position => _timeline.Position;

    public long PresentedFrameCount => Interlocked.Read(ref _video.PresentedFrameCount);

    public long DroppedFrameCount => Interlocked.Read(ref _video.DroppedFrameCount);

    public string? StarvedFor => _timeline.VideoStarved ? "video" : _timeline.AudioStarved ? "audio" : null;

    internal TimeSpan LastPresentedTimestamp
    {
        get
        {
            long ticks = Interlocked.Read(ref _video.PictureTicks);
            return ticks < 0 ? TimeSpan.Zero : TimeSpan.FromTicks(ticks);
        }
    }

    public double AverageVideoDecodeMilliseconds
    {
        get
        {
            long frames = Interlocked.Read(ref _video.DecodedFrameCount);
            return frames <= 0 ? 0 : Interlocked.Read(ref _video.DecodeTicks) * 1000.0 / Stopwatch.Frequency / frames;
        }
    }

    public double AverageVideoRenderMilliseconds
    {
        get
        {
            long frames = Interlocked.Read(ref _video.PresentedFrameCount);
            return frames <= 0 ? 0 : Interlocked.Read(ref _video.RenderTicks) * 1000.0 / Stopwatch.Frequency / frames;
        }
    }

    public long AudioUnderruns => _audio.Renderer.CurrentComponent?.UnderrunCount ?? 0;

    public TimeSpan PictureLag
    {
        get
        {
            long ticks = Interlocked.Read(ref _video.PictureTicks);
            if (ticks < 0 || PresentedFrameCount == 0)
            {
                return TimeSpan.Zero;
            }

            TimeSpan lag = Position - TimeSpan.FromTicks(ticks);
            return lag > TimeSpan.Zero ? lag : TimeSpan.Zero;
        }
    }

    public void Dispose()
    {
        Task<OperationResult> teardown = _events.Submit(() =>
        {
            if (_disposed)
            {
                return OperationResult.Success();
            }

            _disposed = true;
            return StopCore();
        });

        teardown.Wait();
    }

    public Task<OperationResult> Reset(IVideoRenderTarget? videoTarget = null, IVideoRenderTarget? subtitleTarget = null)
    {
        return _events.SubmitAndLog(nameof(Reset), () => ResetCore(videoTarget, subtitleTarget));
    }

    public Task<OperationResult<MediaSource>> AddSource(MediaLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        return _events.SubmitAndLog(nameof(AddSource), () => AddSourceCore(locator));
    }

    public Task<OperationResult> SelectStream(MediaStreamKind kind, MediaStreamInfo? stream)
    {
        return _events.SubmitAndLog(nameof(SelectStream), () => SelectStreamCore(kind, stream));
    }

    public Task<OperationResult> Play()
    {
        return _events.SubmitAndLog(nameof(Play), PlayCore);
    }

    public Task<OperationResult> Pause()
    {
        return _events.SubmitAndLog(nameof(Pause), PauseCore);
    }

    public Task<OperationResult> Seek(TimeSpan position, SeekMode mode = SeekMode.Fast)
    {
        return _events.SubmitAndLog(nameof(Seek), () => SeekCore(position, mode));
    }

    public Task<OperationResult<ReconcileResult>> Reconcile()
    {
        return _events.SubmitAndLog(nameof(Reconcile), ReconcileCore);
    }

    public Task<OperationResult> Stop()
    {
        return _events.SubmitAndLog(nameof(Stop), () => ResetCore(null, null));
    }

    #endregion

    #region Playback

    private OperationResult ResetCore(IVideoRenderTarget? videoTarget, IVideoRenderTarget? subtitleTarget)
    {
        StopCore();

        _video.Target = videoTarget;
        _video.Target?.SurfaceSizeChanged += VideoTarget_SurfaceSizeChanged;
        _subtitle.Target = subtitleTarget;
        _subtitle.Target?.SurfaceSizeChanged += SubtitleTarget_SurfaceSizeChanged;
        _subtitle.LayoutSource = _video;

        _timeline.SetPosition(TimeSpan.Zero);
        _timeline.AdvanceEpoch(TimeSpan.Zero, SeekMode.Fast);

        _events.SetState(MediaEngineState.Paused);
        return OperationResult.Success();
    }

    private OperationResult<MediaSource> AddSourceCore(MediaLocator locator)
    {
        OperationResultCreator<MediaSource> result = new();

        if (_disposed)
        {
            return result.Failure("The pipeline has already been disposed.");
        }

        ISourceProviderFactory? sourceFactory = SourceFactory;
        if (sourceFactory is null)
        {
            return result.Failure("No source provider factory was registered.");
        }

        OperationResult opened = sourceFactory.TryOpen(locator, out IMediaSource? source);
        if (!opened.IsSuccessful)
        {
            return result.Failure(opened);
        }

        if (source is null)
        {
            return result.Failure("The source opened nothing to play.");
        }

        MediaSource added = new(source, locator);
        MediaSourceHolder selection = _selection;
        _selection = new MediaSourceHolder([.. selection.Sources, added], selection.VideoStream, selection.AudioStream, selection.SubtitleStream);
        return result.Success(added);
    }

    private OperationResult SelectStreamCore(MediaStreamKind kind, MediaStreamInfo? stream)
    {
        if (_disposed)
        {
            return OperationResult.Failure("The pipeline has already been disposed.");
        }

        if (!_events.CheckState(out MediaEngineState state, MediaEngineState.Paused, MediaEngineState.Playing, MediaEngineState.Ended))
        {
            return OperationResult.Failure($"Cannot change tracks when the state is {state}.");
        }

        if (stream is not null)
        {
            if (_selection.TryGetSourceOf(stream) is null)
            {
                return OperationResult.Failure($"Stream {stream.Index} is not part of anything the pipeline was given.");
            }

            if (stream.Kind != kind)
            {
                return OperationResult.Failure($"Stream {stream.Index} is a {stream.Kind} stream, not a {kind} stream.");
            }
        }

        MediaSourceHolder selection = _selection;
        _selection = new MediaSourceHolder(
            selection.Sources,
            kind == MediaStreamKind.Video ? (VideoStreamInfo?)stream : selection.VideoStream,
            kind == MediaStreamKind.Audio ? (AudioStreamInfo?)stream : selection.AudioStream,
            kind == MediaStreamKind.Subtitle ? (SubtitleStreamInfo?)stream : selection.SubtitleStream);
        return OperationResult.Success();
    }

    private OperationResult PlayCore()
    {
        if (_disposed)
        {
            return OperationResult.Failure("The pipeline has already been disposed.");
        }

        MediaSourceHolder? sourceHolder = _source.Latest?.Component;
        if (sourceHolder?.VideoStream is null && sourceHolder?.AudioStream is null)
        {
            return OperationResult.Failure("No track is selected. Choose a video or audio track before playing.");
        }

        if (!_events.CheckState(out MediaEngineState state, MediaEngineState.Paused))
        {
            return OperationResult.Failure($"Cannot play when the state is {state}.");
        }

        _events.SetState(MediaEngineState.Playing);
        WakeAll();
        return OperationResult.Success();
    }

    private OperationResult PauseCore()
    {
        if (_disposed)
        {
            return OperationResult.Failure("The pipeline has already been disposed.");
        }

        if (!_events.CheckState(out MediaEngineState state, MediaEngineState.Playing))
        {
            return OperationResult.Failure($"Cannot pause when the state is {state}.");
        }

        _events.SetState(MediaEngineState.Paused);
        WakeAll();
        return OperationResult.Success();
    }

    private OperationResult SeekCore(TimeSpan position, SeekMode mode)
    {
        if (_disposed)
        {
            return OperationResult.Failure("The pipeline has already been disposed.");
        }

        if (mode is not (SeekMode.Fast or SeekMode.Precise))
        {
            return OperationResult.Failure($"The seek mode {mode} is unknown.");
        }

        if (!_events.CheckState(out MediaEngineState state, MediaEngineState.Paused, MediaEngineState.Playing, MediaEngineState.Ended))
        {
            return OperationResult.Failure($"Cannot seek when the state is {state}.");
        }

        MediaSource?[] sources = [_selection.VideoSource, _selection.AudioSource, _selection.SubtitleSource];
        foreach (MediaSource source in sources.OfType<MediaSource>())
        {
            if (!source.Source.CanSeek)
            {
                return OperationResult.Failure($"A source cannot seek, so nothing was moved: {source.Locator}");
            }
        }

        if (state == MediaEngineState.Ended)
        {
            _events.SetState(MediaEngineState.Paused);
        }

        _timeline.AdvanceEpoch(position, mode);
        _timeline.SeekTo(position);

        WakeAll();
        return OperationResult.Success();
    }

    private OperationResult<ReconcileResult> ReconcileCore()
    {
        OperationResultCreator<ReconcileResult> result = new();

        if (_disposed)
        {
            return result.Failure("The pipeline has already been disposed.");
        }

        if (!_events.CheckState(out MediaEngineState state, MediaEngineState.Paused, MediaEngineState.Playing, MediaEngineState.Ended))
        {
            return result.Failure($"Cannot change the pipeline's components when the state is {state}.");
        }

        MediaSourceHolder desired = CreateSourceHolderFromSelection();
        ComponentResolver resolver = Components.Clone();
        OperationResult<ChainChange> video = BuildVideoChain(resolver, desired);
        OperationResult<ChainChange> audio = BuildAudioChain(resolver, desired);
        OperationResult<ChainChange> subtitle = BuildSubtitleChain(resolver, desired);

        bool changed = (video.IsSuccessful && video.Result != ChainChange.None)
            || (audio.IsSuccessful && audio.Result != ChainChange.None)
            || (subtitle.IsSuccessful && subtitle.Result != ChainChange.None);

        if (changed)
        {
            _timeline.AdvanceEpoch(_timeline.Position, SeekMode.Precise);
            WakeAll();

            if (_host is null)
            {
                StartThreads();
            }
        }

        return result.Success(new ReconcileResult
        {
            Video = video.IsSuccessful ? OperationResult.Success() : OperationResult.Failure(video),
            Audio = audio.IsSuccessful ? OperationResult.Success() : OperationResult.Failure(audio),
            Subtitle = subtitle.IsSuccessful ? OperationResult.Success() : OperationResult.Failure(subtitle),
        });
    }

    private OperationResult StopCore()
    {
        StopThreads();

        _video.PacketQueue.Clear();
        _audio.PacketQueue.Clear();
        _subtitle.PacketQueue.Clear();

        while (_video.FrameQueue.TryDequeue(out FrameEntry frame))
        {
            frame.Frame?.Dispose();
        }

        _audio.BufferQueue.Clear();

        foreach (MediaSource source in _selection.Sources)
        {
            source.Source.Dispose();
        }

        _selection = new MediaSourceHolder([], null, null, null);

        _source.Reset();
        _video.Decoder.Reset();
        _audio.Decoder.Reset();
        _video.Renderer.Reset();
        _audio.Renderer.CurrentComponent?.Flush();
        _audio.Renderer.Reset();
        _subtitle.Decoder.Reset();
        _subtitle.Renderer.Reset();

        _video.Target?.SurfaceSizeChanged -= VideoTarget_SurfaceSizeChanged;
        _video.Target = null;
        _subtitle.Target?.SurfaceSizeChanged -= SubtitleTarget_SurfaceSizeChanged;
        _subtitle.Target = null;
        _video.Binding = default;
        _audio.Binding = default;
        _subtitle.Binding = default;

        _timeline.Stop();

        _timeline.EndingEpoch = -1;
        _videoRenderEndedEpoch = -1;
        _audioRenderEndedEpoch = -1;

        foreach (IChainFeed feed in _feeds)
        {
            feed.Tail.Reset();
        }

        Interlocked.Exchange(ref _video.PresentedFrameCount, 0);
        Interlocked.Exchange(ref _video.DroppedFrameCount, 0);
        Interlocked.Exchange(ref _video.DecodedFrameCount, 0);
        Interlocked.Exchange(ref _video.DecodeTicks, 0);
        Interlocked.Exchange(ref _video.RenderTicks, 0);
        Interlocked.Exchange(ref _video.PictureTicks, -1);

        _timeline.AdvanceEpoch(TimeSpan.Zero, SeekMode.Fast);
        _events.SetState(MediaEngineState.None);
        return OperationResult.Success();
    }

    private void StartThreads()
    {
        CoroutineHost host = new("Pipeline - Reader", [
            stop => _reader.Run(stop, NotifySourceEnded),
            stop => _audio.Decode(stop, _reader.TryWake),
            stop => _subtitle.Decode(stop, _reader.TryWake),
        ]);

        host.Start();
        _host = host;
        _video.Start(_reader.TryWake, IsVideoStreamSpent, NotifyVideoRenderEnded);
        _audio.Start(IsAudioStreamSpent, NotifyAudioRenderEnded);
        _subtitle.Start();
    }

    private void StopThreads()
    {
        _host?.RequestStop();
        _video.RequestStop();
        _audio.RequestStop();
        _subtitle.RequestStop();

        WakeAll();

        _host?.Join();
        _host = null;
        _video.Join();
        _audio.Join();
        _subtitle.Join();
    }
    private void NotifySourceEnded(long epoch)
    {
        _timeline.EndingEpoch = epoch;
        TryNotifyPlaybackEnded();
    }

    private void NotifyVideoRenderEnded(long epoch)
    {
        _videoRenderEndedEpoch = epoch;
        TryNotifyPlaybackEnded();
    }

    private bool IsVideoStreamSpent(long epoch, TimeSpan position)
    {
        return _video.Tail.IsSpent(epoch, position, FurthestTailTicks);
    }

    private bool IsAudioStreamSpent(long epoch, TimeSpan position)
    {
        return _audio.Tail.IsSpent(epoch, position, FurthestTailTicks);
    }

    private long FurthestTailTicks => Math.Max(_video.Tail.Ticks, _audio.Tail.Ticks);

    private int ChooseSeekLanding(MediaSource source, TimeSpan position)
    {
        VideoStreamInfo? video = _video.Binding.Source == source ? _video.Binding.Stream : null;
        AudioStreamInfo? audio = _audio.Binding.Source == source ? _audio.Binding.Stream : null;

        if (video is null)
        {
            if (audio is null)
            {
                return -1;
            }

            return audio.Index;
        }

        if (audio is not null &&
            video.Duration > TimeSpan.Zero &&
            audio.Duration > video.Duration &&
            position > video.Duration)
        {
            return audio.Index;
        }

        return video.Index;
    }

    private void NotifyAudioRenderEnded(long epoch)
    {
        _audioRenderEndedEpoch = epoch;
        TryNotifyPlaybackEnded();
    }

    private void TryNotifyPlaybackEnded()
    {
        if (!_events.CheckState(out _, MediaEngineState.Playing, MediaEngineState.Paused))
        {
            return;
        }

        if (_timeline.EndingEpoch != _timeline.Epoch)
        {
            return;
        }

        MediaSourceHolder? sourceHolder = _source.Latest?.Component;
        if (sourceHolder is null)
        {
            return;
        }

        if (sourceHolder.VideoStream is not null && _videoRenderEndedEpoch != _timeline.EndingEpoch)
        {
            return;
        }

        if (sourceHolder.AudioStream is not null && _audioRenderEndedEpoch != _timeline.EndingEpoch)
        {
            return;
        }

        _events.SetState(MediaEngineState.Ended);
        WakeAll();
    }

    #endregion

    #region Events

    private void VideoTarget_SurfaceSizeChanged()
    {
        _video.RenderNode.Wake();
        _subtitle.RenderNode.Wake();
    }

    private void SubtitleTarget_SurfaceSizeChanged()
    {
        Interlocked.Increment(ref _subtitle.SurfaceVersion);
        _subtitle.RenderNode.Wake();
    }

    #endregion

    #region Communication

    private void NotifyClockMoved()
    {
        // Subtitle might be changed
        _subtitle.RenderNode.Wake();
    }

    private void WakeAll()
    {
        foreach (IPipelineWakeTarget target in _wakeTargets)
        {
            target.Wake();
        }
    }

    #endregion
}
