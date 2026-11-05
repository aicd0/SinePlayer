// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Common.Threading;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Abstractions.Timing;
using SinePlayer.Core.Playback.Selection;
using SinePlayer.Core.Playback.Timing;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Pipeline;

public sealed partial class PlaybackPipeline : IDisposable
{
    #region Constants

    private const string TAG = nameof(PlaybackPipeline);

    private const int FrameQueueCapacity = 8;
    private const int DecoderBufferedFrames = FrameQueueCapacity + 4;

    private const int ReadAheadMinMilliseconds = 8000;
    private const int ReadAheadMaxMilliseconds = 10000;
    private const int ReadAheadWakeMilliseconds = ReadAheadMinMilliseconds / 2;
    private const int FallbackFrameMilliseconds = 40;

    private const int VideoStarvationMilliseconds = 200;
    private const int VideoStarvationResumeMilliseconds = 100;
    private const int VideoIdleTimeoutMilliseconds = 50;
    private const int AudioIdleTimeoutMilliseconds = 5;
    private const int SubtitleChangeIntervalMilliseconds = 16;
    private const int ClockSyncToleranceMilliseconds = 10;
    private const int AudioDeviceLeewayMilliseconds = 3000;

    private static readonly TimeSpan ReadAheadMin = TimeSpan.FromMilliseconds(ReadAheadMinMilliseconds);
    private static readonly TimeSpan ReadAheadMax = TimeSpan.FromMilliseconds(ReadAheadMaxMilliseconds);
    private static readonly TimeSpan ReadAheadWake = TimeSpan.FromMilliseconds(ReadAheadWakeMilliseconds);

    #endregion

    #region Variables

    private readonly ITaskDispatcher _playbackQueue = TaskDispatcher.Factory.NewQueue("Pipeline - Playback");
    private readonly SeqLock<VideoTransform> _videoTransform = new(VideoTransform.Identity);
    private readonly SeqLock<SubtitlePresentation> _subtitlePresentation = new(SubtitlePresentation.Identity);

    private volatile MediaSourceHolder _selection = new([], null, null, null);

    private IVideoRenderTarget? _videoTarget;
    private IVideoRenderTarget? _subtitleTarget;
    private long _subtitleSurfaceVersion;
    private readonly ComponentSlot<MediaSourceHolder> _source = new();
    private readonly ComponentSlot<IVideoDecoder> _videoDecoder = new();
    private readonly ComponentSlot<IVideoRenderer> _videoRenderer = new();
    private readonly ComponentSlot<IAudioDecoder> _audioDecoder = new();
    private readonly ComponentSlot<IAudioRenderer> _audioRenderer = new();
    private readonly ComponentSlot<ISubtitleDecoder> _subtitleDecoder = new();
    private readonly ComponentSlot<ISubtitleRenderer> _subtitleRenderer = new();

    private readonly PacketQueue _videoPacketQueue = new();
    private readonly PacketQueue _audioPacketQueue = new();
    private readonly PacketQueue _subtitlePacketQueue = new();
    private readonly ConcurrentQueue<FrameEntry> _frameQueue = new();
    private readonly AudioQueue _audioQueue = new();
    private readonly SubtitleCueWindow _cueWindow = new();

    private Thread? _readerThread;
    private Thread? _videoDecodeThread;
    private Thread? _videoRenderThread;
    private Thread? _audioRenderThread;
    private Thread? _subtitleRenderThread;

    private readonly PipelineNodeAsync _readerNode = new();
    private readonly PipelineNode _videoDecodeNode = new();
    private readonly PipelineNodeAsync _audioDecodeNode = new();
    private readonly PipelineNodeAsync _subtitleDecodeNode = new();
    private readonly PipelineNode _videoRenderNode = new();
    private readonly PipelineNode _audioRenderNode = new();
    private readonly PipelineNode _subtitleRenderNode = new();

    private bool _disposed;
    private volatile MediaEngineState _state = MediaEngineState.None;
    private volatile string? _error;
    private bool _threadsStarted;
    private volatile bool _stopRequested;

    private readonly SeqLock<EpochGate> _epochGate = new(new());
    private long _endingEpoch = -1;
    private long _videoRenderEndedEpoch = -1;
    private long _audioRenderEndedEpoch = -1;

    private readonly Lock _clockLock = new();
    private readonly IPlaybackClock _clock = new StopwatchClock();
    private long _anchoredEpoch = -1;
    private volatile bool _videoStarved;
    private volatile bool _audioStarved;

    private long _readerTailEpoch = -1;
    private long _readerTailVideoTicks = -1;
    private long _readerTailAudioTicks = -1;
    private long _readerTailSubtitleTicks = -1;

    private long _presentedFrameCount;
    private long _droppedFrameCount;
    private long _decodedFrameCount;
    private long _decodeTicks;
    private long _renderTicks;
    private long _pictureTicks = -1;

    private class EpochGate
    {
        public long Epoch { get; init; }
        public TimeSpan SeekPosition { get; init; }
        public SeekMode SeekMode { get; init; }
    }

    #endregion

    #region Public API

    public event Action<MediaEngineState, string?>? StateChanged;

    public event Action<string>? WarningRaised;

    public ComponentResolver Components { get; set; } = new();

    public AudioFilterChain AudioFilters { get; } = new();

    public ISourceProviderFactory? SourceFactory { get; set; }

    public ISynchronizer Synchronizer { get; set; } = new DefaultSynchronizer();

    public VideoTransform VideoTransform
    {
        get => _videoTransform.Read();
        set
        {
            if (_videoTransform.Read() == value)
            {
                return;
            }

            _videoTransform.Write(_ => value);
            _videoRenderNode.Wake();
            _subtitleRenderNode.Wake();
        }
    }

    public SubtitlePresentation SubtitlePresentation
    {
        get => _subtitlePresentation.Read();
        set
        {
            if (_subtitlePresentation.Read() == value)
            {
                return;
            }

            _subtitlePresentation.Write(_ => value);
            _subtitleRenderNode.Wake();
        }
    }

    public MediaEngineState State => _state;

    public string? Error => _error;

    public IReadOnlyList<MediaSource> Sources => _selection.Sources;

    public VideoStreamInfo? SelectedVideoStream => _selection.VideoStream;

    public AudioStreamInfo? SelectedAudioStream => _selection.AudioStream;

    public SubtitleStreamInfo? SelectedSubtitleStream => _selection.SubtitleStream;

    public string? VideoDecoderName => _videoDecoder.CurrentComponent?.Name;

    public string? AudioDecoderName => _audioDecoder.CurrentComponent?.Name;

    public string? VideoRendererName => _videoRenderer.CurrentComponent?.Name;

    public string? AudioRendererName => _audioRenderer.CurrentComponent?.Name;

    public string? SubtitleDecoderName => _subtitleDecoder.CurrentComponent?.Name;

    public string? SubtitleRendererName => _subtitleRenderer.CurrentComponent?.Name;

    public bool IsVideoHardwareAccelerated => _videoDecoder.CurrentComponent?.IsHardwareAccelerated ?? false;

    public bool IsAudioHardwareAccelerated => _audioDecoder.CurrentComponent?.IsHardwareAccelerated ?? false;

    public TimeSpan Position => _clock.Position;

    public long PresentedFrameCount => Interlocked.Read(ref _presentedFrameCount);

    public long DroppedFrameCount => Interlocked.Read(ref _droppedFrameCount);

    public string? StarvedFor => _videoStarved ? "video" : _audioStarved ? "audio" : null;

    internal TimeSpan LastPresentedTimestamp
    {
        get
        {
            long ticks = Interlocked.Read(ref _pictureTicks);
            return ticks < 0 ? TimeSpan.Zero : TimeSpan.FromTicks(ticks);
        }
    }

    public double AverageVideoDecodeMilliseconds
    {
        get
        {
            long frames = Interlocked.Read(ref _decodedFrameCount);
            return frames <= 0 ? 0 : Interlocked.Read(ref _decodeTicks) * 1000.0 / Stopwatch.Frequency / frames;
        }
    }

    public double AverageVideoRenderMilliseconds
    {
        get
        {
            long frames = Interlocked.Read(ref _presentedFrameCount);
            return frames <= 0 ? 0 : Interlocked.Read(ref _renderTicks) * 1000.0 / Stopwatch.Frequency / frames;
        }
    }

    public long AudioUnderruns => _audioRenderer.CurrentComponent?.UnderrunCount ?? 0;

    public TimeSpan PictureLag
    {
        get
        {
            long ticks = Interlocked.Read(ref _pictureTicks);
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
        Task<OperationResult> teardown = Submit(() =>
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
        return SubmitAndLog(nameof(Reset), () => ResetCore(videoTarget, subtitleTarget));
    }

    public Task<OperationResult<MediaSource>> AddSource(MediaLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        return SubmitAndLog(nameof(AddSource), () => AddSourceCore(locator));
    }

    public Task<OperationResult> SelectStream(MediaStreamKind kind, MediaStreamInfo? stream)
    {
        return SubmitAndLog(nameof(SelectStream), () => SelectStreamCore(kind, stream));
    }

    public Task<OperationResult> Play()
    {
        return SubmitAndLog(nameof(Play), PlayCore);
    }

    public Task<OperationResult> Pause()
    {
        return SubmitAndLog(nameof(Pause), PauseCore);
    }

    public Task<OperationResult> Seek(TimeSpan position, SeekMode mode = SeekMode.Fast)
    {
        return SubmitAndLog(nameof(Seek), () => SeekCore(position, mode));
    }

    public Task<OperationResult<ReconcileResult>> Reconcile()
    {
        return SubmitAndLog(nameof(Reconcile), ReconcileCore);
    }

    public Task<OperationResult> Stop()
    {
        return SubmitAndLog(nameof(Stop), () => ResetCore(null, null));
    }

    #endregion

    #region Playback

    private OperationResult ResetCore(IVideoRenderTarget? videoTarget, IVideoRenderTarget? subtitleTarget)
    {
        StopCore();

        _videoTarget = videoTarget;
        _videoTarget?.SurfaceSizeChanged += VideoTarget_SurfaceSizeChanged;
        _subtitleTarget = subtitleTarget;
        _subtitleTarget?.SurfaceSizeChanged += SubtitleTarget_SurfaceSizeChanged;

        lock (_clockLock)
        {
            _clock.SetPosition(TimeSpan.Zero);
        }

        AdvanceEpoch(TimeSpan.Zero, SeekMode.Fast);
        SetState(MediaEngineState.Paused);
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

        if (!CheckState(out MediaEngineState state, MediaEngineState.Paused, MediaEngineState.Playing, MediaEngineState.Ended))
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

        if (!CheckState(out MediaEngineState state, MediaEngineState.Paused))
        {
            return OperationResult.Failure($"Cannot play when the state is {state}.");
        }

        SetState(MediaEngineState.Playing);
        WakeAll();
        return OperationResult.Success();
    }

    private OperationResult PauseCore()
    {
        if (_disposed)
        {
            return OperationResult.Failure("The pipeline has already been disposed.");
        }

        if (!CheckState(out MediaEngineState state, MediaEngineState.Playing))
        {
            return OperationResult.Failure($"Cannot pause when the state is {state}.");
        }

        SetState(MediaEngineState.Paused);
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

        if (!CheckState(out MediaEngineState state, MediaEngineState.Paused, MediaEngineState.Playing, MediaEngineState.Ended))
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
            SetState(MediaEngineState.Paused);
        }

        AdvanceEpoch(position, mode);

        lock (_clockLock)
        {
            if (_anchoredEpoch != _epochGate.Read().Epoch)
            {
                _clock.SetPosition(position);
            }
        }

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

        if (!CheckState(out MediaEngineState state, MediaEngineState.Paused, MediaEngineState.Playing, MediaEngineState.Ended))
        {
            return result.Failure($"Cannot change the pipeline's components when the state is {state}.");
        }

        MediaSourceHolder sourceHolder = CreateSourceHolderFromSelection();
        ChainChanges sourceChanges = DiffSource(sourceHolder);
        ComponentResolver resolver = Components.Clone();
        OperationResult<ChainChanges> video = BuildVideoChain(resolver, sourceHolder, sourceChanges);
        OperationResult<ChainChanges> audio = BuildAudioChain(resolver, sourceHolder, sourceChanges);
        OperationResult<ChainChanges> subtitle = BuildSubtitleChain(resolver, sourceHolder, sourceChanges);
        ChainChanges changes = ChainChanges.None;
        changes |= video.IsSuccessful ? video.Result : ChainChanges.None;
        changes |= audio.IsSuccessful ? audio.Result : ChainChanges.None;
        changes |= subtitle.IsSuccessful ? subtitle.Result : ChainChanges.None;

        if (changes != ChainChanges.None)
        {
            AdvanceEpoch(_clock.Position, SeekMode.Precise);
            WakeAll();

            if (!_threadsStarted)
            {
                _threadsStarted = true;
                _stopRequested = false;
                StartReaderThread();
                StartVideoDecodeThread();
                StartVideoRenderThread();
                StartAudioRenderThread();
                StartSubtitleRenderThread();
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
        _stopRequested = true;
        WakeAll();

        _readerThread?.Join();
        _readerThread = null;
        _videoDecodeThread?.Join();
        _videoDecodeThread = null;
        _videoRenderThread?.Join();
        _videoRenderThread = null;
        _audioRenderThread?.Join();
        _audioRenderThread = null;
        _subtitleRenderThread?.Join();
        _subtitleRenderThread = null;
        _threadsStarted = false;

        _videoPacketQueue.Clear();
        _audioPacketQueue.Clear();
        _subtitlePacketQueue.Clear();

        while (_frameQueue.TryDequeue(out FrameEntry frame))
        {
            frame.Frame?.Dispose();
        }

        _audioQueue.Clear();

        foreach (MediaSource source in _selection.Sources)
        {
            source.Source.Dispose();
        }

        _selection = new MediaSourceHolder([], null, null, null);

        _source.Reset();
        _videoDecoder.Reset();
        _audioDecoder.Reset();
        _videoRenderer.Reset();
        _audioRenderer.CurrentComponent?.Flush();
        _audioRenderer.Reset();
        _subtitleDecoder.Reset();
        _subtitleRenderer.Reset();

        _videoTarget?.SurfaceSizeChanged -= VideoTarget_SurfaceSizeChanged;
        _videoTarget = null;
        _subtitleTarget?.SurfaceSizeChanged -= SubtitleTarget_SurfaceSizeChanged;
        _subtitleTarget = null;

        lock (_clockLock)
        {
            _clock.Pause();
            _anchoredEpoch = -1;
            _videoStarved = false;
            _audioStarved = false;
        }

        Interlocked.Exchange(ref _endingEpoch, -1);
        _videoRenderEndedEpoch = -1;
        _audioRenderEndedEpoch = -1;

        Volatile.Write(ref _readerTailEpoch, -1);
        Interlocked.Exchange(ref _readerTailVideoTicks, -1);
        Interlocked.Exchange(ref _readerTailAudioTicks, -1);
        Interlocked.Exchange(ref _readerTailSubtitleTicks, -1);

        Interlocked.Exchange(ref _presentedFrameCount, 0);
        Interlocked.Exchange(ref _droppedFrameCount, 0);
        Interlocked.Exchange(ref _decodedFrameCount, 0);
        Interlocked.Exchange(ref _decodeTicks, 0);
        Interlocked.Exchange(ref _renderTicks, 0);
        Interlocked.Exchange(ref _pictureTicks, -1);

        AdvanceEpoch(TimeSpan.Zero, SeekMode.Fast);
        SetState(MediaEngineState.None);
        return OperationResult.Success();
    }

    private void StartReaderThread()
    {
        if (_readerThread is not null)
        {
            return;
        }

        void ReaderThreadCore()
        {
            SingleThreadSynchronizationContext context = new();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                async Task AsyncCore()
                {
                    try
                    {
                        await Task.WhenAll(ReaderCore(), AudioDecodeCore(), SubtitleDecodeCore());
                    }
                    finally
                    {
                        context.Complete();
                    }
                }

                Task task = AsyncCore();
                context.RunOnCurrentThread();
                task.Wait();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                context.Dispose();
            }
        }

        _readerThread = new Thread(ReaderThreadCore)
        {
            IsBackground = true,
            Name = "Pipeline - Reader",
        };
        _readerThread.Start();
    }

    private void StartVideoDecodeThread()
    {
        if (_videoDecodeThread is not null)
        {
            return;
        }

        _videoDecodeThread = new Thread(VideoDecodeCore)
        {
            IsBackground = true,
            Name = "Pipeline - Video decode",
        };
        _videoDecodeThread.Start();
    }

    private void StartVideoRenderThread()
    {
        if (_videoRenderThread is not null)
        {
            return;
        }

        _videoRenderThread = new Thread(VideoRenderCore)
        {
            IsBackground = true,
            Name = "Pipeline - Video render",
        };
        _videoRenderThread.Start();
    }

    private void StartAudioRenderThread()
    {
        if (_audioRenderThread is not null)
        {
            return;
        }

        _audioRenderThread = new Thread(AudioRenderCore)
        {
            IsBackground = true,
            Name = "Pipeline - Audio render",
        };
        _audioRenderThread.Start();
    }

    private void StartSubtitleRenderThread()
    {
        if (_subtitleRenderThread is not null)
        {
            return;
        }

        _subtitleRenderThread = new Thread(SubtitleRenderCore)
        {
            IsBackground = true,
            Name = "Pipeline - Subtitle render",
        };
        _subtitleRenderThread.Start();
    }

    private void NotifySourceEnded(long epoch)
    {
        Interlocked.Exchange(ref _endingEpoch, epoch);
        TryNotifyPlaybackEnded();
    }

    private void NotifyVideoRenderEnded(long epoch)
    {
        _videoRenderEndedEpoch = epoch;
        TryNotifyPlaybackEnded();
    }

    private void NotifyAudioRenderEnded(long epoch)
    {
        _audioRenderEndedEpoch = epoch;
        TryNotifyPlaybackEnded();
    }

    private void TryNotifyPlaybackEnded()
    {
        if (!CheckState(out _, MediaEngineState.Playing, MediaEngineState.Paused))
        {
            return;
        }

        if (_endingEpoch != _epochGate.Read().Epoch)
        {
            return;
        }

        MediaSourceHolder? sourceHolder = _source.Latest?.Component;
        if (sourceHolder is null)
        {
            return;
        }

        if (sourceHolder.VideoStream is not null && _videoRenderEndedEpoch != _endingEpoch)
        {
            return;
        }

        if (sourceHolder.AudioStream is not null && _audioRenderEndedEpoch != _endingEpoch)
        {
            return;
        }

        SetState(MediaEngineState.Ended);
        WakeAll();
    }

    private void AdvanceEpoch(TimeSpan position, SeekMode mode)
    {
        _epochGate.Write(x => new EpochGate()
        {
            Epoch = x.Epoch + 1,
            SeekPosition = position,
            SeekMode = mode,
        });
    }

    private OperationResult Fail(string message, Exception? exception = null)
    {
        PlaybackLogger.E(TAG, message, exception);
        SetState(MediaEngineState.Error, message);
        WakeAll();
        return OperationResult.Failure(message, exception);
    }

    private void SetState(MediaEngineState state, string? error = null)
    {
        bool changed = _state != state || _error != error;
        _state = state;
        _error = error;

        if (!changed)
        {
            return;
        }

        StateChanged?.Invoke(state, error);
    }

    private void RaiseWarning(string warning)
    {
        PlaybackLogger.W(TAG, warning);
        WarningRaised?.Invoke(warning);
    }

    #endregion

    #region Components

    [Flags]
    private enum ChainChanges
    {
        None = 0,
        Source = 1,
        VideoRenderer = 2,
        VideoDecoder = 4,
        AudioRenderer = 8,
        AudioDecoder = 16,
        SubtitleRenderer = 32,
        SubtitleDecoder = 64,
    }

    private ChainChanges DiffSource(MediaSourceHolder sourceHolder)
    {
        MediaSourceHolder? current = _source.Latest?.Component;
        if (current is null)
        {
            return ChainChanges.Source
                | ChainChanges.VideoDecoder | ChainChanges.VideoRenderer
                | ChainChanges.AudioDecoder | ChainChanges.AudioRenderer
                | ChainChanges.SubtitleDecoder | ChainChanges.SubtitleRenderer;
        }

        static bool SameStream(MediaSource? beforeSource, MediaStreamInfo? before, MediaSource? afterSource, MediaStreamInfo? after)
        {
            return beforeSource == afterSource && before == after;
        }

        ChainChanges changes = ChainChanges.None;

        if (!SameStream(current.VideoSource, current.VideoStream, sourceHolder.VideoSource, sourceHolder.VideoStream))
        {
            changes |= ChainChanges.Source | ChainChanges.VideoDecoder | ChainChanges.VideoRenderer;
        }

        if (!SameStream(current.AudioSource, current.AudioStream, sourceHolder.AudioSource, sourceHolder.AudioStream))
        {
            changes |= ChainChanges.Source | ChainChanges.AudioDecoder | ChainChanges.AudioRenderer;
        }

        if (!SameStream(current.SubtitleSource, current.SubtitleStream, sourceHolder.SubtitleSource, sourceHolder.SubtitleStream))
        {
            changes |= ChainChanges.Source | ChainChanges.SubtitleDecoder | ChainChanges.SubtitleRenderer;
        }

        return changes;
    }

    private OperationResult<ChainChanges> BuildVideoChain(
        ComponentResolver resolver,
        MediaSourceHolder sourceHolder,
        ChainChanges sourceChanges)
    {
        OperationResultCreator<ChainChanges> result = new();

        ChainChanges changed = sourceChanges & ChainChanges.Source;
        PipelineComponent<IVideoRenderer>? renderer = null;
        PipelineComponent<IVideoDecoder>? decoder = null;
        try
        {
            if (sourceHolder.VideoSource is { } videoSource && sourceHolder.VideoStream is { } videoStream)
            {
                IVideoRenderTarget? videoTarget = _videoTarget;
                if (videoTarget is null)
                {
                    return result.Failure("The file has video but no presentation surface was supplied.");
                }

                // Resolve video renderer
                bool rebuildVideoRenderer = sourceChanges.HasFlag(ChainChanges.VideoRenderer);
                PipelineComponent<IVideoRenderer>? latestRenderer = _videoRenderer.Latest;
                OperationResult<PipelineComponent<IVideoRenderer>?> rendererResolved = ResolveComponent(
                    resolver.ResolveVideoRenderers(),
                    rebuildVideoRenderer ? null : latestRenderer?.Signature,
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

                    changed |= ChainChanges.VideoRenderer;
                }

                // Resolve video decoder
                bool rebuildVideoDecoder = renderer is not null || sourceChanges.HasFlag(ChainChanges.VideoDecoder);
                PipelineComponent<IVideoDecoder>? latestDecoder = _videoDecoder.Latest;
                IGpuDevice? gpuDevice = (renderer ?? latestRenderer)?.Component?.GpuDevice;
                OperationResult<PipelineComponent<IVideoDecoder>?> decoderResolved = ResolveComponent(
                    resolver.ResolveVideoDecoders(videoSource.Source, videoStream, gpuDevice, DecoderBufferedFrames),
                    rebuildVideoDecoder ? null : latestDecoder?.Signature,
                    "video decoder");
                if (!decoderResolved.IsSuccessful)
                {
                    return result.Failure(decoderResolved.Message);
                }

                decoder = decoderResolved.Result;
                if (decoder is not null)
                {
                    changed |= ChainChanges.VideoDecoder;
                }
            }
            else
            {
                if (_videoDecoder.HasComponent)
                {
                    changed |= sourceChanges & ChainChanges.VideoDecoder;
                }

                if (_videoRenderer.HasComponent)
                {
                    changed |= sourceChanges & ChainChanges.VideoRenderer;
                }
            }

            if (changed.HasFlag(ChainChanges.VideoDecoder) || changed.HasFlag(ChainChanges.VideoRenderer))
            {
                PipelineVersion versions = BumpVersions(changed);

                if (changed.HasFlag(ChainChanges.Source))
                {
                    MediaSourceHolder newSourceHolder = CreateSourceHolderFromChange(sourceHolder, MediaStreamKind.Video);
                    _source.Publish(new(newSourceHolder, "source"), versions);
                }

                if (changed.HasFlag(ChainChanges.VideoDecoder))
                {
                    _videoDecoder.Publish(decoder, versions);
                    decoder = null;
                }

                if (changed.HasFlag(ChainChanges.VideoRenderer))
                {
                    _videoRenderer.Publish(renderer, versions);
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

    private OperationResult<ChainChanges> BuildAudioChain(
        ComponentResolver resolver,
        MediaSourceHolder sourceHolder,
        ChainChanges sourceChanges)
    {
        OperationResultCreator<ChainChanges> result = new();

        ChainChanges changed = sourceChanges & ChainChanges.Source;
        PipelineComponent<IAudioRenderer>? renderer = null;
        PipelineComponent<IAudioDecoder>? decoder = null;
        AudioStreamInfo? negotiatedFormat = null;
        try
        {
            if (sourceHolder.AudioSource is { } audioSource && sourceHolder.AudioStream is { } audioStream)
            {
                // Resolve audio renderer
                bool rebuildAudioRenderer = sourceChanges.HasFlag(ChainChanges.AudioRenderer);
                PipelineComponent<IAudioRenderer>? latestRenderer = _audioRenderer.Latest;
                OperationResult<PipelineComponent<IAudioRenderer>?> rendererResolved = ResolveComponent(
                    resolver.ResolveAudioRenderers(),
                    rebuildAudioRenderer ? null : latestRenderer?.Signature,
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
                    changed |= ChainChanges.AudioRenderer;
                }

                // Resolve audio decoder
                bool rebuildAudioDecoder = sourceChanges.HasFlag(ChainChanges.AudioDecoder);
                PipelineComponent<IAudioDecoder>? latestDecoder = _audioDecoder.Latest;
                OperationResult<PipelineComponent<IAudioDecoder>?> decoderResolved = ResolveComponent(
                    resolver.ResolveAudioDecoders(audioSource.Source, audioStream),
                    rebuildAudioDecoder ? null : latestDecoder?.Signature,
                    "audio decoder");
                if (!decoderResolved.IsSuccessful)
                {
                    return result.Failure(decoderResolved.Message);
                }

                decoder = decoderResolved.Result;
                if (decoder is not null)
                {
                    changed |= ChainChanges.AudioDecoder;
                }

                if (negotiatedFormat is not null)
                {
                    AudioFilters.Configure(audioStream, negotiatedFormat);
                }
            }
            else
            {
                if (_audioDecoder.HasComponent)
                {
                    changed |= sourceChanges & ChainChanges.AudioDecoder;
                }

                if (_audioRenderer.HasComponent)
                {
                    changed |= sourceChanges & ChainChanges.AudioRenderer;
                }
            }

            if (changed.HasFlag(ChainChanges.AudioDecoder) || changed.HasFlag(ChainChanges.AudioRenderer))
            {
                PipelineVersion versions = BumpVersions(changed);

                if (changed.HasFlag(ChainChanges.Source))
                {
                    MediaSourceHolder newSourceHolder = CreateSourceHolderFromChange(sourceHolder, MediaStreamKind.Audio);
                    _source.Publish(new(newSourceHolder, "source"), versions);
                }

                if (changed.HasFlag(ChainChanges.AudioDecoder))
                {
                    _audioDecoder.Publish(decoder, versions);
                    decoder = null;
                }

                if (changed.HasFlag(ChainChanges.AudioRenderer))
                {
                    _audioRenderer.Publish(renderer, versions);
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

    private OperationResult<ChainChanges> BuildSubtitleChain(
        ComponentResolver resolver,
        MediaSourceHolder sourceHolder,
        ChainChanges sourceChanges)
    {
        OperationResultCreator<ChainChanges> result = new();

        ChainChanges changed = sourceChanges & ChainChanges.Source;
        PipelineComponent<ISubtitleDecoder>? decoder = null;
        PipelineComponent<ISubtitleRenderer>? renderer = null;
        try
        {
            if (sourceHolder.SubtitleSource is { } subtitleSource && sourceHolder.SubtitleStream is { } subtitleStream)
            {
                IVideoRenderTarget? subtitleTarget = _subtitleTarget;
                if (subtitleTarget is null)
                {
                    return result.Failure("The file has a subtitle track but no subtitle surface was supplied.");
                }

                // Resolve subtitle decoder
                bool rebuildSubtitleDecoder = sourceChanges.HasFlag(ChainChanges.SubtitleDecoder);
                PipelineComponent<ISubtitleDecoder>? latestDecoder = _subtitleDecoder.Latest;
                OperationResult<PipelineComponent<ISubtitleDecoder>?> decoderResolved = ResolveComponent(
                    resolver.ResolveSubtitleDecoders(subtitleSource.Source, subtitleStream),
                    rebuildSubtitleDecoder ? null : latestDecoder?.Signature,
                    "subtitle decoder");
                if (!decoderResolved.IsSuccessful)
                {
                    return result.Failure(decoderResolved.Message);
                }

                decoder = decoderResolved.Result;
                if (decoder is not null)
                {
                    changed |= ChainChanges.SubtitleDecoder;
                }

                // Resolve subtitle renderer
                PipelineComponent<ISubtitleDecoder>? runningDecoder = decoder ?? latestDecoder;
                if (runningDecoder is null)
                {
                    return result.Failure("The track has no decoder to say what its cues are made of.");
                }

                SubtitleCueFormat format = runningDecoder.Component.CueFormat;
                bool rebuildSubtitleRenderer = decoder is not null || sourceChanges.HasFlag(ChainChanges.SubtitleRenderer);
                PipelineComponent<ISubtitleRenderer>? latestRenderer = _subtitleRenderer.Latest;
                OperationResult<PipelineComponent<ISubtitleRenderer>?> rendererResolved = ResolveComponent(
                    resolver.ResolveSubtitleRenderers(subtitleStream, format),
                    rebuildSubtitleRenderer ? null : latestRenderer?.Signature,
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

                    changed |= ChainChanges.SubtitleRenderer;
                }
            }
            else
            {
                if (_subtitleDecoder.HasComponent)
                {
                    changed |= sourceChanges & ChainChanges.SubtitleDecoder;
                }

                if (_subtitleRenderer.HasComponent)
                {
                    changed |= sourceChanges & ChainChanges.SubtitleRenderer;
                }
            }

            if (changed.HasFlag(ChainChanges.SubtitleDecoder) || changed.HasFlag(ChainChanges.SubtitleRenderer))
            {
                PipelineVersion versions = BumpVersions(changed);

                if (changed.HasFlag(ChainChanges.Source))
                {
                    MediaSourceHolder newSourceHolder = CreateSourceHolderFromChange(sourceHolder, MediaStreamKind.Subtitle);
                    _source.Publish(new(newSourceHolder, "source"), versions);
                }

                if (changed.HasFlag(ChainChanges.SubtitleDecoder))
                {
                    _subtitleDecoder.Publish(decoder, versions);
                    decoder = null;
                }

                if (changed.HasFlag(ChainChanges.SubtitleRenderer))
                {
                    _subtitleRenderer.Publish(renderer, versions);
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

    private static OperationResult<PipelineComponent<T>?> ResolveComponent<T>(
        IEnumerable<IComponentCreator<T>> creators,
        string? runningSignature,
        string role) where T : class, IDisposable
    {
        OperationResultCreator<PipelineComponent<T>?> result = new();
        string error = $"No {role} could be offered.";

        foreach (IComponentCreator<T> creator in creators)
        {
            if (creator.Signature == runningSignature)
            {
                return result.Success(null);
            }

            OperationResult created = creator.Create(out T? made);
            if (created.IsSuccessful && made is not null)
            {
                return result.Success(new(made, creator.Signature));
            }

            error = created.IsSuccessful ? $"{creator.Signature} made no {role}." : created.Message;
        }

        return result.Failure(error);
    }

    private MediaSourceHolder CreateSourceHolderFromSelection()
    {
        MediaSourceHolder selection = _selection;
        VideoStreamInfo? videoStream = selection.VideoStream;
        AudioStreamInfo? audioStream = selection.AudioStream;
        SubtitleStreamInfo? subtitleStream = selection.SubtitleStream;
        MediaSource?[] sources = [selection.VideoSource, selection.AudioSource, selection.SubtitleSource];
        return new([.. sources.OfType<MediaSource>().Distinct()], videoStream, audioStream, subtitleStream);
    }

    private MediaSourceHolder CreateSourceHolderFromChange(MediaSourceHolder desired, MediaStreamKind kind)
    {
        MediaSourceHolder? previous = _source.Latest?.Component;

        VideoStreamInfo? videoStream = kind == MediaStreamKind.Video ? desired.VideoStream : previous?.VideoStream;
        AudioStreamInfo? audioStream = kind == MediaStreamKind.Audio ? desired.AudioStream : previous?.AudioStream;
        SubtitleStreamInfo? subtitleStream = kind == MediaStreamKind.Subtitle ? desired.SubtitleStream : previous?.SubtitleStream;

        MediaSource? videoSource = kind == MediaStreamKind.Video ? desired.VideoSource : previous?.VideoSource;
        MediaSource? audioSource = kind == MediaStreamKind.Audio ? desired.AudioSource : previous?.AudioSource;
        MediaSource? subtitleSource = kind == MediaStreamKind.Subtitle ? desired.SubtitleSource : previous?.SubtitleSource;

        MediaSource?[] sources = [videoSource, audioSource, subtitleSource];
        return new([.. sources.OfType<MediaSource>().Distinct()], videoStream, audioStream, subtitleStream);
    }

    private PipelineVersion BumpVersions(ChainChanges changed)
    {
        PipelineVersion current = CurrentVersions();
        long nextVersion = current.Next;
        return new()
        {
            Source = changed.HasFlag(ChainChanges.Source) ? nextVersion : current.Source,
            VideoDecoder = changed.HasFlag(ChainChanges.VideoDecoder) ? nextVersion : current.VideoDecoder,
            VideoRenderer = changed.HasFlag(ChainChanges.VideoRenderer) ? nextVersion : current.VideoRenderer,
            AudioDecoder = changed.HasFlag(ChainChanges.AudioDecoder) ? nextVersion : current.AudioDecoder,
            AudioRenderer = changed.HasFlag(ChainChanges.AudioRenderer) ? nextVersion : current.AudioRenderer,
            SubtitleDecoder = changed.HasFlag(ChainChanges.SubtitleDecoder) ? nextVersion : current.SubtitleDecoder,
            SubtitleRenderer = changed.HasFlag(ChainChanges.SubtitleRenderer) ? nextVersion : current.SubtitleRenderer,
        };
    }

    private PipelineVersion CurrentVersions()
    {
        PipelineVersion versions = default;
        versions = PipelineVersion.Max(versions, _source.HighestVersions);
        versions = PipelineVersion.Max(versions, _videoDecoder.HighestVersions);
        versions = PipelineVersion.Max(versions, _audioDecoder.HighestVersions);
        versions = PipelineVersion.Max(versions, _videoRenderer.HighestVersions);
        versions = PipelineVersion.Max(versions, _audioRenderer.HighestVersions);
        versions = PipelineVersion.Max(versions, _subtitleDecoder.HighestVersions);
        versions = PipelineVersion.Max(versions, _subtitleRenderer.HighestVersions);
        return versions;
    }

    #endregion

    #region Reader

    private void TryWakeReader()
    {
        MediaSourceHolder? sourceHolder = _source.Latest?.Component;
        if (sourceHolder is not null && !IsBuffered(sourceHolder, ReadAheadWake))
        {
            _readerNode.Wake();
        }
    }

    private async Task ReaderCore()
    {
        static TimeSpan SpanOf(MediaPacket? packet, TimeSpan previous, double frameRate)
        {
            if (packet is not null && packet.Duration > TimeSpan.Zero)
            {
                return packet.Duration;
            }

            if (previous > TimeSpan.Zero)
            {
                return previous;
            }

            if (!double.IsFinite(frameRate) || frameRate <= 0 || frameRate > 1000)
            {
                return TimeSpan.FromMilliseconds(FallbackFrameMilliseconds);
            }

            return TimeSpan.FromSeconds(1 / frameRate);
        }

        ComponentSlot<MediaSourceHolder> sourceSlot = _source;
        bool pendingEpochChange = false;
        bool buffering = false;
        long workingEpoch = -1;
        long endingEpoch = -1;
        TimeSpan lastVideoPacketSpan = TimeSpan.Zero;
        TimeSpan lastAudioPacketSpan = TimeSpan.Zero;
        TimeSpan lastSubtitlePacketSpan = TimeSpan.Zero;
        Dictionary<MediaSource, SourceTail> tails = [];

        while (true)
        {
            long stamp = _readerNode.Stamp;
            if (_stopRequested)
            {
                break;
            }

            if (!CheckState(out _, MediaEngineState.Playing, MediaEngineState.Paused))
            {
                await _readerNode.WaitAsync(stamp);
                continue;
            }

            // Install source
            if (sourceSlot.Update(out MediaSourceHolder? oldSourceHolder))
            {
                oldSourceHolder?.Dispose();
                pendingEpochChange = true;
                tails.Clear();
            }

            MediaSourceHolder? sourceHolder = sourceSlot.CurrentComponent;
            if (sourceHolder is null)
            {
                await _readerNode.WaitAsync(stamp);
                continue;
            }

            IReadOnlyList<MediaSource> sources = sourceHolder.Sources;
            if (sources.Count == 0)
            {
                await _readerNode.WaitAsync(stamp);
                continue;
            }

            // Bump epoch
            EpochGate epochGate = _epochGate.Read();
            long epoch = epochGate.Epoch;
            if (epoch != workingEpoch)
            {
                workingEpoch = epoch;
                pendingEpochChange = false;
                tails.Clear();

                Volatile.Write(ref _readerTailEpoch, epoch);
                Interlocked.Exchange(ref _readerTailVideoTicks, -1);
                Interlocked.Exchange(ref _readerTailAudioTicks, -1);
                Interlocked.Exchange(ref _readerTailSubtitleTicks, -1);

                OperationResult sought = SeekSources(sourceHolder, epochGate.SeekPosition);
                if (sought.IsSuccessful)
                {
                    endingEpoch = -1;
                }
                else
                {
                    PostWarning(sought.Message);
                }

                continue;
            }

            if (pendingEpochChange || endingEpoch == epoch)
            {
                await _readerNode.WaitAsync(stamp);
                continue;
            }

            // Buffering
            if (!buffering)
            {
                if (IsBuffered(sourceHolder, ReadAheadWake))
                {
                    await _readerNode.WaitAsync(stamp);
                    continue;
                }

                buffering = true;
            }
            else if (IsBuffered(sourceHolder, ReadAheadMin))
            {
                buffering = false;
                continue;
            }

            MediaSource? chosenSource = null;
            SourceTail? chosenTail = null;
            foreach (MediaSource source in sources)
            {
                if (!tails.TryGetValue(source, out SourceTail? tail))
                {
                    tail = new SourceTail { Tail = epochGate.SeekPosition };
                    tails[source] = tail;
                }

                if (tail.Ended)
                {
                    continue;
                }

                if (chosenTail is null || tail.Tail < chosenTail.Tail)
                {
                    chosenSource = source;
                    chosenTail = tail;
                }
            }

            PipelineVersion sourceVersions = sourceSlot.CurrentVersions;
            if (chosenSource is null || chosenTail is null)
            {
                endingEpoch = epoch;

                _videoPacketQueue.Enqueue(new PacketEntry(null, sourceVersions, epoch, TimeSpan.Zero));
                _audioPacketQueue.Enqueue(new PacketEntry(null, sourceVersions, epoch, TimeSpan.Zero));
                _subtitlePacketQueue.Enqueue(new PacketEntry(null, sourceVersions, epoch, TimeSpan.Zero));
                TryWakeVideoDecode();
                TryWakeAudioDecode();
                TryWakeSubtitleDecode();

                long endedEpoch = epoch;
                _ = Submit(() => NotifySourceEnded(endedEpoch));
                continue;
            }

            MediaPacket? packet = null;
            OperationResult read;
            try
            {
                read = chosenSource.Source.TryReadPacket(out packet);
            }
            catch (Exception ex)
            {
                read = OperationResult.Failure(ex.Message);
            }

            if (!read.IsSuccessful)
            {
                PostFailure(read);
                break;
            }

            if (packet is null)
            {
                chosenTail.Ended = true;
                continue;
            }

            if (packet.Timestamp > chosenTail.Tail)
            {
                chosenTail.Tail = packet.Timestamp;
            }

            // Route packet
            bool handedOn = false;
            AudioStreamInfo? audioStreamInfo = sourceHolder.AudioStream;
            VideoStreamInfo? videoStreamInfo = sourceHolder.VideoStream;
            SubtitleStreamInfo? subtitleStreamInfo = sourceHolder.SubtitleStream;

            if (videoStreamInfo is not null && chosenSource == sourceHolder.VideoSource && packet.StreamIndex == videoStreamInfo.Index)
            {
                AdvanceReaderTail(epoch, packet.Timestamp, ref _readerTailVideoTicks);
                TimeSpan span = SpanOf(packet, lastVideoPacketSpan, videoStreamInfo.FrameRate);
                lastVideoPacketSpan = span;
                _videoPacketQueue.Enqueue(new PacketEntry(packet, sourceVersions, epoch, span));
                handedOn = true;
                TryWakeVideoDecode();
            }
            else if (audioStreamInfo is not null && chosenSource == sourceHolder.AudioSource && packet.StreamIndex == audioStreamInfo.Index)
            {
                AdvanceReaderTail(epoch, packet.Timestamp, ref _readerTailAudioTicks);
                TimeSpan span = SpanOf(packet, lastAudioPacketSpan, 0);
                lastAudioPacketSpan = span;
                _audioPacketQueue.Enqueue(new PacketEntry(packet, sourceVersions, epoch, span));
                handedOn = true;
                TryWakeAudioDecode();
            }
            else if (subtitleStreamInfo is not null && chosenSource == sourceHolder.SubtitleSource && packet.StreamIndex == subtitleStreamInfo.Index)
            {
                AdvanceReaderTail(epoch, packet.Timestamp, ref _readerTailSubtitleTicks);
                TimeSpan span = SpanOf(packet, lastSubtitlePacketSpan, 0);
                lastSubtitlePacketSpan = span;
                _subtitlePacketQueue.Enqueue(new PacketEntry(packet, sourceVersions, epoch, span));
                handedOn = true;
                TryWakeSubtitleDecode();
            }

            if (!handedOn)
            {
                packet.Dispose();
            }

            await Task.Yield();
        }
    }

    private static OperationResult SeekSources(MediaSourceHolder sourceHolder, TimeSpan position)
    {
        IReadOnlyList<MediaSource> sources = sourceHolder.Sources;
        foreach (MediaSource source in sources)
        {
            if (!source.Source.CanSeek)
            {
                return OperationResult.Failure($"A source cannot seek, so the seek to {position:g} was skipped: {source.Locator}");
            }
        }

        var firstFailure = OperationResult.Success();

        foreach (MediaSource entry in sources)
        {
            OperationResult sought;
            try
            {
                sought = entry.Source.Seek(position, ChooseSeekLanding(sourceHolder, entry, position));
            }
            catch (Exception ex)
            {
                sought = OperationResult.Failure(ex);
            }

            if (!sought.IsSuccessful)
            {
                firstFailure = sought;
            }
        }

        return firstFailure;
    }

    private static int ChooseSeekLanding(MediaSourceHolder sourceHolder, MediaSource source, TimeSpan position)
    {
        VideoStreamInfo? video = sourceHolder.VideoSource == source ? sourceHolder.VideoStream : null;
        AudioStreamInfo? audio = sourceHolder.AudioSource == source ? sourceHolder.AudioStream : null;

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

    private bool IsBuffered(MediaSourceHolder holder, TimeSpan minimumReadAhead)
    {
        TimeSpan ReadAhead(ref long ticks, TimeSpan position)
        {
            long tail = Interlocked.Read(ref ticks);
            if (tail < 0)
            {
                return TimeSpan.MinValue;
            }

            long ahead = tail - position.Ticks;
            return ahead > 0 ? TimeSpan.FromTicks(ahead) : TimeSpan.Zero;
        }

        TimeSpan position = _clock.Position;

        if (holder.VideoStream is not null && _videoDecoder.HasComponent && ReadAhead(ref _readerTailVideoTicks, position) >= ReadAheadMax)
        {
            return true;
        }

        if (holder.AudioStream is not null && _audioDecoder.HasComponent && ReadAhead(ref _readerTailAudioTicks, position) >= ReadAheadMax)
        {
            return true;
        }

        if (holder.SubtitleStream is not null && _subtitleDecoder.HasComponent && ReadAhead(ref _readerTailSubtitleTicks, position) >= ReadAheadMax)
        {
            return true;
        }

        if (holder.VideoStream is not null && _videoDecoder.HasComponent && ReadAhead(ref _readerTailVideoTicks, position) < minimumReadAhead)
        {
            return false;
        }

        if (holder.AudioStream is not null && _audioDecoder.HasComponent && ReadAhead(ref _readerTailAudioTicks, position) < minimumReadAhead)
        {
            return false;
        }

        if (holder.SubtitleStream is not null && _subtitleDecoder.HasComponent && ReadAhead(ref _readerTailSubtitleTicks, position) < minimumReadAhead)
        {
            return false;
        }

        return true;
    }

    private void AdvanceReaderTail(long epoch, TimeSpan timestamp, ref long oldTicks)
    {
        if (Volatile.Read(ref _readerTailEpoch) != epoch)
        {
            return;
        }

        long ticks = timestamp.Ticks;

        if (ticks > Interlocked.Read(ref oldTicks))
        {
            Interlocked.Exchange(ref oldTicks, ticks);
        }
    }

    private bool IsStreamSpent(long epoch, TimeSpan position, ref long ticks)
    {
        if (Volatile.Read(ref _readerTailEpoch) != epoch)
        {
            return false;
        }

        long ticksLocal = Interlocked.Read(ref ticks);
        if (ticksLocal < 0 || ticksLocal >= position.Ticks)
        {
            return false;
        }

        long maxTicks = Math.Max(Interlocked.Read(ref _readerTailVideoTicks), Interlocked.Read(ref _readerTailAudioTicks));
        return maxTicks >= position.Ticks;
    }

    private sealed class SourceTail
    {
        public TimeSpan Tail { get; set; }

        public bool Ended { get; set; }
    }

    #endregion

    #region Video decode

    private void TryWakeVideoDecode()
    {
        if (!HasRoomForVideoFrame())
        {
            return;
        }

        if (!_videoPacketQueue.IsEmpty)
        {
            _videoDecodeNode.Wake();
        }
    }

    private void VideoDecodeCore()
    {
        PacketQueue queue = _videoPacketQueue;
        ComponentSlot<IVideoDecoder> videoDecoderSlot = _videoDecoder;
        bool awaitingKeyFrame = false;
        long workingEpoch = -1;

        while (true)
        {
            long stamp = _videoDecodeNode.Stamp;
            if (_stopRequested)
            {
                return;
            }

            // Install video decoder
            if (videoDecoderSlot.Update(out IVideoDecoder? oldVideoDecoder))
            {
                oldVideoDecoder?.Dispose();
                awaitingKeyFrame = true;
            }

            IVideoDecoder? videoDecoder = videoDecoderSlot.CurrentComponent;

            // Bump epoch
            long epoch = _epochGate.Read().Epoch;
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;

                try
                {
                    videoDecoder?.Flush();
                }
                catch (Exception exception)
                {
                    PostFailure(exception.Message, exception);
                    return;
                }

                continue;
            }

            // Dequeue
            if (!HasRoomForVideoFrame())
            {
                _videoDecodeNode.Wait(stamp);
                continue;
            }

            if (!queue.TryPeek(out PacketEntry entry))
            {
                _videoDecodeNode.Wait(stamp);
                continue;
            }

            PipelineVersion videoDecoderVersions = videoDecoderSlot.CurrentVersions;
            VersionVerdict verdict = PipelineVersion.CompareVideoDecoder(entry.Versions, videoDecoderVersions);
            if (verdict == VersionVerdict.Block)
            {
                _videoDecodeNode.Wait(stamp);
                continue;
            }

            if (!queue.TryDequeue(out _))
            {
                _videoDecodeNode.Wait(stamp);
                continue;
            }

            TryWakeReader();

            // Decode
            MediaPacket? packet = entry.Packet;
            long packetEpoch = entry.Epoch;
            using (packet)
            {
                if (verdict == VersionVerdict.Drop || epoch != packetEpoch)
                {
                    continue;
                }

                var videoFrameVersions = PipelineVersion.Max(entry.Versions, videoDecoderVersions);
                if (packet is null)
                {
                    if (videoDecoder is not null)
                    {
                        OperationResult drained = DrainVideoDecoder(videoDecoder, videoFrameVersions, packetEpoch);
                        if (!drained.IsSuccessful)
                        {
                            PostWarning(drained.Message);
                        }
                    }

                    _frameQueue.Enqueue(new FrameEntry(null, videoFrameVersions, epoch));
                    TryWakeVideoRender();
                    _videoDecodeNode.Wait(stamp);
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

                OperationResult decoded = DecodeVideo(videoDecoder, packet, out IReadOnlyList<RawVideoFrame>? frames);
                if (!decoded.IsSuccessful)
                {
                    PostFailure(decoded);
                    return;
                }

                if (frames is null)
                {
                    PostFailure("The video decoder returned no frames.");
                    return;
                }

                QueueFrames(frames, videoFrameVersions, epoch);
            }
        }
    }

    private OperationResult DecodeVideo(IVideoDecoder videoDecoder, MediaPacket packet, out IReadOnlyList<RawVideoFrame>? frames)
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

    private OperationResult DrainVideoDecoder(IVideoDecoder decoder, PipelineVersion versions, long epoch)
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

    private void QueueFrames(IReadOnlyList<RawVideoFrame> frames, PipelineVersion versions, long epoch)
    {
        bool fastExit = false;

        foreach (RawVideoFrame frame in frames)
        {
            if (fastExit || _epochGate.Read().Epoch != epoch)
            {
                fastExit = true;
                frame.Dispose();
                continue;
            }

            _frameQueue.Enqueue(new FrameEntry(frame, versions, epoch));
        }

        if (!fastExit)
        {
            TryWakeVideoRender();
        }
    }

    private bool HasRoomForVideoFrame()
    {
        return _frameQueue.Count < FrameQueueCapacity;
    }

    #endregion

    #region Audio decode

    private void TryWakeAudioDecode()
    {
        if (!_audioPacketQueue.IsEmpty)
        {
            _audioDecodeNode.Wake();
        }
    }

    private async Task AudioDecodeCore()
    {
        PacketQueue queue = _audioPacketQueue;
        ComponentSlot<IAudioDecoder> audioDecoderSlot = _audioDecoder;
        long workingEpoch = -1;

        while (true)
        {
            long stamp = _audioDecodeNode.Stamp;
            if (_stopRequested)
            {
                return;
            }

            // Install audio decoder
            if (audioDecoderSlot.Update(out IAudioDecoder? oldAudioDecoder))
            {
                oldAudioDecoder?.Dispose();
            }

            // Bump epoch
            long epoch = _epochGate.Read().Epoch;
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;

                try
                {
                    audioDecoderSlot.CurrentComponent?.Flush();
                }
                catch (Exception ex)
                {
                    PostWarning($"The audio decoder could not be flushed: {ex.Message}");
                }

                continue;
            }

            if (!queue.TryPeek(out PacketEntry entry))
            {
                await _audioDecodeNode.WaitAsync(stamp);
                continue;
            }

            PipelineVersion audioDecoderVersions = audioDecoderSlot.CurrentVersions;
            VersionVerdict verdict = PipelineVersion.CompareAudioDecoder(entry.Versions, audioDecoderVersions);
            if (verdict == VersionVerdict.Block)
            {
                await _audioDecodeNode.WaitAsync(stamp);
                continue;
            }

            if (!queue.TryDequeue(out _))
            {
                await _audioDecodeNode.WaitAsync(stamp);
                continue;
            }

            TryWakeReader();

            MediaPacket? packet = entry.Packet;
            long packetEpoch = entry.Epoch;
            using (packet)
            {
                if (verdict == VersionVerdict.Drop || epoch != packetEpoch)
                {
                    continue;
                }

                var audioBufferVersions = PipelineVersion.Max(entry.Versions, audioDecoderVersions);
                IAudioDecoder? audioDecoder = audioDecoderSlot.CurrentComponent;
                if (packet is null)
                {
                    OperationResult drained = DrainAudioDecoder(audioDecoder, audioBufferVersions, packetEpoch);
                    if (!drained.IsSuccessful)
                    {
                        PostWarning(drained.Message);
                    }

                    _audioQueue.Enqueue(new AudioEntry(null, audioBufferVersions, epoch));
                    TryWakeAudioRender();
                    continue;
                }

                if (audioDecoder is null)
                {
                    continue;
                }

                OperationResult routed = DecodeAudio(audioDecoder, packet, audioBufferVersions, epoch);
                if (!routed.IsSuccessful)
                {
                    PostFailure(routed);
                    return;
                }
            }

            await Task.Yield();
        }
    }

    private OperationResult DecodeAudio(IAudioDecoder audioDecoder, MediaPacket packet, PipelineVersion versions, long epoch)
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
            _audioQueue.Enqueue(new AudioEntry(buffer, versions, epoch));
        }

        if (buffers.Count > 0)
        {
            TryWakeAudioRender();
        }

        return OperationResult.Success();
    }

    private OperationResult DrainAudioDecoder(IAudioDecoder? decoder, PipelineVersion versions, long epoch)
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
            _audioQueue.Enqueue(new AudioEntry(buffer, versions, epoch));
            handedOver = true;
        }

        if (handedOver)
        {
            TryWakeAudioRender();
        }

        return OperationResult.Success();
    }

    #endregion

    #region Subtitle decode

    private void TryWakeSubtitleDecode()
    {
        if (!_subtitlePacketQueue.IsEmpty)
        {
            _subtitleDecodeNode.Wake();
        }
    }

    private async Task SubtitleDecodeCore()
    {
        PacketQueue queue = _subtitlePacketQueue;
        ComponentSlot<ISubtitleDecoder> subtitleDecoderSlot = _subtitleDecoder;
        long workingEpoch = -1;

        while (true)
        {
            long stamp = _subtitleDecodeNode.Stamp;
            if (_stopRequested)
            {
                return;
            }

            // Install subtitle decoder
            if (subtitleDecoderSlot.Update(out ISubtitleDecoder? oldSubtitleDecoder))
            {
                oldSubtitleDecoder?.Dispose();
                _cueWindow.Reset();
            }

            // Bump epoch
            EpochGate epochGate = _epochGate.Read();
            long epoch = epochGate.Epoch;
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;

                try
                {
                    subtitleDecoderSlot.CurrentComponent?.Flush();
                }
                catch (Exception ex)
                {
                    PostWarning($"The subtitle decoder could not be flushed: {ex.Message}");
                }

                _cueWindow.SeekTo(epoch, epochGate.SeekPosition);
                continue;
            }

            if (!queue.TryPeek(out PacketEntry entry))
            {
                await _subtitleDecodeNode.WaitAsync(stamp);
                continue;
            }

            PipelineVersion subtitleDecoderVersions = subtitleDecoderSlot.CurrentVersions;
            VersionVerdict verdict = PipelineVersion.CompareSubtitleDecoder(entry.Versions, subtitleDecoderVersions);
            if (verdict == VersionVerdict.Block)
            {
                await _subtitleDecodeNode.WaitAsync(stamp);
                continue;
            }

            if (!queue.TryDequeue(out _))
            {
                await _subtitleDecodeNode.WaitAsync(stamp);
                continue;
            }

            TryWakeReader();

            MediaPacket? packet = entry.Packet;
            long packetEpoch = entry.Epoch;
            using (packet)
            {
                if (verdict == VersionVerdict.Drop || epoch != packetEpoch)
                {
                    continue;
                }

                ISubtitleDecoder? subtitleDecoder = subtitleDecoderSlot.CurrentComponent;
                if (packet is null || subtitleDecoder is null)
                {
                    continue;
                }

                OperationResult routed = DecodeSubtitle(epoch, subtitleDecoder, packet);
                if (!routed.IsSuccessful)
                {
                    PostFailure(routed);
                    return;
                }
            }

            await Task.Yield();
        }
    }

    private OperationResult DecodeSubtitle(long epoch, ISubtitleDecoder subtitleDecoder, MediaPacket packet)
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

        _cueWindow.Add(epoch, cues, _clock.Position, discardStale: !_subtitleRenderer.HasComponent);
        if (cues.Count > 0)
        {
            _subtitleRenderNode.Wake();
        }

        return OperationResult.Success();
    }

    #endregion

    #region Video render

    private void TryWakeVideoRender()
    {
        if (!_frameQueue.IsEmpty)
        {
            _videoRenderNode.Wake();
        }
    }

    private void VideoRenderCore()
    {
        ConcurrentQueue<FrameEntry> queue = _frameQueue;
        ComponentSlot<IVideoRenderer> videoRendererSlot = _videoRenderer;
        bool deviceAvailable = false;
        long workingEpoch = -1;
        long endingEpoch = -1;
        TimeSpan? startTime = null;
        TimeSpan? firstFrameTime = null;

        void DequeueAndDispose()
        {
            if (!queue.TryDequeue(out FrameEntry entry))
            {
                return;
            }

            entry.Frame?.Dispose();
            TryWakeVideoDecode();
        }

        while (true)
        {
            long stamp = _videoRenderNode.Stamp;
            if (_stopRequested)
            {
                return;
            }

            // Install video renderer
            if (videoRendererSlot.Update(out IVideoRenderer? oldVideoRenderer))
            {
                if (oldVideoRenderer is not null)
                {
                    if (videoRendererSlot.CurrentComponent is null)
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
                            PostWarning(cleared.Message);
                        }

                        Interlocked.Exchange(ref _pictureTicks, -1);
                    }

                    oldVideoRenderer.Dispose();
                }

                deviceAvailable = videoRendererSlot.CurrentComponent is not null;
                continue;
            }

            IVideoRenderer? videoRenderer = videoRendererSlot.CurrentComponent;

            // Bump epoch
            EpochGate epochGate = _epochGate.Read();
            long epoch = epochGate.Epoch;
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;
                startTime = epochGate.SeekMode == SeekMode.Precise ? epochGate.SeekPosition : null;
                firstFrameTime = null;
                Interlocked.Exchange(ref _pictureTicks, -1);
            }

            // Refresh surface
            bool playing = CheckState(out MediaEngineState state, MediaEngineState.Playing);
            VideoTransform transform = VideoTransform;

            VideoOrientation containerTransform = SelectedVideoStream?.Orientation ?? VideoOrientation.Identity;
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
                    PostWarning(refreshed.Message);
                    deviceAvailable = false;
                }
            }

            // Pause the clock on pause or starvation
            FrameEntry? entry = queue.TryPeek(out FrameEntry head) ? head : null;
            TimeSpan position = _clock.Position;
            bool offeringTheStill = state == MediaEngineState.Paused && !firstFrameTime.HasValue && endingEpoch != epoch;

            bool starved = _videoDecoder.HasComponent && endingEpoch != epoch && entry is null;
            starved = starved && !IsStreamSpent(epoch, position, ref _readerTailVideoTicks);
            starved = starved && (!firstFrameTime.HasValue || IsVideoStarved(position));

            lock (_clockLock)
            {
                _videoStarved = starved;
                StartOrPauseClock(epoch, state);
            }

            // Idle on pause or starvation
            if (!playing && !offeringTheStill)
            {
                _videoRenderNode.Wait(stamp);
                continue;
            }

            if (entry is null)
            {
                _videoRenderNode.Wait(stamp, playing ? VideoIdleTimeoutMilliseconds : Timeout.Infinite);
                continue;
            }

            // Probe the entry
            if (entry.Value.Epoch != epoch)
            {
                DequeueAndDispose();
                continue;
            }

            VersionVerdict verdict = PipelineVersion.CompareVideoRenderer(entry.Value.Versions, videoRendererSlot.CurrentVersions);
            if (verdict == VersionVerdict.Block)
            {
                _videoRenderNode.Wait(stamp);
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
                    Submit(() => NotifyVideoRenderEnded(epochCopy));
                }

                DequeueAndDispose();
                continue;
            }

            if (startTime.HasValue && frame.Timestamp < startTime)
            {
                DequeueAndDispose();
                continue;
            }

            // Ready to present
            FrameSyncAction syncAction = Synchronizer.Evaluate(frame.Timestamp, position);

            if (playing)
            {
                TimeSpan anchor = firstFrameTime ?? frame.Timestamp;

                lock (_clockLock)
                {
                    if (AnchorClock(epoch, anchor))
                    {
                        position = _clock.Position;
                        syncAction = Synchronizer.Evaluate(frame.Timestamp, position);
                    }

                    StartOrPauseClock(epoch, state);
                }
            }

            if (playing && syncAction == FrameSyncAction.Wait)
            {
                int remaining = (int)Math.Clamp((frame.Timestamp - position).TotalMilliseconds, 1, VideoIdleTimeoutMilliseconds);
                _videoRenderNode.Wait(stamp, remaining);
                continue;
            }

            // Present
            if (!queue.TryDequeue(out _))
            {
                continue;
            }

            TryWakeVideoDecode();

            using (frame)
            {
                if (playing
                    && syncAction == FrameSyncAction.Drop
                    && queue.TryPeek(out FrameEntry closer)
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

                    OperationResult shown = RenderVideoFrame(videoRenderer, frame, transform);
                    if (!shown.IsSuccessful)
                    {
                        PostWarning(shown.Message);
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

    private OperationResult RenderVideoFrame(IVideoRenderer renderer, RawVideoFrame frame, VideoTransform transform)
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

    private bool IsVideoStarved(TimeSpan position)
    {
        long presentedTicks = Interlocked.Read(ref _pictureTicks);
        if (presentedTicks < 0)
        {
            return false;
        }

        TimeSpan behind = position - TimeSpan.FromTicks(presentedTicks);
        return _videoStarved
            ? behind > TimeSpan.FromMilliseconds(VideoStarvationResumeMilliseconds)
            : behind > TimeSpan.FromMilliseconds(VideoStarvationMilliseconds);
    }

    #endregion

    #region Audio render

    private void TryWakeAudioRender()
    {
        if (!_audioQueue.IsEmpty)
        {
            _audioRenderNode.Wake();
        }
    }

    private void AudioRenderCore()
    {
        AudioQueue queue = _audioQueue;
        ComponentSlot<IAudioRenderer> audioRendererSlot = _audioRenderer;
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

            audioRendererSlot.Publish(null, audioRendererSlot.CurrentVersions);
            return true;
        }

        while (true)
        {
            long stamp = _audioRenderNode.Stamp;
            if (_stopRequested)
            {
                break;
            }

            // Install audio renderer
            if (audioRendererSlot.Update(out IAudioRenderer? oldAudioRenderer))
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

            IAudioRenderer? audioRenderer = audioRendererSlot.CurrentComponent;

            if (filters is null || filters.Version != AudioFilters.Version)
            {
                filters = AudioFilters.Snapshot();

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
                        PostWarning($"The audio device could not be opened: {opened.Message}");
                        if (AbandonFailedDevice(audioRenderer))
                        {
                            continue;
                        }
                    }

                    clockOffset = null;
                }
            }

            // Bump epoch
            EpochGate epochGate = _epochGate.Read();
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

            // Pause the clock on pause or starvation
            bool playing = CheckState(out MediaEngineState state, MediaEngineState.Playing);
            lock (_clockLock)
            {
                if (epochChanged)
                {
                    _audioStarved = true;
                }

                StartOrPauseClock(epoch, state);

                if (deviceAvailable && audioRenderer is not null)
                {
                    IPlaybackClock deviceClock = audioRenderer.Clock;
                    bool clockRunning = _clock.IsRunning;
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
                            PostWarning(ex.Message);
                            AbandonFailedDevice(audioRenderer);
                            continue;
                        }
                    }
                }
            }

            // Idle on pause
            if (!playing)
            {
                _audioRenderNode.Wait(stamp);
                continue;
            }

            // Idle on leeway device
            TimeSpan position = _clock.Position;
            if (deviceAvailable && tailBufferPosition is TimeSpan consumed
                && consumed - position > TimeSpan.FromMilliseconds(AudioDeviceLeewayMilliseconds))
            {
                if (!reportedRunningAhead)
                {
                    reportedRunningAhead = true;
                    PostWarning("The audio device is not playing what it is given.");
                }

                lock (_clockLock)
                {
                    // A device that is running ahead of the clock is not starved.
                    _audioStarved = false;
                }

                _audioRenderNode.Wait(stamp, AudioIdleTimeoutMilliseconds);
                continue;
            }

            // Continue in-flight audio
            bool hasDeviceAudio = deviceAvailable && audioRenderer is not null && audioRenderer.QueuedSampleCount > 0;
            if (deviceAvailable && audioRenderer is not null)
            {
                IPlaybackClock deviceClock = audioRenderer.Clock;

                if (hasDeviceAudio && clockOffset is TimeSpan offset)
                {
                    SyncClock(_clock, deviceClock, offset);
                }

                if (inFlightSubmit is not null)
                {
                    if (!inFlightSubmit.MoveNext())
                    {
                        OperationResult submitted = inFlightSubmit.Result;
                        ReleaseInFlightAudio();

                        if (!submitted.IsSuccessful)
                        {
                            PostWarning(submitted.Message);
                            AbandonFailedDevice(audioRenderer);
                        }

                        continue;
                    }

                    _audioRenderNode.Wait(stamp, AudioIdleTimeoutMilliseconds);
                    continue;
                }
            }

            // Empty queue: starvation or ending
            if (queue.IsEmpty)
            {
                bool ending = endingEpoch == epoch;
                bool starved = deviceAvailable && !hasDeviceAudio && !ending && !IsStreamSpent(epoch, position, ref _readerTailAudioTicks);
                lock (_clockLock)
                {
                    _audioStarved = starved;
                    StartOrPauseClock(epoch, state);
                }

                if (ending && !hasDeviceAudio && playedOutEpoch != endingEpoch)
                {
                    playedOutEpoch = endingEpoch;

                    long epochCopy = endingEpoch;
                    Submit(() => NotifyAudioRenderEnded(epochCopy));
                }

                _audioRenderNode.Wait(stamp, AudioIdleTimeoutMilliseconds);
                continue;
            }

            // Idle on failing device if the clock is behind, since the device clock is no longer reliable
            if (!deviceAvailable && tailBufferPosition.HasValue && position < tailBufferPosition.Value)
            {
                _audioRenderNode.Wait(stamp, AudioIdleTimeoutMilliseconds);
                continue;
            }

            // Probe the entry
            if (!queue.TryPeek(out AudioEntry entry))
            {
                continue;
            }

            VersionVerdict verdict = PipelineVersion.CompareAudioRenderer(entry.Versions, audioRendererSlot.CurrentVersions);
            if (verdict == VersionVerdict.Block)
            {
                _audioRenderNode.Wait(stamp);
                continue;
            }

            if (!queue.TryDequeue(out _))
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
                PostFailure(filtersApplied.Message, filtersApplied.Exception);
                return;
            }

            buffer = filtersApplied.Result;

            // Ready to submit
            tailBufferPosition = buffer.Timestamp + buffer.Duration;

            lock (_clockLock)
            {
                _audioStarved = false;

                bool clockChanged = AnchorClock(epoch, buffer.Timestamp);
                if (deviceAvailable && audioRenderer is not null && (clockChanged || clockOffset is null))
                {
                    clockOffset = audioRenderer.Clock.Position - buffer.Timestamp;
                }

                StartOrPauseClock(epoch, state);
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

        // Cleanup
        ReleaseInFlightAudio();
    }

    private void SyncClock(IPlaybackClock clock, IPlaybackClock deviceClock, TimeSpan offset)
    {
        TimeSpan deviceTime;
        TimeSpan localTime;
        try
        {
            deviceTime = deviceClock.Position;
            localTime = clock.Position;
        }
        catch (Exception ex)
        {
            PlaybackLogger.E(TAG, ex);
            return;
        }

        TimeSpan expected = deviceTime - offset;
        if (Math.Abs((expected - localTime).TotalMilliseconds) <= ClockSyncToleranceMilliseconds)
        {
            return;
        }

        lock (_clockLock)
        {
            if (!clock.IsRunning || _anchoredEpoch != _epochGate.Read().Epoch)
            {
                return;
            }

            clock.SetPosition(expected);
            NotifyClockMoved();
        }
    }

    #endregion

    #region Subtitle render

    private void SubtitleRenderCore()
    {
        void ReportOnFailure(string message, OperationResult result)
        {
            if (!result.IsSuccessful)
            {
                PostWarning($"{message}: {result.Message}");
            }
        }

        ComponentSlot<ISubtitleRenderer> subtitleRendererSlot = _subtitleRenderer;
        bool drawnInvalidated = true;
        long workingEpoch = -1;
        long drawnSurfaceVersion = -1;
        TimeSpan drawnPosition = TimeSpan.MinValue;
        SubtitlePresentation drawnPresentation = default;
        SubtitleCueSnapshot drawnCues = SubtitleCueSnapshot.Empty;

        while (true)
        {
            long stamp = _subtitleRenderNode.Stamp;
            if (_stopRequested)
            {
                break;
            }

            // Install subtitle renderer
            if (subtitleRendererSlot.Update(out ISubtitleRenderer? oldRenderer))
            {
                if (oldRenderer is not null)
                {
                    if (subtitleRendererSlot.CurrentComponent is null)
                    {
                        ReportOnFailure("The subtitles could not be cleared",
                            SafeInvoke(() => oldRenderer.Show([], TimeSpan.Zero, SubtitlePresentation.Identity)));
                    }

                    oldRenderer.Dispose();
                }

                drawnInvalidated = true;
                continue;
            }

            ISubtitleRenderer? renderer = subtitleRendererSlot.CurrentComponent;
            if (renderer is null)
            {
                _subtitleRenderNode.Wait(stamp);
                continue;
            }

            if (!CheckState(out MediaEngineState state, MediaEngineState.Playing, MediaEngineState.Paused))
            {
                _subtitleRenderNode.Wait(stamp);
                continue;
            }

            // Bump epoch
            EpochGate epochGate = _epochGate.Read();
            long epoch = epochGate.Epoch;
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;
                drawnInvalidated = true;
            }

            // Show
            TimeSpan position = _clock.Position;
            SubtitleCueSnapshot cues = _cueWindow.GetVisibleCues(epoch, position);
            long surfaceVersion = Interlocked.Read(ref _subtitleSurfaceVersion);

            SubtitlePresentation presentation = SubtitlePresentation with
            {
                Frame = ResolveSubtitleFrame()
            };

            if (drawnInvalidated ||
                surfaceVersion != drawnSurfaceVersion ||
                presentation != drawnPresentation ||
                cues.Cues.Any(cue => cue.HasChanges(drawnPosition, position)) ||
                !SameCues(cues, drawnCues))
            {
                ReportOnFailure("The subtitles could not be drawn",
                    SafeInvoke(() => renderer.Show(cues.Cues, position, presentation)));
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

            // Idle
            int timeout = Timeout.Infinite;
            if (state == MediaEngineState.Playing)
            {
                TimeSpan? next = _cueWindow.TimeUntilNextChange(position);
                if (next.HasValue)
                {
                    timeout = next.Value <= TimeSpan.Zero
                        ? SubtitleChangeIntervalMilliseconds
                        : (int)Math.Clamp(Math.Ceiling(next.Value.TotalMilliseconds), 1, int.MaxValue);
                }
            }

            _subtitleRenderNode.Wait(stamp, timeout);
        }

        drawnCues.Dispose();
    }

    private SubtitleFrame ResolveSubtitleFrame()
    {
        IVideoRenderTarget? videoTarget = _videoTarget;
        VideoStreamInfo? stream = _source.Latest?.Component?.VideoStream;
        if (videoTarget is null || stream is null || stream.Width <= 0 || stream.Height <= 0)
        {
            return SubtitleFrame.Full;
        }

        int surfaceWidth = videoTarget.SurfaceWidth;
        int surfaceHeight = videoTarget.SurfaceHeight;
        if (surfaceWidth <= 0 || surfaceHeight <= 0)
        {
            return SubtitleFrame.Full;
        }

        // The placement maps the picture's own square onto the surface, so its four corners say where the picture ends:
        // the bounding box of that is the frame, whatever turning, zooming or mirroring was asked for.
        Matrix3x2 placement = _videoTransform.Read().GetPlacement(stream.Width, stream.Height, surfaceWidth, surfaceHeight);
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

    #endregion

    #region Events

    private void VideoTarget_SurfaceSizeChanged()
    {
        _videoRenderNode.Wake();
        _subtitleRenderNode.Wake();
    }

    private void SubtitleTarget_SurfaceSizeChanged()
    {
        Interlocked.Increment(ref _subtitleSurfaceVersion);
        _subtitleRenderNode.Wake();
    }

    #endregion

    #region Communication

    private bool CheckState(out MediaEngineState state, params MediaEngineState[] validStates)
    {
        state = _state;
        if (validStates.Contains(state))
        {
            return true;
        }

        return false;
    }

    private void StartOrPauseClock(long epoch, MediaEngineState state)
    {
        bool start = state == MediaEngineState.Playing && !_videoStarved && !_audioStarved;
        if (start == _clock.IsRunning)
        {
            return;
        }

        if (start && _anchoredEpoch != epoch)
        {
            return;
        }

        if (start)
        {
            _clock.Start();
        }
        else
        {
            _clock.Pause();
        }
    }

    private bool AnchorClock(long iterationEpoch, TimeSpan mediaTimestamp)
    {
        if (_anchoredEpoch == iterationEpoch || iterationEpoch != _epochGate.Read().Epoch)
        {
            return false;
        }

        _anchoredEpoch = iterationEpoch;
        _clock.SetPosition(mediaTimestamp);
        NotifyClockMoved();
        return true;
    }

    private void NotifyClockMoved()
    {
        // Subtitle might be changed
        _subtitleRenderNode.Wake();
    }

    private void WakeAll()
    {
        _readerNode.Wake();
        _videoDecodeNode.Wake();
        _audioDecodeNode.Wake();
        _subtitleDecodeNode.Wake();
        _videoRenderNode.Wake();
        _audioRenderNode.Wake();
        _subtitleRenderNode.Wake();
    }

    private void PostWarning(string warning)
    {
        Submit(() => RaiseWarning(warning));
    }

    private void PostFailure(string message, Exception? exception = null)
    {
        Submit(() => Fail(message, exception));
    }

    private void PostFailure(OperationResult result)
    {
        PostFailure(result.Message, result.Exception);
    }

    private Task Submit(Action work)
    {
        return _playbackQueue.Submit(work);
    }

    private Task<T> Submit<T>(Func<T> work)
    {
        return _playbackQueue.Submit(work);
    }

    private Task<OperationResult> SubmitAndLog(string action, Func<OperationResult> func)
    {
        return Submit(() =>
        {
            OperationResult result = func();
            if (!result.IsSuccessful)
            {
                PlaybackLogger.E(TAG, $"{action}: {result.Message}", result.Exception);
            }

            return result;
        });
    }

    private Task<OperationResult<T>> SubmitAndLog<T>(string action, Func<OperationResult<T>> func)
    {
        return Submit(() =>
        {
            OperationResult<T> result = func();
            if (!result.IsSuccessful)
            {
                PlaybackLogger.E(TAG, $"{action}: {result.Message}", result.Exception);
            }

            return result;
        });
    }

    #endregion

    #region Utilities

    private static OperationResult SafeInvoke(Func<OperationResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            return OperationResult.Failure(ex);
        }
    }

    #endregion
}
