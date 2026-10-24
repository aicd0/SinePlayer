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
    private readonly Lock _componentLock = new();
    private readonly SeqLock<VideoTransform> _videoTransform = new(VideoTransform.Identity);

    private MediaInfo? _info;
    private VideoStreamInfo? _videoStreamInfo;
    private AudioStreamInfo? _audioStreamInfo;
    private long _audioMinimumSamples;
    private long _audioWakeSamples;

    private IVideoRenderTarget? _videoTarget;
    private readonly ComponentSlot<IMediaSource> _source = new();
    private readonly ComponentSlot<IVideoDecoder> _videoDecoder = new();
    private readonly ComponentSlot<IAudioDecoder> _audioDecoder = new();
    private readonly ComponentSlot<IVideoRenderer> _videoRenderer = new();
    private readonly ComponentSlot<IAudioRenderer> _audioRenderer = new();

    private readonly AudioQueue _audioQueue = new();
    private readonly ConcurrentQueue<PacketEntry> _packetQueue = new();
    private readonly ConcurrentQueue<FrameEntry> _frameQueue = new();

    private Thread? _readerThread;
    private Thread? _videoDecodeThread;
    private Thread? _videoRenderThread;
    private Thread? _audioRenderThread;

    private readonly PipelineNode _readerNode = new();
    private readonly PipelineNode _videoDecodeNode = new();
    private readonly PipelineNode _videoRenderNode = new();
    private readonly PipelineNode _audioRenderNode = new();

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
    private long _displayedFrameCount = -1;
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

    public MediaEngineState State => _state;

    public string? Error => _error;

    public MediaInfo? MediaInfo => _info;

    public bool HasVideo => _videoStreamInfo is not null;

    public bool HasAudio => _audioStreamInfo is not null;

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

    public long? DisplayedFrameCount
    {
        get
        {
            long displayed = Interlocked.Read(ref _displayedFrameCount);
            return displayed < 0 ? null : displayed;
        }
    }

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

    public Task<OperationResult> Open(MediaLocator locator, IVideoRenderTarget? videoTarget = null)
    {
        ArgumentNullException.ThrowIfNull(locator);
        return Submit(() => OpenCore(locator, videoTarget));
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

    public Task<OperationResult> Stop()
    {
        return Submit(StopCore);
    }

    #endregion

    #region Playback

    private OperationResult OpenCore(MediaLocator locator, IVideoRenderTarget? videoTarget)
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

        OperationResult result = PreparePlayback(locator, sourceFactory, videoTarget);
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

        return OperationResult.Success();
    }

    private OperationResult PreparePlayback(MediaLocator locator, ISourceProviderFactory sourceFactory, IVideoRenderTarget? videoTarget)
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

        VideoStreamInfo? videoStreamInfo = source.Info.VideoStreams.Count > 0 ? source.Info.VideoStreams[0] : null;
        AudioStreamInfo? audioStreamInfo = source.Info.AudioStreams.Count > 0 ? source.Info.AudioStreams[0] : null;

        if (videoStreamInfo is null && audioStreamInfo is null)
        {
            return OperationResult.Failure("The file holds neither video nor audio.");
        }

        _source.Publish(new(source, "source"), default(PipelineVersion).WithSource(1));
        _info = source.Info;
        _videoStreamInfo = videoStreamInfo;
        _audioStreamInfo = audioStreamInfo;

        if (videoStreamInfo is null)
        {
            videoTarget = null;
        }

        if (!ReferenceEquals(_videoTarget, videoTarget))
        {
            _videoTarget?.SurfaceSizeChanged -= VideoTarget_SurfaceSizeChanged;
            _videoTarget = videoTarget;
            _videoTarget?.SurfaceSizeChanged += VideoTarget_SurfaceSizeChanged;
        }

        OperationResult built = BuildChains(out _);
        if (!built.IsSuccessful)
        {
            return built;
        }

        if (_audioStreamInfo is { } playingAudio)
        {
            _audioMinimumSamples = Math.Max(1, (long)playingAudio.SampleRate * AudioMinimumMilliseconds / 1000);
            _audioWakeSamples = _audioMinimumSamples / 2;
        }

        _epochGate.Write(x => new EpochGate()
        {
            Epoch = x.Epoch + 1,
            SeekPosition = TimeSpan.Zero,
            SeekMode = SeekMode.Fast,
        });

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

    private OperationResult PlayCore()
    {
        if (_disposed)
        {
            return OperationResult.Failure("The pipeline has already been disposed.");
        }

        if (!_source.HasComponent)
        {
            return OperationResult.Failure("Nothing is open.");
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

        IMediaSource? source = _source.Latest?.Component;
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

        SetState(MediaEngineState.Paused);

        _epochGate.Write(x => new EpochGate()
        {
            Epoch = x.Epoch + 1,
            SeekPosition = position,
            SeekMode = mode,
        });

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

        OperationResult built = BuildChains(out ChainChanges changes);
        if (!built.IsSuccessful)
        {
            return built;
        }

        if (changes != ChainChanges.None)
        {
            TimeSpan position = _clock.Position;

            _epochGate.Write(x => new EpochGate()
            {
                Epoch = x.Epoch + 1,
                SeekPosition = position,
                SeekMode = SeekMode.Precise,
            });

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

        while (_packetQueue.TryDequeue(out PacketEntry packet))
        {
            packet.Packet?.Dispose();
        }

        while (_frameQueue.TryDequeue(out FrameEntry frame))
        {
            frame.Frame?.Dispose();
        }

        _audioQueue.Clear();

        lock (_componentLock)
        {
            _source.Reset();
            _videoDecoder.Reset();
            _audioDecoder.Reset();
            _videoRenderer.Reset();
            _audioRenderer.CurrentComponent?.Flush();
            _audioRenderer.Reset();
        }

        _videoTarget?.SurfaceSizeChanged -= VideoTarget_SurfaceSizeChanged;
        _videoTarget = null;

        lock (_clockLock)
        {
            _clock.Pause();
            _anchoredEpoch = -1;
            _videoStarved = false;
            _audioStarved = false;
        }

        _info = null;
        _videoStreamInfo = null;
        _audioStreamInfo = null;

        Interlocked.Exchange(ref _endingEpoch, -1);
        _videoRenderEndedEpoch = -1;
        _audioRenderEndedEpoch = -1;

        Volatile.Write(ref _readerTailEpoch, -1);
        Interlocked.Exchange(ref _readerTailVideoTicks, -1);
        Interlocked.Exchange(ref _readerTailAudioTicks, -1);

        Interlocked.Exchange(ref _presentedFrameCount, 0);
        Interlocked.Exchange(ref _droppedFrameCount, 0);
        Interlocked.Exchange(ref _displayedFrameCount, -1);
        Interlocked.Exchange(ref _decodedFrameCount, 0);
        Interlocked.Exchange(ref _decodeTicks, 0);
        Interlocked.Exchange(ref _renderTicks, 0);
        Interlocked.Exchange(ref _pictureTicks, -1);

        _epochGate.Write(x => new EpochGate() { Epoch = x.Epoch + 1 });

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

        if (HasVideo && _videoRenderEndedEpoch != _endingEpoch)
        {
            return;
        }

        if (HasAudio && _audioRenderEndedEpoch != _endingEpoch)
        {
            return;
        }

        SetState(MediaEngineState.Ended);
        WakeAll();
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
        VideoRenderer = 1,
        VideoDecoder = 2,
        AudioRenderer = 4,
        AudioDecoder = 8,
    }

    private OperationResult BuildChains(out ChainChanges changes)
    {
        changes = ChainChanges.None;

        IMediaSource? source = _source.Latest?.Component;
        if (source is null)
        {
            return OperationResult.Failure("Nothing is open to build a chain for.");
        }

        ComponentResolver resolver = Components.Clone();

        PipelineComponent<IVideoRenderer>? newRenderer = null;
        PipelineComponent<IVideoDecoder>? newDecoder = null;
        PipelineComponent<IAudioRenderer>? newAudioRenderer = null;
        PipelineComponent<IAudioDecoder>? newAudioDecoder = null;

        try
        {
            lock (_componentLock)
            {
                ChainChanges pendingChanges = ChainChanges.None;

                VideoStreamInfo? videoStream = _videoStreamInfo;
                if (videoStream is not null)
                {
                    IVideoRenderTarget? videoTarget = _videoTarget;
                    if (videoTarget is null)
                    {
                        return OperationResult.Failure("The file has video but no presentation surface was supplied.");
                    }

                    // Resolve video renderer
                    PipelineComponent<IVideoRenderer>? latestRenderer = _videoRenderer.Latest;
                    OperationResult<PipelineComponent<IVideoRenderer>?> rendererResolved = ResolveComponent(
                        resolver.ResolveVideoRenderers(),
                        latestRenderer?.Signature,
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
                    PipelineComponent<IVideoDecoder>? latestDecoder = _videoDecoder.Latest;
                    IGpuDevice? gpuDevice = (newRenderer ?? latestRenderer)?.Component?.GpuDevice;
                    OperationResult<PipelineComponent<IVideoDecoder>?> decoderResolved = ResolveComponent(
                        resolver.ResolveVideoDecoders(source, videoStream, gpuDevice, DecoderBufferedFrames),
                        newRenderer is null ? latestDecoder?.Signature : null,
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

                AudioStreamInfo? audioStream = _audioStreamInfo;
                if (audioStream is not null)
                {
                    AudioFilters.Configure(audioStream);

                    BaseOperationResult? audioFailure = null;

                    // Resolve audio renderer
                    PipelineComponent<IAudioRenderer>? latestRenderer = _audioRenderer.Latest;
                    OperationResult<PipelineComponent<IAudioRenderer>?> rendererResolved = ResolveComponent(
                        resolver.ResolveAudioRenderers(),
                        latestRenderer?.Signature,
                        "audio renderer");
                    if (rendererResolved.IsSuccessful)
                    {
                        newAudioRenderer = rendererResolved.Result;
                        if (newAudioRenderer is not null)
                        {
                            OperationResult configured = newAudioRenderer.Component.Configure(audioStream);
                            if (configured.IsSuccessful)
                            {
                                pendingChanges |= ChainChanges.AudioRenderer;
                            }
                            else
                            {
                                audioFailure = configured;
                            }
                        }
                    }
                    else
                    {
                        audioFailure = rendererResolved;
                    }

                    if (audioFailure is null)
                    {
                        // Resolve audio decoder
                        PipelineComponent<IAudioDecoder>? latestDecoder = _audioDecoder.Latest;
                        OperationResult<PipelineComponent<IAudioDecoder>?> decoderResolved = ResolveComponent(
                            resolver.ResolveAudioDecoders(source, audioStream),
                            latestDecoder?.Signature,
                            "audio decoder");
                        if (!decoderResolved.IsSuccessful)
                        {
                            audioFailure = decoderResolved;
                        }

                        newAudioDecoder = decoderResolved.Result;
                        if (newAudioDecoder is not null)
                        {
                            pendingChanges |= ChainChanges.AudioDecoder;
                        }
                    }

                    if (audioFailure is not null)
                    {
                        if (videoStream is null)
                        {
                            return OperationResult.Failure(audioFailure);
                        }

                        RaiseWarning(_audioRenderer.HasComponent
                            ? $"The audio output could not be changed: {audioFailure.Message}"
                            : $"Audio is not playing: {audioFailure.Message}");
                    }
                }

                if (pendingChanges.HasFlag(ChainChanges.VideoDecoder) || pendingChanges.HasFlag(ChainChanges.VideoRenderer))
                {
                    PipelineVersion versions = CurrentVersions();
                    if (pendingChanges.HasFlag(ChainChanges.VideoDecoder) && pendingChanges.HasFlag(ChainChanges.VideoRenderer))
                    {
                        versions = versions.WithVideo(versions.Next);
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
                }

                if (pendingChanges.HasFlag(ChainChanges.AudioDecoder) || pendingChanges.HasFlag(ChainChanges.AudioRenderer))
                {
                    PipelineVersion versions = CurrentVersions();
                    if (pendingChanges.HasFlag(ChainChanges.AudioDecoder) && pendingChanges.HasFlag(ChainChanges.AudioRenderer))
                    {
                        versions = versions.WithAudio(versions.Next);
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
                }
            }
        }
        finally
        {
            newRenderer?.Dispose();
            newDecoder?.Dispose();
            newAudioRenderer?.Dispose();
            newAudioDecoder?.Dispose();
        }

        return OperationResult.Success();
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
        return versions;
    }

    #endregion

    #region Reader

    private void TryWakeReader()
    {
        if (_stopRequested || NeedsBuffering())
        {
            _readerNode.Wake();
        }
    }

    private void ReaderCore()
    {
        ComponentSlot<IMediaSource> sourceSlot = _source;
        ComponentSlot<IAudioDecoder> audioDecoderSlot = _audioDecoder;
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
            if (sourceSlot.Update(out IMediaSource? oldSource))
            {
                oldSource?.Dispose();
                pendingEpochChange = true;
            }

            IMediaSource? source = sourceSlot.CurrentComponent;
            if (source is null)
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

            // Bump epoch
            EpochGate epochGate = _epochGate.Read();
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
                    seeked = source.Seek(seekPosition);
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
                if (!NeedsBuffering())
                {
                    _readerNode.Wait(stamp);
                    continue;
                }

                buffering = true;
            }
            else if (IsBuffered())
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
                AudioStreamInfo? audioStreamInfo = _audioStreamInfo;
                VideoStreamInfo? videoStreamInfo = _videoStreamInfo;
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

                _packetQueue.Enqueue(new PacketEntry(null, default, epoch));
                _audioQueue.Enqueue(new AudioEntry(null, default, epoch));
                TryWakeVideoDecode();
                TryWakeAudioRender();

                long epochCopy = epoch;
                Submit(() => NotifySourceEnded(epochCopy));
            }
        }

        // Cleanup
        packet?.Dispose();
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

    private bool IsBuffered()
    {
        if (_audioDecoder.HasComponent && _audioQueue.Samples < _audioMinimumSamples)
        {
            return false;
        }

        ConcurrentQueue<PacketEntry> packets = _packetQueue;
        if (_videoDecoder.HasComponent && packets.Count < VideoPacketMinimum)
        {
            return false;
        }

        return true;
    }

    private bool NeedsBuffering()
    {
        if (_audioDecoder.HasComponent && _audioQueue.Samples < _audioWakeSamples)
        {
            return true;
        }

        ConcurrentQueue<PacketEntry> packets = _packetQueue;
        if (_videoDecoder.HasComponent && packets.Count < VideoPacketWake)
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
        if (_stopRequested)
        {
            _videoDecodeNode.Wake();
            return;
        }

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
            if (videoDecoder is null)
            {
                _videoDecodeNode.Wait(stamp);
                continue;
            }

            // Bump epoch
            long epoch = _epochGate.Read().Epoch;
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;

                try
                {
                    videoDecoder.Flush();
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
                    OperationResult drained = DrainVideoDecoder(videoDecoder, videoFrameVersions, packetEpoch);
                    if (!drained.IsSuccessful)
                    {
                        PostWarning(drained.Message);
                    }

                    _frameQueue.Enqueue(new FrameEntry(null, default, epoch));
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
        if (_stopRequested || !_frameQueue.IsEmpty)
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
                oldVideoRenderer?.Dispose();
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

            if (playing && !_audioDecoder.HasComponent)
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

        if (renderer.TryGetDisplayedFrameCount(out long displayed))
        {
            Interlocked.Exchange(ref _displayedFrameCount, displayed);
        }

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
        if (_stopRequested || !_audioQueue.IsEmpty)
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

                deviceAvailable = audioRendererSlot.CurrentComponent is not null;
                reportedRunningAhead = false;
                clockOffset = null;
                continue;
            }

            IAudioRenderer? audioRenderer = audioRendererSlot.CurrentComponent;

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

            buffer = AudioFilters.Apply(buffer);

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
        }
    }

    #endregion

    #region Events

    private void VideoTarget_SurfaceSizeChanged()
    {
        _videoRenderNode.Wake();
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
        return true;
    }

    private void WakeAll()
    {
        _readerNode.Wake();
        _videoDecodeNode.Wake();
        _videoRenderNode.Wake();
        _audioRenderNode.Wake();
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
}
