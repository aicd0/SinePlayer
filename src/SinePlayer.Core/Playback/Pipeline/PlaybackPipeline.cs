// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Diagnostics;

using SinePlayer.Core.Common.ErrorHandling;
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
    private const int AudioDeviceLeewayMilliseconds = 3000;

    private static readonly TimeSpan ReadAheadMin = TimeSpan.FromMilliseconds(ReadAheadMinMilliseconds);
    private static readonly TimeSpan ReadAheadMax = TimeSpan.FromMilliseconds(ReadAheadMaxMilliseconds);
    private static readonly TimeSpan ReadAheadWake = TimeSpan.FromMilliseconds(ReadAheadWakeMilliseconds);

    #endregion

    #region Variables

    private readonly PipelineEvents _events;
    private readonly PipelineTimeline _timeline;
    private readonly SubtitleChain _subtitle;
    private readonly VideoChain _video;

    private volatile MediaSourceHolder _selection = new([], null, null, null);

    private long _subtitleSurfaceVersion;
    private readonly ComponentSlot<MediaSourceHolder> _source = new();
    private readonly ComponentSlot<IAudioDecoder> _audioDecoder = new();
    private readonly ComponentSlot<IAudioRenderer> _audioRenderer = new();

    private readonly PacketQueue _audioPacketQueue = new();
    private readonly AudioQueue _audioQueue = new();

    private Thread? _readerThread;
    private Thread? _videoDecodeThread;
    private Thread? _videoRenderThread;
    private Thread? _audioRenderThread;
    private Thread? _subtitleRenderThread;

    private readonly IPipelineWakeTarget[] _wakeTargets;
    private readonly PipelineNodeAsync _readerNode = new();
    private readonly PipelineNodeAsync _audioDecodeNode = new();
    private readonly PipelineNode _audioRenderNode = new();

    private bool _disposed;
    private bool _threadsStarted;
    private volatile bool _stopRequested;

    private long _videoRenderEndedEpoch = -1;
    private long _audioRenderEndedEpoch = -1;

    private long _readerTailEpoch = -1;
    private long _readerTailVideoTicks = -1;
    private long _readerTailAudioTicks = -1;
    private long _readerTailSubtitleTicks = -1;

    #endregion

    #region Construction

    public PlaybackPipeline()
    {
        _subtitle = new SubtitleChain();
        _video = new VideoChain();
        _wakeTargets = [
            _readerNode,
            _video.DecodeNode,
            _audioDecodeNode,
            _subtitle.DecodeNode,
            _video.RenderNode,
            _audioRenderNode,
            _subtitle.RenderNode,
        ];
        _events = new PipelineEvents(WakeAll);
        _timeline = new PipelineTimeline(NotifyClockMoved);
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

    public AudioFilterChain AudioFilters { get; } = new();

    public ISourceProviderFactory? SourceFactory { get; set; }

    public ISynchronizer Synchronizer { get; set; } = new DefaultSynchronizer();

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

    public string? AudioDecoderName => _audioDecoder.CurrentComponent?.Name;

    public string? VideoRendererName => _video.Renderer.CurrentComponent?.Name;

    public string? AudioRendererName => _audioRenderer.CurrentComponent?.Name;

    public string? SubtitleDecoderName => _subtitle.Decoder.CurrentComponent?.Name;

    public string? SubtitleRendererName => _subtitle.Renderer.CurrentComponent?.Name;

    public bool IsVideoHardwareAccelerated => _video.Decoder.CurrentComponent?.IsHardwareAccelerated ?? false;

    public bool IsAudioHardwareAccelerated => _audioDecoder.CurrentComponent?.IsHardwareAccelerated ?? false;

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

    public long AudioUnderruns => _audioRenderer.CurrentComponent?.UnderrunCount ?? 0;

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

        if (!CheckState(out MediaEngineState state, MediaEngineState.Playing))
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
            _timeline.AdvanceEpoch(_timeline.Position, SeekMode.Precise);
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

        _video.PacketQueue.Clear();
        _audioPacketQueue.Clear();
        _subtitle.PacketQueue.Clear();

        while (_video.FrameQueue.TryDequeue(out FrameEntry frame))
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
        _video.Decoder.Reset();
        _audioDecoder.Reset();
        _video.Renderer.Reset();
        _audioRenderer.CurrentComponent?.Flush();
        _audioRenderer.Reset();
        _subtitle.Decoder.Reset();
        _subtitle.Renderer.Reset();

        _video.Target?.SurfaceSizeChanged -= VideoTarget_SurfaceSizeChanged;
        _video.Target = null;
        _subtitle.Target?.SurfaceSizeChanged -= SubtitleTarget_SurfaceSizeChanged;
        _subtitle.Target = null;

        _timeline.Stop();

        _timeline.EndingEpoch = -1;
        _videoRenderEndedEpoch = -1;
        _audioRenderEndedEpoch = -1;

        Volatile.Write(ref _readerTailEpoch, -1);
        Interlocked.Exchange(ref _readerTailVideoTicks, -1);
        Interlocked.Exchange(ref _readerTailAudioTicks, -1);
        Interlocked.Exchange(ref _readerTailSubtitleTicks, -1);

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
        _timeline.EndingEpoch = epoch;
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
        Interlocked.Increment(ref _subtitleSurfaceVersion);
        _subtitle.RenderNode.Wake();
    }

    #endregion

    #region Communication

    private bool CheckState(out MediaEngineState state, params MediaEngineState[] validStates)
    {
        state = _events.State;
        if (validStates.Contains(state))
        {
            return true;
        }

        return false;
    }

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
