// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Concurrent;
using System.Diagnostics;

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
