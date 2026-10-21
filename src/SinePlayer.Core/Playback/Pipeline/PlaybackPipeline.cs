// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Concurrent;
using System.Diagnostics;

using SinePlayer.Core.Common.DebugTools;
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

    private MediaInfo? _info;
    private VideoStreamInfo? _videoStreamInfo;
    private AudioStreamInfo? _audioStreamInfo;
    private long _audioMinimumSamples;
    private long _audioWakeSamples;
    private double _volume = 1.0;
    private bool _muted;

    private IVideoRenderTarget? _videoTarget;
    private IMediaSource? _source;
    private volatile ComponentSlot<IVideoDecoder>? _videoDecoder;
    private volatile ComponentSlot<IAudioDecoder>? _audioDecoder;
    private volatile ComponentSlot<IVideoRenderer>? _videoRenderer;
    private volatile ComponentSlot<IAudioRenderer>? _audioRenderer;
    private volatile ComponentSlot<IVideoRenderer>? _pendingVideoRenderer;
    private volatile ComponentSlot<IAudioRenderer>? _pendingAudioRenderer;
    private volatile ComponentSlot<IVideoDecoder>? _pendingVideoDecoder;
    private volatile ComponentSlot<IAudioDecoder>? _pendingAudioDecoder;
    private volatile bool _hasAudioChain;
    private volatile bool _hasVideoChain;

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
    private long _pendingSeekPosition;

    private readonly SeqLock<long> _epoch = new(0);
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

    #endregion

    #region Public API

    public event Action<MediaEngineState, string?>? StateChanged;

    public event Action<string>? WarningRaised;

    public ComponentResolver Components { get; set; } = new();

    public ISourceProviderFactory? SourceFactory { get; set; }

    public ISynchronizer Synchronizer { get; set; } = new DefaultSynchronizer();

    public MediaEngineState State => _state;

    public string? Error => _error;

    public MediaInfo? MediaInfo => _info;

    public bool HasVideo => _videoStreamInfo is not null;

    public bool HasAudio => _audioStreamInfo is not null;

    public string? VideoRendererName => _videoRenderer?.Component.Name;

    public string? AudioRendererName => _audioRenderer?.Component.Name;

    public bool IsVideoHardwareAccelerated => _videoDecoder?.Component.IsHardwareAccelerated ?? false;

    public bool IsAudioHardwareAccelerated => _audioDecoder?.Component.IsHardwareAccelerated ?? false;

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

    public double Volume
    {
        get => Volatile.Read(ref _volume);
        set
        {
            Volatile.Write(ref _volume, Math.Clamp(value, 0.0, 1.0));
            IAudioRenderer? renderer = _audioRenderer?.Component;
            renderer?.Volume = Volatile.Read(ref _volume);
        }
    }

    public bool IsMuted
    {
        get => Volatile.Read(ref _muted);
        set
        {
            Volatile.Write(ref _muted, value);
            IAudioRenderer? renderer = _audioRenderer?.Component;
            renderer?.IsMuted = value;
        }
    }

    internal TimeSpan LastPresentedTimestamp
    {
        get
        {
            long ticks = Interlocked.Read(ref _pictureTicks);
            return ticks < 0 ? TimeSpan.Zero : TimeSpan.FromTicks(ticks);
        }
    }

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

            TimeSpan lag = Position - TimeSpan.FromTicks(ticks);
            return lag > TimeSpan.Zero ? lag : TimeSpan.Zero;
        }
    }

    public void Dispose()
    {
        Task<PlaybackOperationResult> teardown = Submit(() =>
        {
            if (_disposed)
            {
                return PlaybackOperationResult.Success;
            }

            _disposed = true;
            return StopCore();
        });

        teardown.Wait();
    }

    public Task<PlaybackOperationResult> Open(MediaLocator locator, IVideoRenderTarget? videoTarget = null)
    {
        ArgumentNullException.ThrowIfNull(locator);
        return Submit(() => OpenCore(locator, videoTarget));
    }

    public Task<PlaybackOperationResult> Play()
    {
        return Submit(PlayCore);
    }

    public Task<PlaybackOperationResult> Pause()
    {
        return Submit(PauseCore);
    }

    public Task<PlaybackOperationResult> Seek(TimeSpan position)
    {
        return Submit(() => SeekCore(position));
    }

    public Task<PlaybackOperationResult> Reconcile()
    {
        return Submit(ReconcileCore);
    }

    public Task<PlaybackOperationResult> Stop()
    {
        return Submit(StopCore);
    }

    #endregion

    #region Playback

    private PlaybackOperationResult OpenCore(MediaLocator locator, IVideoRenderTarget? videoTarget)
    {
        if (_disposed)
        {
            return PlaybackOperationResult.Failure("The pipeline has already been disposed.");
        }

        ISourceProviderFactory? sourceFactory = SourceFactory;
        if (sourceFactory is null)
        {
            return PlaybackOperationResult.Failure("No source provider factory was registered.");
        }

        StopCore();
        SetState(MediaEngineState.Opening);

        PlaybackOperationResult result = PreparePlayback(locator, sourceFactory, videoTarget);
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

        return PlaybackOperationResult.Success;
    }

    private PlaybackOperationResult PreparePlayback(MediaLocator locator, ISourceProviderFactory sourceFactory, IVideoRenderTarget? videoTarget)
    {
        PlaybackOperationResult opened = sourceFactory.TryOpen(locator, out IMediaSource? source);
        if (!opened.IsSuccessful)
        {
            return opened;
        }

        if (source is null)
        {
            return PlaybackOperationResult.Failure("The source opened nothing to play.");
        }

        _source = source;
        _info = source.Info;
        VideoStreamInfo? videoStreamInfo = source.Info.VideoStreams.Count > 0 ? source.Info.VideoStreams[0] : null;
        AudioStreamInfo? audioStreamInfo = source.Info.AudioStreams.Count > 0 ? source.Info.AudioStreams[0] : null;
        _videoStreamInfo = videoStreamInfo;
        _audioStreamInfo = audioStreamInfo;

        if (videoStreamInfo is null && audioStreamInfo is null)
        {
            return PlaybackOperationResult.Failure("The file holds neither video nor audio.");
        }

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

        PlaybackOperationResult built = BuildChains(out _);
        if (!built.IsSuccessful)
        {
            return built;
        }

        if (_audioStreamInfo is { } playingAudio)
        {
            _audioMinimumSamples = Math.Max(1, (long)playingAudio.SampleRate * AudioMinimumMilliseconds / 1000);
            _audioWakeSamples = _audioMinimumSamples / 2;
        }

        Volatile.Write(ref _pendingSeekPosition, 0);
        return PlaybackOperationResult.Success;
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

    private PlaybackOperationResult PlayCore()
    {
        if (_disposed)
        {
            return PlaybackOperationResult.Failure("The pipeline has already been disposed.");
        }

        if (_source is null)
        {
            return PlaybackOperationResult.Failure("Nothing is open.");
        }

        if (!CheckState(out MediaEngineState state, MediaEngineState.Paused))
        {
            return PlaybackOperationResult.Failure($"Cannot play when the state is {state}.");
        }

        SetState(MediaEngineState.Playing);
        WakeAll();
        return PlaybackOperationResult.Success;
    }

    private PlaybackOperationResult PauseCore()
    {
        if (_disposed)
        {
            return PlaybackOperationResult.Failure("The pipeline has already been disposed.");
        }

        if (_source is null)
        {
            return PlaybackOperationResult.Failure("Nothing is open.");
        }

        if (!CheckState(out MediaEngineState state, MediaEngineState.Playing))
        {
            return PlaybackOperationResult.Failure($"Cannot pause when the state is {state}.");
        }

        SetState(MediaEngineState.Paused);
        WakeAll();
        return PlaybackOperationResult.Success;
    }

    private PlaybackOperationResult SeekCore(TimeSpan position)
    {
        if (_disposed)
        {
            return PlaybackOperationResult.Failure("The pipeline has already been disposed.");
        }

        IMediaSource? source = _source;
        if (source is null)
        {
            return PlaybackOperationResult.Failure("Nothing is open.");
        }

        if (!source.CanSeek)
        {
            return PlaybackOperationResult.Failure("The source cannot be seeked.");
        }

        if (!CheckState(out MediaEngineState state, MediaEngineState.Paused, MediaEngineState.Playing, MediaEngineState.Ended))
        {
            return PlaybackOperationResult.Failure($"Cannot seek when the state is {state}.");
        }

        SetState(MediaEngineState.Paused);
        _epoch.Change(epoch => epoch + 1, _ => Volatile.Write(ref _pendingSeekPosition, position.Ticks));
        WakeAll();
        return PlaybackOperationResult.Success;
    }

    private PlaybackOperationResult ReconcileCore()
    {
        if (_disposed)
        {
            return PlaybackOperationResult.Failure("The pipeline has already been disposed.");
        }

        if (_source is null || (_videoStreamInfo is null && _audioStreamInfo is null))
        {
            return PlaybackOperationResult.Success;
        }

        if (!CheckState(out MediaEngineState state, MediaEngineState.Paused, MediaEngineState.Playing, MediaEngineState.Ended))
        {
            return PlaybackOperationResult.Failure($"Cannot change the pipeline's components when the state is {state}.");
        }

        PlaybackOperationResult built = BuildChains(out ChainChanges changes);
        if (!built.IsSuccessful)
        {
            return built;
        }

        if (changes != ChainChanges.None)
        {
            TimeSpan position = _clock.Position;
            _epoch.Change(epoch => epoch + 1, _ => Volatile.Write(ref _pendingSeekPosition, position.Ticks));
            WakeAll();
        }

        return PlaybackOperationResult.Success;
    }

    private PlaybackOperationResult StopCore()
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
            _videoDecoder?.Dispose();
            _videoDecoder = null;
            _audioDecoder?.Dispose();
            _audioDecoder = null;
            _videoRenderer?.Dispose();
            _videoRenderer = null;
            _audioRenderer?.Component.Flush();
            _audioRenderer?.Dispose();
            _audioRenderer = null;
            _pendingVideoDecoder?.Dispose();
            _pendingVideoDecoder = null;
            _pendingAudioDecoder?.Dispose();
            _pendingAudioDecoder = null;
            _pendingVideoRenderer?.Dispose();
            _pendingVideoRenderer = null;
            _pendingAudioRenderer?.Dispose();
            _pendingAudioRenderer = null;
            _hasAudioChain = false;
            _hasVideoChain = false;
        }

        _videoTarget?.SurfaceSizeChanged -= VideoTarget_SurfaceSizeChanged;
        _videoTarget = null;

        _source?.Dispose();
        _source = null;

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

        _epoch.Change(epoch => epoch + 1);

        SetState(MediaEngineState.None, null);
        return PlaybackOperationResult.Success;
    }

    private void NotifySourceEnded(long epoch)
    {
        if (epoch != _epoch.Read())
        {
            return;
        }

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

        if (_endingEpoch != _epoch.Read())
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

    private PlaybackOperationResult Fail(string message, Exception? exception = null)
    {
        Logger.E(TAG, message, exception);
        SetState(MediaEngineState.Error, message);
        WakeAll();
        return PlaybackOperationResult.Failure(message, exception);
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

    private PlaybackOperationResult BuildChains(out ChainChanges changes)
    {
        changes = ChainChanges.None;

        IMediaSource? source = _source;
        if (source is null)
        {
            return PlaybackOperationResult.Failure("Nothing is open to build a chain for.");
        }

        ComponentResolver resolver = Components.Clone();
        VideoStreamInfo? videoStream = _videoStreamInfo;
        AudioStreamInfo? audioStream = _audioStreamInfo;

        lock (_componentLock)
        {
            ComponentSlot<IVideoRenderer>? videoRendererSlot = null;
            ComponentSlot<IVideoDecoder>? videoDecoderSlot = null;
            ComponentSlot<IAudioRenderer>? audioRendererSlot = null;
            ComponentSlot<IAudioDecoder>? audioDecoderSlot = null;

            try
            {
                if (videoStream is not null)
                {
                    IVideoRenderTarget? videoTarget = _videoTarget;
                    if (videoTarget is null)
                    {
                        return PlaybackOperationResult.Failure("The file has video but no presentation surface was supplied.");
                    }

                    ComponentSlot<IVideoRenderer>? runningRenderer = _pendingVideoRenderer ?? _videoRenderer;

                    long videoVersion = runningRenderer?.Version ?? 0;
                    PlaybackOperationResult resolvedRenderer = ResolveComponent(
                        resolver.ResolveVideoRenderers(),
                        runningRenderer?.Signature,
                        "video renderer",
                        videoVersion,
                        out videoRendererSlot);
                    if (!resolvedRenderer.IsSuccessful)
                    {
                        return resolvedRenderer;
                    }

                    IVideoRenderer? videoRenderer = videoRendererSlot?.Component;
                    if (videoRenderer is not null)
                    {
                        PlaybackOperationResult configured = videoRenderer.Configure(videoStream, videoTarget);
                        if (!configured.IsSuccessful)
                        {
                            return configured;
                        }
                    }

                    IGpuDevice? gpuDevice = (videoRenderer ?? runningRenderer?.Component)?.GpuDevice;

                    // A decoder is bound to the device it decodes on, and a renderer that is being replaced is a device
                    // that is being replaced — so whatever the signature says, the decoder that is running is not the
                    // decoder this renderer can take frames from.
                    ComponentSlot<IVideoDecoder>? runningVideoDecoder = videoRendererSlot is not null
                        ? null
                        : (_pendingVideoDecoder ?? _videoDecoder);

                    PlaybackOperationResult resolvedDecoder = ResolveComponent(
                        resolver.ResolveVideoDecoders(source, videoStream, gpuDevice, DecoderBufferedFrames),
                        runningVideoDecoder?.Signature,
                        "video decoder",
                        videoVersion,
                        out videoDecoderSlot);
                    if (!resolvedDecoder.IsSuccessful)
                    {
                        return resolvedDecoder;
                    }

                    if (videoDecoderSlot is not null && videoRendererSlot is not null)
                    {
                        long version = videoVersion + 1;
                        videoDecoderSlot.Version = version;
                        videoRendererSlot.Version = version;
                    }

                    if (videoDecoderSlot is not null)
                    {
                        _pendingVideoDecoder?.Dispose();
                        _pendingVideoDecoder = videoDecoderSlot;
                        videoDecoderSlot = null;
                        changes |= ChainChanges.VideoDecoder;
                    }

                    if (videoRendererSlot is not null)
                    {
                        _pendingVideoRenderer?.Dispose();
                        _pendingVideoRenderer = videoRendererSlot;
                        videoRendererSlot = null;
                        changes |= ChainChanges.VideoRenderer;
                    }
                }

                if (audioStream is not null)
                {
                    string? audioFailure = null;

                    ComponentSlot<IAudioRenderer>? runningAudioRenderer = _pendingAudioRenderer ?? _audioRenderer;
                    PlaybackOperationResult resolvedRenderer = ResolveComponent(
                        resolver.ResolveAudioRenderers(),
                        runningAudioRenderer?.Signature,
                        "audio renderer",
                        0,
                        out audioRendererSlot);
                    if (!resolvedRenderer.IsSuccessful)
                    {
                        audioFailure = resolvedRenderer.Message;
                    }
                    else if (audioRendererSlot is not null)
                    {
                        IAudioRenderer audioRenderer = audioRendererSlot.Component;
                        PlaybackOperationResult configured = audioRenderer.Configure(audioStream);
                        if (configured.IsSuccessful)
                        {
                            audioRenderer.Volume = Volatile.Read(ref _volume);
                            audioRenderer.IsMuted = Volatile.Read(ref _muted);
                        }
                        else
                        {
                            audioFailure = configured.Message;
                        }
                    }

                    if (audioFailure is null)
                    {
                        ComponentSlot<IAudioDecoder>? runningAudioDecoder = _pendingAudioDecoder ?? _audioDecoder;
                        PlaybackOperationResult resolvedDecoder = ResolveComponent(
                            resolver.ResolveAudioDecoders(source, audioStream),
                            runningAudioDecoder?.Signature,
                            "audio decoder",
                            0,
                            out audioDecoderSlot);
                        if (!resolvedDecoder.IsSuccessful)
                        {
                            audioFailure = resolvedDecoder.Message;
                        }
                    }

                    if (audioFailure is not null)
                    {
                        if (videoStream is null)
                        {
                            return PlaybackOperationResult.Failure(audioFailure);
                        }

                        RaiseWarning(runningAudioRenderer is not null
                            ? $"The audio output could not be changed: {audioFailure}"
                            : $"Audio is not playing: {audioFailure}");
                    }
                    else
                    {
                        if (audioDecoderSlot is not null)
                        {
                            _pendingAudioDecoder?.Dispose();
                            _pendingAudioDecoder = audioDecoderSlot;
                            audioDecoderSlot = null;
                            changes |= ChainChanges.AudioDecoder;
                        }

                        if (audioRendererSlot is not null)
                        {
                            _pendingAudioRenderer?.Dispose();
                            _pendingAudioRenderer = audioRendererSlot;
                            audioRendererSlot = null;
                            changes |= ChainChanges.AudioRenderer;
                        }
                    }
                }
            }
            finally
            {
                videoRendererSlot?.Dispose();
                videoDecoderSlot?.Dispose();
                audioRendererSlot?.Dispose();
                audioDecoderSlot?.Dispose();

                _hasAudioChain = _audioDecoder is not null || _pendingAudioDecoder is not null;
                _hasVideoChain = _videoDecoder is not null || _pendingVideoDecoder is not null;
            }
        }

        return PlaybackOperationResult.Success;
    }

    private static PlaybackOperationResult ResolveComponent<TComponent>(
        IEnumerable<IComponentCreator<TComponent>> creators,
        string? runningSignature,
        string role,
        long version,
        out ComponentSlot<TComponent>? slot) where TComponent : class, IDisposable
    {
        string error = $"No {role} could be offered.";
        slot = null;

        foreach (IComponentCreator<TComponent> creator in creators)
        {
            if (creator.Signature == runningSignature)
            {
                return PlaybackOperationResult.Success;
            }

            PlaybackOperationResult created = creator.Create(out TComponent? component);
            if (created.IsSuccessful && component is not null)
            {
                slot = new()
                {
                    Component = component,
                    Signature = creator.Signature,
                    Version = version,
                };
                return PlaybackOperationResult.Success;
            }

            error = created.IsSuccessful ? $"{creator.Signature} made no {role}." : created.Message;
        }

        return PlaybackOperationResult.Failure(error);
    }

    private sealed partial class ComponentSlot<TComponent> : IDisposable where TComponent : IDisposable
    {
        internal required TComponent Component { get; init; }
        internal required string Signature { get; init; }
        internal long Version { get; set; }

        public void Dispose()
        {
            Component.Dispose();
        }
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
        IMediaSource? source = _source;
        if (source is null)
        {
            return;
        }

        long workingEpoch = -1;
        long endingEpoch = -1;
        bool buffering = false;

        while (true)
        {
            long stamp = _readerNode.Stamp;
            if (_stopRequested)
            {
                return;
            }

            if (!CheckState(out _, MediaEngineState.Playing, MediaEngineState.Paused))
            {
                _readerNode.Wait(stamp);
                continue;
            }

            SwapAudioDecoder();

            long newEpoch = _epoch.Read();
            if (newEpoch != workingEpoch)
            {
                var target = TimeSpan.FromTicks(Volatile.Read(ref _pendingSeekPosition));
                workingEpoch = newEpoch;

                Volatile.Write(ref _readerTailEpoch, newEpoch);
                Interlocked.Exchange(ref _readerTailVideoTicks, -1);
                Interlocked.Exchange(ref _readerTailAudioTicks, -1);

                PlaybackOperationResult seeked;
                try
                {
                    seeked = source.Seek(target);
                }
                catch (Exception ex)
                {
                    seeked = PlaybackOperationResult.Failure(ex);
                }

                if (seeked.IsSuccessful)
                {
                    endingEpoch = -1;

                    try
                    {
                        _audioDecoder?.Component.Flush();
                    }
                    catch (Exception ex)
                    {
                        PostWarning($"The audio decoder could not be flushed: {ex.Message}");
                    }
                }
                else
                {
                    PostWarning($"The source refused to seek to {target:g}: {seeked.Message}");
                }

                continue;
            }

            if (endingEpoch == workingEpoch)
            {
                _readerNode.Wait(stamp);
                continue;
            }

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

            MediaPacket? packet = null;
            PlaybackOperationResult read;
            try
            {
                read = source.TryReadPacket(out packet);
            }
            catch (Exception ex)
            {
                read = PlaybackOperationResult.Failure(ex.Message);
            }

            PlaybackOperationResult routed = PlaybackOperationResult.Success;
            bool ending = false;
            bool handedOn = false;
            if (!read.IsSuccessful)
            {
                routed = read;
            }
            else if (packet is null)
            {
                ending = true;
            }
            else
            {
                routed = RoutePacket(packet, workingEpoch, out handedOn);
            }

            if (!handedOn)
            {
                packet?.Dispose();
            }

            if (!routed.IsSuccessful)
            {
                PostFailure(routed);
                return;
            }

            if (ending)
            {
                endingEpoch = workingEpoch;

                PlaybackOperationResult drained = DrainAudioDecoder(workingEpoch);
                if (!drained.IsSuccessful)
                {
                    PostWarning(drained.Message);
                }

                _packetQueue.Enqueue(new PacketEntry(null, workingEpoch));
                _audioQueue.Enqueue(new AudioEntry(null, workingEpoch));
                TryWakeVideoDecode();
                TryWakeAudioRender();

                long epochCopy = workingEpoch;
                Submit(() => NotifySourceEnded(epochCopy));
            }
        }
    }

    private void SwapAudioDecoder()
    {
        if (_pendingAudioDecoder is null)
        {
            return;
        }

        lock (_componentLock)
        {
            ComponentSlot<IAudioDecoder>? pending = _pendingAudioDecoder;
            if (pending is null)
            {
                return;
            }

            _pendingAudioDecoder = null;
            _audioDecoder?.Dispose();
            _audioDecoder = pending;
        }
    }

    private PlaybackOperationResult RoutePacket(MediaPacket packet, long epoch, out bool handedOn)
    {
        handedOn = false;

        IAudioDecoder? audioDecoder = _audioDecoder?.Component;
        if (audioDecoder is not null)
        {
            AudioStreamInfo? audioStreamInfo = _audioStreamInfo;
            if (audioStreamInfo is null)
            {
                return PlaybackOperationResult.Failure("The audio stream info is not available.");
            }

            if (packet.StreamIndex == audioStreamInfo.Index)
            {
                AdvanceReaderTail(epoch, packet.Timestamp, ref _readerTailAudioTicks);
                return DecodeAudio(audioDecoder, packet, epoch);
            }
        }

        VideoStreamInfo? videoStreamInfo = _videoStreamInfo;
        if (videoStreamInfo is not null && packet.StreamIndex == videoStreamInfo.Index)
        {
            AdvanceReaderTail(epoch, packet.Timestamp, ref _readerTailVideoTicks);

            _packetQueue.Enqueue(new PacketEntry(packet, epoch));
            handedOn = true;
            TryWakeVideoDecode();

            return PlaybackOperationResult.Success;
        }

        // Streams nobody selected, such as a second audio track, are simply skipped.
        return PlaybackOperationResult.Success;
    }

    private PlaybackOperationResult DecodeAudio(IAudioDecoder audioDecoder, MediaPacket packet, long epoch)
    {
        PlaybackOperationResult decoded;
        IReadOnlyList<RawAudioBuffer> buffers;
        try
        {
            decoded = audioDecoder.TryDecode(packet, out buffers);
        }
        catch (Exception ex)
        {
            return PlaybackOperationResult.Failure(ex.Message);
        }

        if (!decoded.IsSuccessful)
        {
            return decoded;
        }

        if (buffers is null)
        {
            return PlaybackOperationResult.Failure("The audio decoder returned no buffers.");
        }

        foreach (RawAudioBuffer buffer in buffers)
        {
            _audioQueue.Enqueue(new AudioEntry(buffer, epoch));
        }

        if (buffers.Count > 0)
        {
            TryWakeAudioRender();
        }

        return PlaybackOperationResult.Success;
    }

    private bool IsBuffered()
    {
        if (_hasAudioChain && _audioQueue.Samples < _audioMinimumSamples)
        {
            return false;
        }

        ConcurrentQueue<PacketEntry> packets = _packetQueue;
        if (_hasVideoChain && packets.Count < VideoPacketMinimum)
        {
            return false;
        }

        return true;
    }

    private bool NeedsBuffering()
    {
        if (_hasAudioChain && _audioQueue.Samples < _audioWakeSamples)
        {
            return true;
        }

        ConcurrentQueue<PacketEntry> packets = _packetQueue;
        if (_hasVideoChain && packets.Count < VideoPacketWake)
        {
            return true;
        }

        return false;
    }

    private PlaybackOperationResult DrainAudioDecoder(long epoch)
    {
        IAudioDecoder? decoder = _audioDecoder?.Component;
        if (decoder is null)
        {
            return PlaybackOperationResult.Success;
        }

        PlaybackOperationResult drained;
        IReadOnlyList<RawAudioBuffer> buffers;
        try
        {
            drained = decoder.Drain(out buffers);
        }
        catch (Exception ex)
        {
            return PlaybackOperationResult.Failure(ex.Message);
        }

        if (!drained.IsSuccessful)
        {
            return drained;
        }

        if (buffers is null)
        {
            return PlaybackOperationResult.Failure("The audio decoder returned no buffers.");
        }

        bool handedOver = false;
        foreach (RawAudioBuffer buffer in buffers)
        {
            _audioQueue.Enqueue(new AudioEntry(buffer, epoch));
            handedOver = true;
        }

        if (handedOver)
        {
            TryWakeAudioRender();
        }

        return PlaybackOperationResult.Success;
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
        long workingEpoch = -1;
        long componentVersion = -1;
        bool awaitingKeyFrame = false;
        ConcurrentQueue<PacketEntry> queue = _packetQueue;

        while (true)
        {
            long stamp = _videoDecodeNode.Stamp;
            if (_stopRequested)
            {
                return;
            }

            if (SwapVideoDecoder() is ComponentSlot<IVideoDecoder> pendingDecoder)
            {
                componentVersion = pendingDecoder.Version;

                // A decoder that has just been opened has no reference picture: what is queued was decoded against
                // a stream the new one has not seen, so it starts at the next frame the encoder made self-contained.
                awaitingKeyFrame = true;
                continue;
            }

            long epoch = _epoch.Read();
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;

                try
                {
                    _videoDecoder?.Component.Flush();
                }
                catch (Exception exception)
                {
                    PostFailure(exception.Message, exception);
                    return;
                }

                continue;
            }

            if (!HasRoomForVideoFrame())
            {
                _videoDecodeNode.Wait(stamp);
                continue;
            }

            if (!queue.TryDequeue(out PacketEntry entry))
            {
                _videoDecodeNode.Wait(stamp);
                continue;
            }

            TryWakeReader();

            MediaPacket? packet = entry.Packet;
            long packetEpoch = entry.Epoch;

            using (packet)
            {
                if (workingEpoch != packetEpoch)
                {
                    continue;
                }

                if (packet is null)
                {
                    PlaybackOperationResult drained = DrainVideoDecoder(packetEpoch, componentVersion);
                    if (!drained.IsSuccessful)
                    {
                        PostWarning(drained.Message);
                    }

                    _frameQueue.Enqueue(new FrameEntry(null, epoch, componentVersion));
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

                if (_videoDecoder is null)
                {
                    continue;
                }

                PlaybackOperationResult decoded = DecodeVideo(packet, out IReadOnlyList<RawVideoFrame>? frames);
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

                QueueFrames(frames, packetEpoch, componentVersion);
            }
        }
    }

    private ComponentSlot<IVideoDecoder>? SwapVideoDecoder()
    {
        if (_pendingVideoDecoder is null)
        {
            return null;
        }

        lock (_componentLock)
        {
            ComponentSlot<IVideoDecoder>? pending = _pendingVideoDecoder;
            if (pending is null)
            {
                return null;
            }

            _pendingVideoDecoder = null;
            _videoDecoder?.Dispose();
            _videoDecoder = pending;
            return pending;
        }
    }

    private PlaybackOperationResult DecodeVideo(MediaPacket packet, out IReadOnlyList<RawVideoFrame>? frames)
    {
        frames = null;

        IVideoDecoder? videoDecoder = _videoDecoder?.Component;
        if (videoDecoder is null)
        {
            return PlaybackOperationResult.Success;
        }

        long startTicks = Stopwatch.GetTimestamp();
        PlaybackOperationResult decoded;
        IReadOnlyList<RawVideoFrame>? produced = null;
        try
        {
            decoded = videoDecoder.TryDecode(packet, out produced);
        }
        catch (Exception ex)
        {
            decoded = PlaybackOperationResult.Failure(ex);
        }

        Interlocked.Add(ref _decodeTicks, Stopwatch.GetTimestamp() - startTicks);

        if (!decoded.IsSuccessful || produced is null)
        {
            return decoded;
        }

        frames = produced;
        Interlocked.Add(ref _decodedFrameCount, produced.Count);
        return PlaybackOperationResult.Success;
    }

    private PlaybackOperationResult DrainVideoDecoder(long epoch, long componentVersion)
    {
        IVideoDecoder? decoder = _videoDecoder?.Component;
        if (decoder is null)
        {
            return PlaybackOperationResult.Success;
        }

        PlaybackOperationResult drained;
        IReadOnlyList<RawVideoFrame> frames;
        try
        {
            drained = decoder.Drain(out frames);
        }
        catch (Exception ex)
        {
            return PlaybackOperationResult.Failure(ex.Message);
        }

        if (!drained.IsSuccessful)
        {
            return drained;
        }

        if (frames is null)
        {
            return PlaybackOperationResult.Failure("The video decoder returned no frames.");
        }

        QueueFrames(frames, epoch, componentVersion);
        return PlaybackOperationResult.Success;
    }

    private void QueueFrames(IReadOnlyList<RawVideoFrame> frames, long epoch, long componentVersion)
    {
        bool fastExit = false;

        foreach (RawVideoFrame frame in frames)
        {
            if (fastExit || _epoch.Read() != epoch)
            {
                fastExit = true;
                frame.Dispose();
                continue;
            }

            _frameQueue.Enqueue(new FrameEntry(frame, epoch, componentVersion));
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
        bool deviceFailed = false;
        long workingEpoch = -1;
        long componentVersion = -1;
        long firstFrameEpoch = -1;
        long firstFrameTicks = -1;
        long endingEpoch = -1;
        ConcurrentQueue<FrameEntry> queue = _frameQueue;

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

            if (SwapVideoRenderer() is ComponentSlot<IVideoRenderer> pendingRenderer)
            {
                componentVersion = pendingRenderer.Version;
                deviceFailed = false;
                continue;
            }

            long epoch = _epoch.Read();
            bool playing = CheckState(out MediaEngineState state, MediaEngineState.Playing);

            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;
                Interlocked.Exchange(ref _pictureTicks, -1);
            }

            IVideoRenderer? renderer = _videoRenderer?.Component;
            if (!deviceFailed && renderer is not null && state is MediaEngineState.Paused or MediaEngineState.Ended)
            {
                PlaybackOperationResult refreshed;
                try
                {
                    refreshed = renderer.RefreshSurface();
                }
                catch (Exception ex)
                {
                    refreshed = PlaybackOperationResult.Failure(ex);
                }

                if (!refreshed.IsSuccessful)
                {
                    PostWarning(refreshed.Message);
                    deviceFailed = true;
                }
            }

            FrameEntry? entry = queue.TryPeek(out FrameEntry head) ? head : null;
            TimeSpan position = _clock.Position;
            bool offeringTheStill = state == MediaEngineState.Paused && firstFrameEpoch != epoch && endingEpoch != epoch;

            bool starved = _videoDecoder is not null && endingEpoch != epoch && entry is null;
            starved = starved && !IsStreamSpent(epoch, position, ref _readerTailVideoTicks);
            starved = starved && (firstFrameEpoch != epoch || IsVideoStarved(position));

            lock (_clockLock)
            {
                _videoStarved = starved;
                StartOrPauseClock(epoch, state);
            }

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

            if (entry.Value.Epoch != epoch || entry.Value.ComponentVersion != componentVersion)
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

            FrameSyncAction syncAction = Synchronizer.Evaluate(frame.Timestamp, position);

            if (playing && _audioDecoder is null)
            {
                TimeSpan anchor = firstFrameEpoch == epoch ? TimeSpan.FromTicks(firstFrameTicks) : frame.Timestamp;

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

                if (!deviceFailed)
                {
                    PlaybackOperationResult shown = RenderVideoFrame(frame);
                    if (!shown.IsSuccessful)
                    {
                        PostWarning(shown.Message);
                        deviceFailed = true;
                    }
                }

                if (firstFrameEpoch != epoch)
                {
                    firstFrameEpoch = epoch;
                    firstFrameTicks = frame.Timestamp.Ticks;
                }
            }
        }
    }

    private ComponentSlot<IVideoRenderer>? SwapVideoRenderer()
    {
        if (_pendingVideoRenderer is null)
        {
            return null;
        }

        lock (_componentLock)
        {
            ComponentSlot<IVideoRenderer>? pending = _pendingVideoRenderer;
            if (pending is null)
            {
                return null;
            }

            _pendingVideoRenderer = null;
            _videoRenderer?.Dispose();
            _videoRenderer = pending;
            return pending;
        }
    }

    private PlaybackOperationResult RenderVideoFrame(RawVideoFrame frame)
    {
        IVideoRenderer? renderer = _videoRenderer?.Component;
        if (renderer is null)
        {
            return PlaybackOperationResult.Failure("The video renderer is not available.");
        }

        long startTicks = Stopwatch.GetTimestamp();
        PlaybackOperationResult rendered;
        try
        {
            rendered = renderer.Render(frame);
        }
        catch (Exception ex)
        {
            rendered = PlaybackOperationResult.Failure(ex.Message);
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

        return PlaybackOperationResult.Success;
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
        bool deviceAvailable = false;
        bool reportedRunningAhead = false;
        long workingEpoch = -1;
        long endingEpoch = -1;
        long playedOutEpoch = -1;
        TimeSpan? clockOffset = null;
        TimeSpan? tailBufferPosition = null;
        AudioQueue queue = _audioQueue;
        IAudioRenderer? renderer = _audioRenderer?.Component;

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
                goto CleanUp;
            }

            if (SwapAudioRenderer() is ComponentSlot<IAudioRenderer> pendingRenderer)
            {
                IAudioRenderer? replaced = renderer;
                renderer = pendingRenderer.Component;
                deviceAvailable = true;
                reportedRunningAhead = false;
                clockOffset = null;

                ReleaseInFlightAudio();
                replaced?.Flush();
                replaced?.Dispose();

                continue;
            }

            long epoch = _epoch.Read();
            bool playing = CheckState(out MediaEngineState state, MediaEngineState.Playing);
            bool epochChanged = workingEpoch != epoch;

            if (epochChanged)
            {
                workingEpoch = epoch;
                tailBufferPosition = null;
                reportedRunningAhead = false;
                renderer?.Flush();
            }

            if (epochChanged || !deviceAvailable)
            {
                clockOffset = null;
                ReleaseInFlightAudio();
            }

            lock (_clockLock)
            {
                if (epochChanged)
                {
                    _audioStarved = true;
                }

                StartOrPauseClock(epoch, state);

                if (deviceAvailable && renderer is not null)
                {
                    IPlaybackClock deviceClock = renderer.Clock;
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

            if (!playing)
            {
                _audioRenderNode.Wait(stamp);
                continue;
            }

            bool hasDeviceAudio = deviceAvailable && renderer is not null && renderer.QueuedSampleCount > 0;
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

            if (deviceAvailable && renderer is not null)
            {
                IPlaybackClock deviceClock = renderer.Clock;

                if (hasDeviceAudio && clockOffset is TimeSpan offset)
                {
                    SyncClock(_clock, deviceClock, offset);
                }

                if (inFlightSubmit is not null)
                {
                    if (!inFlightSubmit.MoveNext())
                    {
                        PlaybackOperationResult submitted = inFlightSubmit.Result;
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

            if (!deviceAvailable && tailBufferPosition.HasValue && position < tailBufferPosition.Value)
            {
                // Audio position must not run ahead of the clock position.
                _audioRenderNode.Wait(stamp, AudioIdleTimeoutMilliseconds);
                continue;
            }

            if (!queue.TryDequeue(out AudioEntry entry))
            {
                continue;
            }

            TryWakeReader();

            RawAudioBuffer? buffer = entry.Buffer;
            long bufferEpoch = entry.Epoch;

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

            tailBufferPosition = buffer.Timestamp + buffer.Duration;

            lock (_clockLock)
            {
                _audioStarved = false;

                bool clockChanged = AnchorClock(epoch, buffer.Timestamp);
                if (deviceAvailable && renderer is not null && (clockChanged || clockOffset is null))
                {
                    clockOffset = renderer.Clock.Position - buffer.Timestamp;
                }

                StartOrPauseClock(epoch, state);
            }

            if (deviceAvailable && renderer is not null)
            {
                inFlightSubmit = renderer.Submit(buffer);
                inFlightBuffer = buffer;
            }
            else
            {
                buffer.Dispose();
            }
        }

    CleanUp:
        ReleaseInFlightAudio();
    }

    private ComponentSlot<IAudioRenderer>? SwapAudioRenderer()
    {
        if (_pendingAudioRenderer is null)
        {
            return null;
        }

        lock (_componentLock)
        {
            ComponentSlot<IAudioRenderer>? pending = _pendingAudioRenderer;
            if (pending is null)
            {
                return null;
            }

            _pendingAudioRenderer = null;
            _audioRenderer = pending;
            return pending;
        }
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
            Logger.E(TAG, ex);
            return;
        }

        TimeSpan expected = deviceTime - offset;
        if (Math.Abs((expected - localTime).TotalMilliseconds) <= ClockSyncToleranceMilliseconds)
        {
            return;
        }

        lock (_clockLock)
        {
            if (!clock.IsRunning)
            {
                // Sync is only valid when the clock is running.
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

    private bool IsEnding(long epoch)
    {
        return Interlocked.Read(ref _endingEpoch) == epoch;
    }

    private void StartOrPauseClock(long epoch, MediaEngineState state)
    {
        bool anchored = _anchoredEpoch == epoch;
        if (!anchored)
        {
            // This sync is inaccurate, but it is the best that can be done for now.
            _clock.SetPosition(TimeSpan.FromTicks(Volatile.Read(ref _pendingSeekPosition)));
        }

        bool start = state == MediaEngineState.Playing && !_videoStarved && !_audioStarved;
        if (start == _clock.IsRunning)
        {
            return;
        }

        if (start && !anchored)
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

    private bool AnchorClock(long epoch, TimeSpan mediaTimestamp)
    {
        if (_anchoredEpoch == epoch)
        {
            return false;
        }

        _anchoredEpoch = epoch;
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

    private void PostFailure(PlaybackOperationResult result)
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
