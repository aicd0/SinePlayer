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
    private const int VideoPacketMinimum = 64;
    private const int VideoPacketWake = VideoPacketMinimum / 2;

    private const int AudioMinimumMilliseconds = 500;
    private const int VideoStarvationMilliseconds = 200;
    private const int VideoStarvationResumeMilliseconds = 100;
    private const int VideoIdleTimeoutMilliseconds = 50;
    private const int AudioIdleTimeoutMilliseconds = 5;
    private const int ClockSyncToleranceMilliseconds = 10;
    private const int AudioDeviceLeewayMilliseconds = 3000;

    #endregion

    #region Variables

    private readonly ITaskDispatcher _playbackQueue = TaskDispatcher.Factory.NewQueue("Pipeline - Playback");
    private readonly SeqLock<VideoTransform> _videoTransform = new(VideoTransform.Identity);
    private readonly SeqLock<SubtitlePlacement> _subtitlePlacement = new(SubtitlePlacement.Identity);

    private MediaInfo? _info;
    private long _audioMinimumSamples;
    private long _audioWakeSamples;

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

    private readonly AudioQueue _audioQueue = new();
    private readonly ConcurrentQueue<PacketEntry> _packetQueue = new();
    private readonly ConcurrentQueue<FrameEntry> _frameQueue = new();
    private readonly SubtitleCueWindow _cueWindow = new();

    private Thread? _readerThread;
    private Thread? _videoDecodeThread;
    private Thread? _videoRenderThread;
    private Thread? _audioRenderThread;
    private Thread? _subtitleRenderThread;

    private readonly PipelineNode _readerNode = new();
    private readonly PipelineNode _videoDecodeNode = new();
    private readonly PipelineNode _videoRenderNode = new();
    private readonly PipelineNode _audioRenderNode = new();
    private readonly PipelineNode _subtitleRenderNode = new();

    private bool _disposed;
    private volatile MediaEngineState _state = MediaEngineState.None;
    private volatile string? _error;
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
        }
    }

    public SubtitlePlacement SubtitlePlacement
    {
        get => _subtitlePlacement.Read();
        set
        {
            if (_subtitlePlacement.Read() == value)
            {
                return;
            }

            _subtitlePlacement.Write(_ => value);
            _subtitleRenderNode.Wake();
        }
    }

    public MediaEngineState State => _state;

    public string? Error => _error;

    public MediaInfo? MediaInfo => _info;

    public bool HasVideo => _info?.VideoStreams.Count > 0;

    public bool HasAudio => _info?.AudioStreams.Count > 0;

    public VideoStreamInfo? SelectedVideoStream => _source.Latest?.Component?.VideoStream;

    public AudioStreamInfo? SelectedAudioStream => _source.Latest?.Component?.AudioStream;

    public SubtitleStreamInfo? SelectedSubtitleStream => _source.Latest?.Component?.SubtitleStream;

    public string? VideoDecoderName => _videoDecoder.CurrentComponent?.Name;

    public string? AudioDecoderName => _audioDecoder.CurrentComponent?.Name;

    public string? VideoRendererName => _videoRenderer.CurrentComponent?.Name;

    public string? AudioRendererName => _audioRenderer.CurrentComponent?.Name;

    public bool IsVideoHardwareAccelerated => _videoDecoder.CurrentComponent?.IsHardwareAccelerated ?? false;

    public bool IsAudioHardwareAccelerated => _audioDecoder.CurrentComponent?.IsHardwareAccelerated ?? false;

    public TimeSpan Duration => _info?.Duration ?? TimeSpan.Zero;

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

    public Task<OperationResult> Open(MediaLocator locator, IVideoRenderTarget? videoTarget = null, IVideoRenderTarget? subtitleTarget = null)
    {
        ArgumentNullException.ThrowIfNull(locator);
        return Submit(() => OpenCore(locator, videoTarget, subtitleTarget));
    }

    public Task<OperationResult> Play()
    {
        return Submit(PlayCore);
    }

    public Task<OperationResult> Pause()
    {
        return Submit(PauseCore);
    }

    public Task<OperationResult> Seek(TimeSpan position, SeekMode mode = SeekMode.Fast)
    {
        return Submit(() => SeekCore(position, mode));
    }

    public Task<OperationResult> Reconcile()
    {
        return Submit(ReconcileCore);
    }

    public Task<OperationResult> SelectStream(MediaStreamKind kind, MediaStreamInfo? stream)
    {
        return Submit(() => SelectStreamCore(kind, stream));
    }

    public Task<OperationResult> Stop()
    {
        return Submit(StopCore);
    }

    #endregion

    #region Playback

    private OperationResult OpenCore(MediaLocator locator, IVideoRenderTarget? videoTarget, IVideoRenderTarget? subtitleTarget)
    {
        if (_disposed)
        {
            return OperationResult.Failure("The pipeline has already been disposed.");
        }

        ISourceProviderFactory? sourceFactory = SourceFactory;
        if (sourceFactory is null)
        {
            return OperationResult.Failure("No source provider factory was registered.");
        }

        StopCore();
        SetState(MediaEngineState.Opening);

        OperationResult result = PreparePlayback(locator, sourceFactory, videoTarget, subtitleTarget);
        if (!result.IsSuccessful)
        {
            StopCore();
            return result;
        }

        SetState(MediaEngineState.Paused);

        _stopRequested = false;
        StartReaderThread();
        StartVideoDecodeThread();
        StartVideoRenderThread();
        StartAudioRenderThread();
        StartSubtitleRenderThread();

        return OperationResult.Success();
    }

    private OperationResult PreparePlayback(MediaLocator locator, ISourceProviderFactory sourceFactory, IVideoRenderTarget? videoTarget, IVideoRenderTarget? subtitleTarget)
    {
        OperationResult opened = sourceFactory.TryOpen(locator, out IMediaSource? source);
        if (!opened.IsSuccessful)
        {
            return opened;
        }

        if (source is null)
        {
            return OperationResult.Failure("The source opened nothing to play.");
        }

        if (source.Info.VideoStreams.Count == 0 && source.Info.AudioStreams.Count == 0)
        {
            source.Dispose();
            return OperationResult.Failure("The file holds neither video nor audio.");
        }

        _info = source.Info;

        if (!ReferenceEquals(_videoTarget, videoTarget))
        {
            _videoTarget?.SurfaceSizeChanged -= VideoTarget_SurfaceSizeChanged;
            _videoTarget = videoTarget;
            _videoTarget?.SurfaceSizeChanged += VideoTarget_SurfaceSizeChanged;
        }

        if (!ReferenceEquals(_subtitleTarget, subtitleTarget))
        {
            _subtitleTarget?.SurfaceSizeChanged -= SubtitleTarget_SurfaceSizeChanged;
            _subtitleTarget = subtitleTarget;
            _subtitleTarget?.SurfaceSizeChanged += SubtitleTarget_SurfaceSizeChanged;
        }

        MediaSourceHolder sourceHolder = new(source, null, null, null);
        OperationResult built = BuildChains(sourceHolder, out _);
        if (!built.IsSuccessful)
        {
            sourceHolder.Dispose();
            source.Dispose();
            return built;
        }

        lock (_clockLock)
        {
            _clock.SetPosition(TimeSpan.Zero);
        }

        AdvanceEpoch(TimeSpan.Zero, SeekMode.Fast);
        return OperationResult.Success();
    }

    private void StartReaderThread()
    {
        if (_readerThread is not null)
        {
            return;
        }

        _readerThread = new Thread(ReaderCore)
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

    private OperationResult PlayCore()
    {
        if (_disposed)
        {
            return OperationResult.Failure("The pipeline has already been disposed.");
        }

        MediaSourceHolder? sourceHolder = _source.Latest?.Component;
        if (sourceHolder is null)
        {
            return OperationResult.Failure("Nothing is open.");
        }

        if (sourceHolder.VideoStream is null && sourceHolder.AudioStream is null)
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

        if (!_source.HasComponent)
        {
            return OperationResult.Failure("Nothing is open.");
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

        IMediaSource? source = _source.Latest?.Component?.Source;
        if (source is null)
        {
            return OperationResult.Failure("Nothing is open.");
        }

        if (!source.CanSeek)
        {
            return OperationResult.Failure("The source cannot be seeked.");
        }

        if (!CheckState(out MediaEngineState state, MediaEngineState.Paused, MediaEngineState.Playing, MediaEngineState.Ended))
        {
            return OperationResult.Failure($"Cannot seek when the state is {state}.");
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

    private OperationResult ReconcileCore()
    {
        if (_disposed)
        {
            return OperationResult.Failure("The pipeline has already been disposed.");
        }

        if (!_source.HasComponent)
        {
            return OperationResult.Success();
        }

        if (!CheckState(out MediaEngineState state, MediaEngineState.Paused, MediaEngineState.Playing, MediaEngineState.Ended))
        {
            return OperationResult.Failure($"Cannot change the pipeline's components when the state is {state}.");
        }

        MediaSourceHolder? sourceHolder = _source.Latest?.Component;
        if (sourceHolder is null)
        {
            return OperationResult.Success();
        }

        OperationResult built = BuildChains(sourceHolder, out ChainChanges changes);
        if (!built.IsSuccessful)
        {
            return built;
        }

        if (changes != ChainChanges.None)
        {
            AdvanceEpoch(_clock.Position, SeekMode.Precise);
            WakeAll();
        }

        return OperationResult.Success();
    }

    private OperationResult SelectStreamCore(MediaStreamKind kind, MediaStreamInfo? stream)
    {
        if (_disposed)
        {
            return OperationResult.Failure("The pipeline has already been disposed.");
        }

        MediaSourceHolder? current = _source.Latest?.Component;
        if (current is null || _info is null)
        {
            return OperationResult.Failure("Nothing is open.");
        }

        if (!CheckState(out MediaEngineState state, MediaEngineState.Paused, MediaEngineState.Playing, MediaEngineState.Ended))
        {
            return OperationResult.Failure($"Cannot change tracks when the state is {state}.");
        }

        MediaStreamInfo? target = null;
        if (stream is not null)
        {
            target = _info.FindStream(stream.Index);
            if (target is null)
            {
                return OperationResult.Failure($"Stream {stream.Index} is not part of the open file.");
            }

            if (target.Kind != kind)
            {
                return OperationResult.Failure($"Stream {stream.Index} is a {target.Kind} stream, not a {kind} stream.");
            }
        }

        var sourceHolder = new MediaSourceHolder(
            current.Source,
            kind == MediaStreamKind.Video ? (VideoStreamInfo?)target : current.VideoStream,
            kind == MediaStreamKind.Audio ? (AudioStreamInfo?)target : current.AudioStream,
            kind == MediaStreamKind.Subtitle ? (SubtitleStreamInfo?)target : current.SubtitleStream);

        OperationResult built = BuildChains(sourceHolder, out ChainChanges changes);
        if (!built.IsSuccessful)
        {
            return built;
        }

        if (changes != ChainChanges.None)
        {
            if (sourceHolder.VideoStream is null && sourceHolder.AudioStream is null && _state == MediaEngineState.Playing)
            {
                SetState(MediaEngineState.Paused);
            }

            AdvanceEpoch(_clock.Position, SeekMode.Precise);
            WakeAll();
        }

        return OperationResult.Success();
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

        while (_packetQueue.TryDequeue(out PacketEntry packet))
        {
            packet.Packet?.Dispose();
        }

        while (_frameQueue.TryDequeue(out FrameEntry frame))
        {
            frame.Frame?.Dispose();
        }

        _audioQueue.Clear();

        _source.Latest?.Component?.Source?.Dispose();
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

        _info = null;

        Interlocked.Exchange(ref _endingEpoch, -1);
        _videoRenderEndedEpoch = -1;
        _audioRenderEndedEpoch = -1;

        Volatile.Write(ref _readerTailEpoch, -1);
        Interlocked.Exchange(ref _readerTailVideoTicks, -1);
        Interlocked.Exchange(ref _readerTailAudioTicks, -1);

        Interlocked.Exchange(ref _presentedFrameCount, 0);
        Interlocked.Exchange(ref _droppedFrameCount, 0);
        Interlocked.Exchange(ref _decodedFrameCount, 0);
        Interlocked.Exchange(ref _decodeTicks, 0);
        Interlocked.Exchange(ref _renderTicks, 0);
        Interlocked.Exchange(ref _pictureTicks, -1);

        AdvanceEpoch(TimeSpan.Zero, SeekMode.Fast);
        SetState(MediaEngineState.None, null);
        return OperationResult.Success();
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

    private OperationResult BuildChains(MediaSourceHolder sourceHolder, out ChainChanges changes)
    {
        changes = ChainChanges.None;

        IMediaSource source = sourceHolder.Source;
        VideoStreamInfo? videoStream = sourceHolder.VideoStream;
        AudioStreamInfo? audioStream = sourceHolder.AudioStream;
        SubtitleStreamInfo? subtitleStream = sourceHolder.SubtitleStream;
        ChainChanges sourceChanges = DiffSource(sourceHolder);

        ComponentResolver resolver = Components.Clone();
        PipelineComponent<IVideoRenderer>? newRenderer = null;
        PipelineComponent<IVideoDecoder>? newDecoder = null;
        PipelineComponent<IAudioRenderer>? newAudioRenderer = null;
        PipelineComponent<IAudioDecoder>? newAudioDecoder = null;
        PipelineComponent<ISubtitleRenderer>? newSubtitleRenderer = null;
        PipelineComponent<ISubtitleDecoder>? newSubtitleDecoder = null;
        try
        {
            ChainChanges pendingChanges = sourceChanges & ChainChanges.Source;

            if (videoStream is not null)
            {
                IVideoRenderTarget? videoTarget = _videoTarget;
                if (videoTarget is null)
                {
                    return OperationResult.Failure("The file has video but no presentation surface was supplied.");
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
                    return OperationResult.Failure(rendererResolved);
                }

                newRenderer = rendererResolved.Result;
                if (newRenderer is not null)
                {
                    OperationResult configured = newRenderer.Component.Configure(videoStream, videoTarget);
                    if (!configured.IsSuccessful)
                    {
                        return configured;
                    }

                    pendingChanges |= ChainChanges.VideoRenderer;
                }

                // Resolve video decoder
                bool rebuildVideoDecoder = newRenderer is not null || sourceChanges.HasFlag(ChainChanges.VideoDecoder);
                PipelineComponent<IVideoDecoder>? latestDecoder = _videoDecoder.Latest;
                IGpuDevice? gpuDevice = (newRenderer ?? latestRenderer)?.Component?.GpuDevice;
                OperationResult<PipelineComponent<IVideoDecoder>?> decoderResolved = ResolveComponent(
                    resolver.ResolveVideoDecoders(source, videoStream, gpuDevice, DecoderBufferedFrames),
                    rebuildVideoDecoder ? null : latestDecoder?.Signature,
                    "video decoder");
                if (!decoderResolved.IsSuccessful)
                {
                    return OperationResult.Failure(decoderResolved);
                }

                newDecoder = decoderResolved.Result;
                if (newDecoder is not null)
                {
                    pendingChanges |= ChainChanges.VideoDecoder;
                }
            }
            else
            {
                pendingChanges |= sourceChanges & (ChainChanges.VideoDecoder | ChainChanges.VideoRenderer);
            }

            AudioStreamInfo? negotiatedAudioFormat = null;
            if (audioStream is not null)
            {
                // Resolve audio renderer
                bool rebuildAudioRenderer = sourceChanges.HasFlag(ChainChanges.AudioDecoder);
                PipelineComponent<IAudioRenderer>? latestRenderer = _audioRenderer.Latest;
                OperationResult<PipelineComponent<IAudioRenderer>?> rendererResolved = ResolveComponent(
                    resolver.ResolveAudioRenderers(),
                    rebuildAudioRenderer ? null : latestRenderer?.Signature,
                    "audio renderer");
                if (!rendererResolved.IsSuccessful)
                {
                    return OperationResult.Failure(rendererResolved);
                }

                newAudioRenderer = rendererResolved.Result;
                if (newAudioRenderer is not null)
                {
                    OperationResult<AudioStreamInfo> negotiated = newAudioRenderer.Component.Negotiate(audioStream);
                    if (!negotiated.IsSuccessful)
                    {
                        return OperationResult.Failure(negotiated);
                    }

                    negotiatedAudioFormat = negotiated.Result;
                    pendingChanges |= ChainChanges.AudioRenderer;
                }

                // Resolve audio decoder
                bool rebuildAudioDecoder = sourceChanges.HasFlag(ChainChanges.AudioDecoder);
                PipelineComponent<IAudioDecoder>? latestDecoder = _audioDecoder.Latest;
                OperationResult<PipelineComponent<IAudioDecoder>?> decoderResolved = ResolveComponent(
                    resolver.ResolveAudioDecoders(source, audioStream),
                    rebuildAudioDecoder ? null : latestDecoder?.Signature,
                    "audio decoder");
                if (!decoderResolved.IsSuccessful)
                {
                    return OperationResult.Failure(decoderResolved);
                }

                newAudioDecoder = decoderResolved.Result;
                if (newAudioDecoder is not null)
                {
                    pendingChanges |= ChainChanges.AudioDecoder;
                }
            }
            else
            {
                pendingChanges |= sourceChanges & (ChainChanges.AudioDecoder | ChainChanges.AudioRenderer);
            }

            if (subtitleStream is not null)
            {
                IVideoRenderTarget? subtitleTarget = _subtitleTarget;
                if (subtitleTarget is null)
                {
                    return OperationResult.Failure("The file has a subtitle track but no subtitle surface was supplied.");
                }

                // Resolve subtitle decoder
                bool rebuildSubtitleDecoder = sourceChanges.HasFlag(ChainChanges.SubtitleDecoder);
                PipelineComponent<ISubtitleDecoder>? latestDecoder = _subtitleDecoder.Latest;
                IMediaSource? subtitleSource = subtitleStream.IsExternal ? null : source;
                OperationResult<PipelineComponent<ISubtitleDecoder>?> decoderResolved = ResolveComponent(
                    resolver.ResolveSubtitleDecoders(subtitleSource, subtitleStream),
                    rebuildSubtitleDecoder ? null : latestDecoder?.Signature,
                    "subtitle decoder");
                if (!decoderResolved.IsSuccessful)
                {
                    return OperationResult.Failure(decoderResolved);
                }

                newSubtitleDecoder = decoderResolved.Result;
                if (newSubtitleDecoder is not null)
                {
                    pendingChanges |= ChainChanges.SubtitleDecoder;
                }

                // Resolve subtitle renderer
                PipelineComponent<ISubtitleDecoder>? subtitleDecoder = newSubtitleDecoder ?? latestDecoder;
                if (subtitleDecoder is null)
                {
                    return OperationResult.Failure("The subtitle track has no decoder to say what its cues are made of.");
                }

                SubtitleCueFormat format = subtitleDecoder.Component.CueFormat;
                bool rebuildSubtitleRenderer = newSubtitleDecoder is not null || sourceChanges.HasFlag(ChainChanges.SubtitleRenderer);
                PipelineComponent<ISubtitleRenderer>? latestRenderer = _subtitleRenderer.Latest;
                OperationResult<PipelineComponent<ISubtitleRenderer>?> rendererResolved = ResolveComponent(
                    resolver.ResolveSubtitleRenderers(format),
                    rebuildSubtitleRenderer ? null : latestRenderer?.Signature,
                    "subtitle renderer");
                if (!rendererResolved.IsSuccessful)
                {
                    return OperationResult.Failure(rendererResolved);
                }

                newSubtitleRenderer = rendererResolved.Result;
                if (newSubtitleRenderer is not null)
                {
                    OperationResult configured = newSubtitleRenderer.Component.Configure(subtitleStream, subtitleTarget);
                    if (!configured.IsSuccessful)
                    {
                        return configured;
                    }

                    pendingChanges |= ChainChanges.SubtitleRenderer;
                }
            }
            else
            {
                pendingChanges |= sourceChanges & (ChainChanges.SubtitleDecoder | ChainChanges.SubtitleRenderer);
            }

            PipelineVersion versions = CurrentVersions();
            long nextVersion = versions.Next;
            versions = new()
            {
                Source = pendingChanges.HasFlag(ChainChanges.Source) ? nextVersion : versions.Source,
                VideoDecoder = pendingChanges.HasFlag(ChainChanges.VideoDecoder) ? nextVersion : versions.VideoDecoder,
                VideoRenderer = pendingChanges.HasFlag(ChainChanges.VideoRenderer) ? nextVersion : versions.VideoRenderer,
                AudioDecoder = pendingChanges.HasFlag(ChainChanges.AudioDecoder) ? nextVersion : versions.AudioDecoder,
                AudioRenderer = pendingChanges.HasFlag(ChainChanges.AudioRenderer) ? nextVersion : versions.AudioRenderer,
                SubtitleDecoder = pendingChanges.HasFlag(ChainChanges.SubtitleDecoder) ? nextVersion : versions.SubtitleDecoder,
                SubtitleRenderer = pendingChanges.HasFlag(ChainChanges.SubtitleRenderer) ? nextVersion : versions.SubtitleRenderer,
            };

            if (audioStream is not null)
            {
                if (negotiatedAudioFormat is not null)
                {
                    AudioFilters.Configure(audioStream, negotiatedAudioFormat);
                }

                _audioMinimumSamples = Math.Max(1, (long)audioStream.SampleRate * AudioMinimumMilliseconds / 1000);
                _audioWakeSamples = _audioMinimumSamples / 2;
            }
            else
            {
                _audioMinimumSamples = 0;
                _audioWakeSamples = 0;
            }

            if (pendingChanges.HasFlag(ChainChanges.Source))
            {
                _source.Publish(new(sourceHolder, "source"), versions);
                changes |= ChainChanges.Source;
            }

            if (pendingChanges.HasFlag(ChainChanges.VideoDecoder))
            {
                _videoDecoder.Publish(newDecoder, versions);
                newDecoder = null;
                changes |= ChainChanges.VideoDecoder;
            }

            if (pendingChanges.HasFlag(ChainChanges.VideoRenderer))
            {
                _videoRenderer.Publish(newRenderer, versions);
                newRenderer = null;
                changes |= ChainChanges.VideoRenderer;
            }

            if (pendingChanges.HasFlag(ChainChanges.AudioDecoder))
            {
                _audioDecoder.Publish(newAudioDecoder, versions);
                newAudioDecoder = null;
                changes |= ChainChanges.AudioDecoder;
            }

            if (pendingChanges.HasFlag(ChainChanges.AudioRenderer))
            {
                _audioRenderer.Publish(newAudioRenderer, versions);
                newAudioRenderer = null;
                changes |= ChainChanges.AudioRenderer;
            }

            if (pendingChanges.HasFlag(ChainChanges.SubtitleDecoder))
            {
                _subtitleDecoder.Publish(newSubtitleDecoder, versions);
                newSubtitleDecoder = null;
                changes |= ChainChanges.SubtitleDecoder;
            }

            if (pendingChanges.HasFlag(ChainChanges.SubtitleRenderer))
            {
                _subtitleRenderer.Publish(newSubtitleRenderer, versions);
                newSubtitleRenderer = null;
                changes |= ChainChanges.SubtitleRenderer;
            }
        }
        finally
        {
            newRenderer?.Dispose();
            newDecoder?.Dispose();
            newAudioRenderer?.Dispose();
            newAudioDecoder?.Dispose();
            newSubtitleRenderer?.Dispose();
            newSubtitleDecoder?.Dispose();
        }

        return OperationResult.Success();
    }

    private ChainChanges DiffSource(MediaSourceHolder sourceHolder)
    {
        MediaSourceHolder? current = _source.Latest?.Component;
        if (!ReferenceEquals(current?.Source, sourceHolder.Source))
        {
            return ChainChanges.Source
                | ChainChanges.VideoDecoder | ChainChanges.VideoRenderer
                | ChainChanges.AudioDecoder | ChainChanges.AudioRenderer
                | ChainChanges.SubtitleDecoder | ChainChanges.SubtitleRenderer;
        }

        ChainChanges changes = ChainChanges.None;

        if (current.VideoStream?.Index != sourceHolder.VideoStream?.Index)
        {
            changes |= ChainChanges.Source | ChainChanges.VideoDecoder | ChainChanges.VideoRenderer;
        }

        if (current.AudioStream?.Index != sourceHolder.AudioStream?.Index)
        {
            changes |= ChainChanges.Source | ChainChanges.AudioDecoder | ChainChanges.AudioRenderer;
        }

        if (current.SubtitleStream?.Index != sourceHolder.SubtitleStream?.Index || current.SubtitleStream?.Path != sourceHolder.SubtitleStream?.Path)
        {
            changes |= ChainChanges.Source | ChainChanges.SubtitleDecoder | ChainChanges.SubtitleRenderer;
        }

        return changes;
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
        if (sourceHolder is not null && NeedsBuffering(sourceHolder))
        {
            _readerNode.Wake();
        }
    }

    private void ReaderCore()
    {
        ComponentSlot<MediaSourceHolder> sourceSlot = _source;
        ComponentSlot<IAudioDecoder> audioDecoderSlot = _audioDecoder;
        ComponentSlot<ISubtitleDecoder> subtitleDecoderSlot = _subtitleDecoder;
        bool pendingEpochChange = false;
        bool buffering = false;
        long workingEpoch = -1;
        long endingEpoch = -1;

        MediaPacket? packet = null;

        while (true)
        {
            long stamp = _readerNode.Stamp;
            if (_stopRequested)
            {
                break;
            }

            if (!CheckState(out _, MediaEngineState.Playing, MediaEngineState.Paused))
            {
                _readerNode.Wait(stamp);
                continue;
            }

            // Install source
            if (sourceSlot.Update(out MediaSourceHolder? oldSourceHolder))
            {
                oldSourceHolder?.Dispose();
                pendingEpochChange = true;
            }

            MediaSourceHolder? sourceHolder = sourceSlot.CurrentComponent;
            IMediaSource? source = sourceHolder?.Source;
            if (sourceHolder is null || source is null)
            {
                _readerNode.Wait(stamp);
                continue;
            }

            // Install audio decoder
            if (audioDecoderSlot.Update(out IAudioDecoder? oldAudioDecoder))
            {
                oldAudioDecoder?.Dispose();
            }

            IAudioDecoder? audioDecoder = audioDecoderSlot.CurrentComponent;

            // Install subtitle decoder
            EpochGate epochGate = _epochGate.Read();
            if (subtitleDecoderSlot.Update(out ISubtitleDecoder? oldSubtitleDecoder))
            {
                oldSubtitleDecoder?.Dispose();
                _cueWindow.Reset();
            }

            ISubtitleDecoder? subtitleDecoder = subtitleDecoderSlot.CurrentComponent;

            // Bump epoch
            long epoch = epochGate.Epoch;
            if (epoch != workingEpoch)
            {
                workingEpoch = epoch;
                pendingEpochChange = false;

                Volatile.Write(ref _readerTailEpoch, epoch);
                Interlocked.Exchange(ref _readerTailVideoTicks, -1);
                Interlocked.Exchange(ref _readerTailAudioTicks, -1);

                TimeSpan seekPosition = epochGate.SeekPosition;
                OperationResult seeked;
                try
                {
                    seeked = source.Seek(seekPosition, ChooseSeekLanding(sourceHolder, seekPosition));
                }
                catch (Exception ex)
                {
                    seeked = OperationResult.Failure(ex);
                }

                if (seeked.IsSuccessful)
                {
                    endingEpoch = -1;

                    try
                    {
                        audioDecoder?.Flush();
                    }
                    catch (Exception ex)
                    {
                        PostWarning($"The audio decoder could not be flushed: {ex.Message}");
                    }

                    try
                    {
                        subtitleDecoder?.Flush();
                    }
                    catch (Exception ex)
                    {
                        PostWarning($"The subtitle decoder could not be flushed: {ex.Message}");
                    }

                    _cueWindow.SkipTo(seekPosition);
                }
                else
                {
                    PostWarning($"The source refused to seek to {seekPosition:g}: {seeked.Message}");
                }

                continue;
            }

            if (pendingEpochChange || endingEpoch == epoch)
            {
                _readerNode.Wait(stamp);
                continue;
            }

            // Buffering
            if (!buffering)
            {
                if (!NeedsBuffering(sourceHolder))
                {
                    _readerNode.Wait(stamp);
                    continue;
                }

                buffering = true;
            }
            else if (IsBuffered(sourceHolder))
            {
                buffering = false;
                continue;
            }

            if (packet is null)
            {
                OperationResult read;
                try
                {
                    read = source.TryReadPacket(out packet);
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
            }

            // Route package
            PipelineVersion sourceVersions = sourceSlot.CurrentVersions;
            PipelineVersion audioDecoderVersions = audioDecoderSlot.CurrentVersions;
            PipelineVersion subtitleDecoderVersions = subtitleDecoderSlot.CurrentVersions;
            var audioBufferVersions = PipelineVersion.Max(sourceVersions, audioDecoderVersions);
            var routed = OperationResult.Success();
            bool ending = false;
            bool handedOn = false;
            if (packet is null)
            {
                ending = true;
            }
            else
            {
                AudioStreamInfo? audioStreamInfo = sourceHolder.AudioStream;
                VideoStreamInfo? videoStreamInfo = sourceHolder.VideoStream;
                SubtitleStreamInfo? subtitleStreamInfo = sourceHolder.SubtitleStream;
                if (audioStreamInfo is not null && packet.StreamIndex == audioStreamInfo.Index)
                {
                    if (audioDecoder is not null)
                    {
                        VersionVerdict verdict = PipelineVersion.CompareAudioDecoder(sourceVersions, audioDecoderVersions);
                        switch (verdict)
                        {
                            case VersionVerdict.Accept:
                                AdvanceReaderTail(epoch, packet.Timestamp, ref _readerTailAudioTicks);
                                routed = DecodeAudio(audioDecoder, packet, audioBufferVersions, epoch);
                                break;
                            case VersionVerdict.Block:
                                _readerNode.Wait(stamp);
                                continue;
                            case VersionVerdict.Drop:
                                break;
                            default:
                                throw new InvalidOperationException($"Unknown verdict '{verdict}'.");
                        }
                    }
                }
                else if (videoStreamInfo is not null && packet.StreamIndex == videoStreamInfo.Index)
                {
                    AdvanceReaderTail(epoch, packet.Timestamp, ref _readerTailVideoTicks);
                    _packetQueue.Enqueue(new PacketEntry(packet, sourceVersions, epoch));
                    handedOn = true;
                    TryWakeVideoDecode();
                }
                else if (subtitleStreamInfo is not null && packet.StreamIndex == subtitleStreamInfo.Index)
                {
                    if (subtitleDecoder is not null)
                    {
                        VersionVerdict verdict = PipelineVersion.CompareSubtitleDecoder(sourceVersions, subtitleDecoderVersions);
                        switch (verdict)
                        {
                            case VersionVerdict.Accept:
                                routed = DecodeSubtitle(subtitleDecoder, packet);
                                break;
                            case VersionVerdict.Block:
                                _readerNode.Wait(stamp);
                                continue;
                            case VersionVerdict.Drop:
                                break;
                            default:
                                throw new InvalidOperationException($"Unknown verdict '{verdict}'.");
                        }
                    }
                }
            }

            if (!handedOn)
            {
                packet?.Dispose();
            }

            packet = null;

            if (!routed.IsSuccessful)
            {
                PostFailure(routed);
                break;
            }

            // Ending
            if (ending)
            {
                endingEpoch = epoch;

                OperationResult drained = DrainAudioDecoder(audioDecoder, audioBufferVersions, epoch);
                if (!drained.IsSuccessful)
                {
                    PostWarning(drained.Message);
                }

                _packetQueue.Enqueue(new PacketEntry(null, sourceVersions, epoch));
                _audioQueue.Enqueue(new AudioEntry(null, audioBufferVersions, epoch));
                TryWakeVideoDecode();
                TryWakeAudioRender();

                long epochCopy = epoch;
                Submit(() => NotifySourceEnded(epochCopy));
            }
        }

        // Cleanup
        packet?.Dispose();
    }

    private static int ChooseSeekLanding(MediaSourceHolder sourceHolder, TimeSpan position)
    {
        VideoStreamInfo? video = sourceHolder.VideoStream;
        AudioStreamInfo? audio = sourceHolder.AudioStream;

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

    private OperationResult DecodeSubtitle(ISubtitleDecoder subtitleDecoder, MediaPacket packet)
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

        _cueWindow.Add(cues, _clock.Position, _subtitleRenderer.HasComponent);
        if (cues.Count > 0)
        {
            _subtitleRenderNode.Wake();
        }

        return OperationResult.Success();
    }

    private bool IsBuffered(MediaSourceHolder holder)
    {
        if (holder.AudioStream is not null && _audioDecoder.HasComponent && _audioQueue.Samples < _audioMinimumSamples)
        {
            return false;
        }

        if (holder.VideoStream is not null && _videoDecoder.HasComponent && _packetQueue.Count < VideoPacketMinimum)
        {
            return false;
        }

        return true;
    }

    private bool NeedsBuffering(MediaSourceHolder holder)
    {
        if (holder.AudioStream is not null && _audioDecoder.HasComponent && _audioQueue.Samples < _audioWakeSamples)
        {
            return true;
        }

        if (holder.VideoStream is not null && _videoDecoder.HasComponent && _packetQueue.Count < VideoPacketWake)
        {
            return true;
        }

        return false;
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

    #endregion

    #region Video decode

    private void TryWakeVideoDecode()
    {
        if (!HasRoomForVideoFrame())
        {
            return;
        }

        if (!_packetQueue.IsEmpty)
        {
            _videoDecodeNode.Wake();
        }
    }

    private void VideoDecodeCore()
    {
        ConcurrentQueue<PacketEntry> queue = _packetQueue;
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
                    }
                    else
                    {
                        deviceFormat = null;
                        PostWarning($"The audio device could not be opened: {opened.Message}");
                    }

                    deviceAvailable = deviceFormat is not null;
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
                            deviceAvailable = false;
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
                            deviceAvailable = false;
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

            TryWakeReader();

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
        SubtitlePlacement drawnPlacement = default;
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
                        ReportOnFailure("The subtitles could not be cleared", SafeInvoke(() => oldRenderer.Show([], SubtitlePlacement)));
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
            SubtitleCueSnapshot cues = _cueWindow.Visible(position);
            long surfaceVersion = Interlocked.Read(ref _subtitleSurfaceVersion);
            SubtitlePlacement placement = SubtitlePlacement;
            if (drawnInvalidated ||
                surfaceVersion != drawnSurfaceVersion ||
                placement != drawnPlacement ||
                !SameCues(cues, drawnCues))
            {
                ReportOnFailure("The subtitles could not be drawn", SafeInvoke(() => renderer.Show(cues.Cues, placement)));
                drawnCues.Dispose();
                drawnCues = cues;
                drawnInvalidated = false;
                drawnSurfaceVersion = surfaceVersion;
                drawnPlacement = placement;
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
                    timeout = (int)Math.Clamp(Math.Ceiling(next.Value.TotalMilliseconds), 1, int.MaxValue);
                }
            }

            _subtitleRenderNode.Wait(stamp, timeout);
        }

        drawnCues.Dispose();
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
