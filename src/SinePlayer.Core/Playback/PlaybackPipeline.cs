// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

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
using SinePlayer.Core.Playback.Internal;
using SinePlayer.Core.Playback.Internal.Timing;

namespace SinePlayer.Core.Playback;

public sealed partial class PlaybackPipeline : IDisposable
{
    #region Constants

    private const string TAG = nameof(PlaybackPipeline);

    private const int FrameQueueCapacity = 8;
    private const int VideoPacketMinimum = 64;
    private const int VideoPacketWake = VideoPacketMinimum / 2;

    private const int AudioMinimumMilliseconds = 500;
    private const int VideoStarvationMilliseconds = 200;
    private const int VideoStarvationResumeMilliseconds = 100;
    private const int VideoIdleTimeoutMilliseconds = 50;
    private const int AudioIdleTimeoutMilliseconds = 5;

    private const int StalledAudioDeviceCheckIntervalMilliseconds = 100;
    private const int StalledAudioDeviceThresholdMilliseconds = 1000;
    private static readonly long StalledAudioDeviceCheckIntervalTicks = Stopwatch.Frequency * StalledAudioDeviceCheckIntervalMilliseconds / 1000;
    private static readonly long StalledAudioDeviceThresholdTicks = Stopwatch.Frequency * StalledAudioDeviceThresholdMilliseconds / 1000;
    private const string WarningAudioDeviceStalled = "The audio device is stalled.";

    #endregion

    #region Variables

    private readonly ITaskDispatcher _playbackQueue = TaskDispatcher.Factory.NewQueue("Pipeline - Playback");

    private readonly ISourceProviderFactory _sourceFactory;
    private readonly IVideoDecoderFactory _videoDecoderFactory;
    private readonly IAudioDecoderFactory _audioDecoderFactory;
    private readonly IComponentFactory<IVideoRenderer> _videoRendererFactory;
    private readonly IComponentFactory<IAudioRenderer> _audioRendererFactory;
    private readonly ISynchronizer _synchronizer;

    private MediaInfo? _info;
    private VideoStreamInfo? _videoStreamInfo;
    private AudioStreamInfo? _audioStreamInfo;
    private long _audioMinimumSamples;
    private long _audioWakeSamples;
    private double _volume = 1.0;
    private bool _muted;

    private IVideoRenderTarget? _videoTarget;
    private IMediaSource? _source;
    private IVideoDecoder? _videoDecoder;
    private IAudioDecoder? _audioDecoder;
    private IVideoRenderer? _videoRenderer;
    private IAudioRenderer? _audioRenderer;

    private MediaQueue<RawAudioBuffer>? _audioQueue;
    private MediaQueue<MediaPacket>? _packetQueue;
    private MediaQueue<RawVideoFrame>? _frameQueue;

    private Thread? _readerThread;
    private Thread? _videoDecodeThread;
    private Thread? _videoRenderThread;
    private Thread? _audioRenderThread;

    private bool _disposed;
    private volatile MediaEngineState _state = MediaEngineState.None;
    private volatile string? _error;
    private volatile bool _stopRequested;

    private readonly SeqLock<long> _epochs = new(0);
    private readonly PipelineNode _readerNode = new();
    private readonly PipelineNode _videoDecodeNode = new();
    private readonly PipelineNode _videoRenderNode = new();
    private readonly PipelineNode _audioRenderNode = new();

    private long _pendingSeekPosition;
    private long _endingEpoch = -1;
    private long _videoRenderEndedEpoch = -1;
    private long _audioRenderEndedEpoch = -1;

    private readonly Lock _clockLock = new();
    private volatile IPlaybackClock? _clock;
    private long _anchoredEpoch = -1;
    private volatile bool _videoStarved;
    private volatile bool _audioStarved;

    private long _presentedFrameCount;
    private long _droppedFrameCount;
    private long _displayedFrameCount = -1;
    private long _decodedFrameCount;
    private long _decodeTicks;
    private long _renderTicks;
    private long _pictureTicks;
    private long _tailDecodedFrameTicks = -1;

    private TimeSpan _devicePosition;
    private long _deviceMovedAt;
    private long _deviceCheckedAt;
    private long _deviceWatchEpoch = -1;
    private bool _deviceStallReported;

    public PlaybackPipeline(
        ISourceProviderFactory sourceFactory,
        IVideoDecoderFactory videoDecoderFactory,
        IComponentFactory<IVideoRenderer> videoRendererFactory,
        IAudioDecoderFactory audioDecoderFactory,
        IComponentFactory<IAudioRenderer> audioRendererFactory,
        ISynchronizer synchronizer)
    {
        _sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
        _videoDecoderFactory = videoDecoderFactory ?? throw new ArgumentNullException(nameof(videoDecoderFactory));
        _videoRendererFactory = videoRendererFactory ?? throw new ArgumentNullException(nameof(videoRendererFactory));
        _audioDecoderFactory = audioDecoderFactory ?? throw new ArgumentNullException(nameof(audioDecoderFactory));
        _audioRendererFactory = audioRendererFactory ?? throw new ArgumentNullException(nameof(audioRendererFactory));
        _synchronizer = synchronizer ?? throw new ArgumentNullException(nameof(synchronizer));
    }

    #endregion

    #region Public API

    public event Action<MediaEngineState, string?>? StateChanged;
    public event Action<string>? WarningRaised;

    public MediaEngineState State => _state;

    public string? Error => _error;

    public MediaInfo? MediaInfo => _info;

    public bool HasVideo => _videoStreamInfo is not null;

    public bool HasAudio => _audioStreamInfo is not null;

    public TimeSpan Duration => _info?.Duration ?? TimeSpan.Zero;

    public TimeSpan Position => _clock?.Position ?? TimeSpan.Zero;

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
            IAudioRenderer? renderer = Volatile.Read(ref _audioRenderer);
            renderer?.Volume = Volatile.Read(ref _volume);
        }
    }

    public bool IsMuted
    {
        get => Volatile.Read(ref _muted);
        set
        {
            Volatile.Write(ref _muted, value);
            IAudioRenderer? renderer = Volatile.Read(ref _audioRenderer);
            renderer?.IsMuted = value;
        }
    }

    internal TimeSpan LastDecodedTimestamp
    {
        get
        {
            long ticks = Interlocked.Read(ref _tailDecodedFrameTicks);
            return ticks > 0 ? TimeSpan.FromTicks(ticks) : TimeSpan.Zero;
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
            if (ticks == 0 || PresentedFrameCount == 0)
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

        StopCore();
        SetState(MediaEngineState.Opening);

        PlaybackOperationResult result = PreparePlayback(locator, videoTarget);
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

    private PlaybackOperationResult PreparePlayback(MediaLocator locator, IVideoRenderTarget? videoTarget)
    {
        PlaybackOperationResult opened = _sourceFactory.TryOpen(locator, out IMediaSource? source);
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

        if (audioStreamInfo is not null)
        {
            PlaybackOperationResult prepared = PrepareAudio(source, audioStreamInfo);
            if (!prepared.IsSuccessful)
            {
                if (videoStreamInfo is null)
                {
                    return prepared;
                }

                RaiseWarning($"Audio is not playing: {prepared.Message}");
                _audioStreamInfo = null;
            }
        }

        if (videoStreamInfo is not null)
        {
            if (videoTarget is null)
            {
                return PlaybackOperationResult.Failure("The file has video but no presentation surface was supplied.");
            }

            PlaybackOperationResult prepared = PrepareVideo(videoStreamInfo, videoTarget);
            if (!prepared.IsSuccessful)
            {
                return prepared;
            }
        }

        lock (_clockLock)
        {
            _clock ??= new StopwatchClock();
        }

        Volatile.Write(ref _pendingSeekPosition, 0);

        return PlaybackOperationResult.Success;
    }

    private PlaybackOperationResult PrepareAudio(IMediaSource source, AudioStreamInfo stream)
    {
        PlaybackOperationResult rendererResult = _audioRendererFactory.TryCreate(out IAudioRenderer? renderer);
        if (!rendererResult.IsSuccessful || renderer is null)
        {
            return rendererResult.IsSuccessful ? PlaybackOperationResult.Failure("No audio renderer was created.") : rendererResult;
        }

        PlaybackOperationResult created = _audioDecoderFactory.TryCreate(source, stream, DecoderOptions.Default, out IAudioDecoder? decoder);
        if (!created.IsSuccessful || decoder is null)
        {
            renderer.Dispose();
            return created.IsSuccessful ? PlaybackOperationResult.Failure("No audio decoder was created.") : created;
        }

        PlaybackOperationResult configured = renderer.Configure(stream);
        if (!configured.IsSuccessful)
        {
            decoder.Dispose();
            renderer.Dispose();
            return configured;
        }

        _audioDecoder = decoder;
        _audioRenderer = renderer;
        renderer.Volume = Volatile.Read(ref _volume);
        renderer.IsMuted = Volatile.Read(ref _muted);

        lock (_clockLock)
        {
            _clock = renderer.Clock;
        }

        _audioMinimumSamples = Math.Max(1, (long)stream.SampleRate * AudioMinimumMilliseconds / 1000);
        _audioWakeSamples = _audioMinimumSamples / 2;
        _audioQueue?.Dispose();
        _audioQueue = new MediaQueue<RawAudioBuffer>(weigh: buffer => Math.Max(1, buffer.SampleCount));

        return PlaybackOperationResult.Success;
    }

    private PlaybackOperationResult PrepareVideo(VideoStreamInfo stream, IVideoRenderTarget target)
    {
        IMediaSource? source = _source;
        if (source is null)
        {
            return PlaybackOperationResult.Failure("Nothing is open.");
        }

        PlaybackOperationResult rendererResult = _videoRendererFactory.TryCreate(out IVideoRenderer? renderer);
        if (!rendererResult.IsSuccessful || renderer is null)
        {
            return rendererResult.IsSuccessful ? PlaybackOperationResult.Failure("No video renderer was created.") : rendererResult;
        }

        PlaybackOperationResult created = _videoDecoderFactory.TryCreate(source, stream, DecoderOptions.Default, out IVideoDecoder? decoder);
        if (!created.IsSuccessful || decoder is null)
        {
            renderer.Dispose();
            return created.IsSuccessful ? PlaybackOperationResult.Failure("No video decoder was created.") : created;
        }

        PlaybackOperationResult configured = renderer.Configure(stream, target);
        if (!configured.IsSuccessful)
        {
            decoder.Dispose();
            renderer.Dispose();
            return configured;
        }

        _videoRenderer = renderer;
        _videoDecoder = decoder;

        if (!ReferenceEquals(_videoTarget, target))
        {
            _videoTarget?.SurfaceSizeChanged -= VideoTarget_SurfaceSizeChanged;
            _videoTarget = target;
            target.SurfaceSizeChanged += VideoTarget_SurfaceSizeChanged;
        }

        _packetQueue?.Dispose();
        _packetQueue = new MediaQueue<MediaPacket>();

        _frameQueue?.Dispose();
        _frameQueue = new MediaQueue<RawVideoFrame>();

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
        if (_packetQueue is null || _videoDecodeThread is not null)
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
        if (_frameQueue is null || _videoRenderThread is not null)
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
        if (_audioQueue is null || _audioRenderThread is not null)
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

        _epochs.Change(epoch => epoch + 1, _ =>
        {
            Volatile.Write(ref _pendingSeekPosition, position.Ticks);

            _packetQueue?.Clear();
            _frameQueue?.Clear();
            _audioQueue?.Clear();
        });

        WakeAll();
        return PlaybackOperationResult.Success;
    }

    private PlaybackOperationResult StopCore()
    {
        _stopRequested = true;
        WakeAll();

        _audioQueue?.Dispose();
        _audioQueue = null;
        _packetQueue?.Dispose();
        _packetQueue = null;
        _frameQueue?.Dispose();
        _frameQueue = null;

        _readerThread?.Join();
        _readerThread = null;
        _videoDecodeThread?.Join();
        _videoDecodeThread = null;
        _videoRenderThread?.Join();
        _videoRenderThread = null;
        _audioRenderThread?.Join();
        _audioRenderThread = null;

        _videoDecoder?.Dispose();
        _videoDecoder = null;
        _audioDecoder?.Dispose();
        _audioDecoder = null;
        _videoRenderer?.Dispose();
        _videoRenderer = null;

        IAudioRenderer? audioRenderer = _audioRenderer;
        _audioRenderer = null;
        audioRenderer?.Flush();
        audioRenderer?.Dispose();

        _videoTarget?.SurfaceSizeChanged -= VideoTarget_SurfaceSizeChanged;
        _videoTarget = null;

        _source?.Dispose();
        _source = null;

        lock (_clockLock)
        {
            _clock = null;
            _anchoredEpoch = -1;
            _videoStarved = false;
            _audioStarved = false;
        }

        _videoStreamInfo = null;
        _audioStreamInfo = null;
        _info = null;
        Interlocked.Exchange(ref _endingEpoch, -1);
        _videoRenderEndedEpoch = -1;
        _audioRenderEndedEpoch = -1;
        _devicePosition = TimeSpan.Zero;
        _deviceMovedAt = 0;
        _deviceCheckedAt = 0;
        _deviceWatchEpoch = -1;
        _deviceStallReported = false;
        Interlocked.Exchange(ref _presentedFrameCount, 0);
        Interlocked.Exchange(ref _droppedFrameCount, 0);
        Interlocked.Exchange(ref _displayedFrameCount, -1);
        Interlocked.Exchange(ref _decodedFrameCount, 0);
        Interlocked.Exchange(ref _decodeTicks, 0);
        Interlocked.Exchange(ref _renderTicks, 0);
        Interlocked.Exchange(ref _pictureTicks, 0);
        Interlocked.Exchange(ref _tailDecodedFrameTicks, -1);

        _epochs.Change(epoch => epoch + 1);

        SetState(MediaEngineState.None, null);
        return PlaybackOperationResult.Success;
    }

    private void NotifySourceEnded(long epoch)
    {
        if (epoch != _epochs.Read())
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

        if (_endingEpoch != _epochs.Read())
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

        while (!_stopRequested)
        {
            long stamp = _readerNode.Stamp;

            if (!CheckState(out _, MediaEngineState.Playing, MediaEngineState.Paused))
            {
                _readerNode.Wait(stamp);
                continue;
            }

            long newEpoch = _epochs.Read();
            if (newEpoch != workingEpoch)
            {
                var target = TimeSpan.FromTicks(Volatile.Read(ref _pendingSeekPosition));
                workingEpoch = newEpoch;

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
                        _audioDecoder?.Flush();
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
                    PostFailure(drained);
                    return;
                }

                _packetQueue?.EndStream(workingEpoch);
                _audioQueue?.EndStream(workingEpoch);
                TryWakeVideoDecode();
                TryWakeAudioRender();

                long epochCopy = workingEpoch;
                Submit(() => NotifySourceEnded(epochCopy));
            }
        }
    }

    private PlaybackOperationResult RoutePacket(MediaPacket packet, long epoch, out bool handedOn)
    {
        handedOn = false;

        IAudioDecoder? audioDecoder = _audioDecoder;
        if (audioDecoder is not null)
        {
            AudioStreamInfo? audioStreamInfo = _audioStreamInfo;
            if (audioStreamInfo is null)
            {
                return PlaybackOperationResult.Failure("The audio stream info is not available.");
            }

            if (packet.StreamIndex == audioStreamInfo.Index)
            {
                return DecodeAudio(audioDecoder, packet, epoch);
            }
        }

        if (_videoDecoder is not null)
        {
            VideoStreamInfo? videoStreamInfo = _videoStreamInfo;
            if (videoStreamInfo is null)
            {
                return PlaybackOperationResult.Failure("The video stream info is not available.");
            }

            if (packet.StreamIndex == videoStreamInfo.Index)
            {
                MediaQueue<MediaPacket>? packetQueue = _packetQueue;
                if (packetQueue is null)
                {
                    return PlaybackOperationResult.Failure("The video packet queue is not available.");
                }

                if (packetQueue.TryEnqueue(packet, epoch))
                {
                    handedOn = true;
                    TryWakeVideoDecode();
                }

                return PlaybackOperationResult.Success;
            }
        }

        // Streams nobody selected, such as a second audio track, are simply skipped.
        return PlaybackOperationResult.Success;
    }

    private PlaybackOperationResult DecodeAudio(IAudioDecoder audioDecoder, MediaPacket packet, long epoch)
    {
        PlaybackOperationResult decoded = audioDecoder.TryDecode(packet, out IReadOnlyList<RawAudioBuffer> buffers);
        if (!decoded.IsSuccessful)
        {
            return decoded;
        }

        MediaQueue<RawAudioBuffer>? audioQueue = _audioQueue;
        if (audioQueue is null)
        {
            return PlaybackOperationResult.Failure("The audio queue is not available.");
        }

        foreach (RawAudioBuffer buffer in buffers)
        {
            if (!audioQueue.TryEnqueue(buffer, epoch))
            {
                buffer.Dispose();
            }
        }

        if (buffers.Count > 0)
        {
            TryWakeAudioRender();
        }

        return PlaybackOperationResult.Success;
    }

    private bool IsBuffered()
    {
        MediaQueue<RawAudioBuffer>? audio = _audioQueue;
        if (audio is not null && audio.Weight < _audioMinimumSamples)
        {
            return false;
        }

        MediaQueue<MediaPacket>? packets = _packetQueue;
        if (packets is not null && packets.Count < VideoPacketMinimum)
        {
            return false;
        }

        return true;
    }

    private bool NeedsBuffering()
    {
        MediaQueue<RawAudioBuffer>? audio = _audioQueue;
        if (audio is not null && audio.Weight < _audioWakeSamples)
        {
            return true;
        }

        MediaQueue<MediaPacket>? packets = _packetQueue;
        if (packets is not null && packets.Count < VideoPacketWake)
        {
            return true;
        }

        return false;
    }

    private PlaybackOperationResult DrainAudioDecoder(long epoch)
    {
        IAudioDecoder? decoder = _audioDecoder;
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

        MediaQueue<RawAudioBuffer>? queue = _audioQueue;
        if (queue is null)
        {
            foreach (RawAudioBuffer buffer in buffers)
            {
                buffer.Dispose();
            }

            return PlaybackOperationResult.Success;
        }

        bool handedOver = false;
        foreach (RawAudioBuffer buffer in buffers)
        {
            if (queue.TryEnqueue(buffer, epoch))
            {
                handedOver = true;
            }
            else
            {
                buffer.Dispose();
            }
        }

        if (handedOver)
        {
            TryWakeAudioRender();
        }

        return PlaybackOperationResult.Success;
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

        MediaQueue<MediaPacket>? packets = _packetQueue;
        if (packets is null || !HasRoomForVideoFrame())
        {
            return;
        }

        if (packets.TryPeek(out _))
        {
            _videoDecodeNode.Wake();
        }
    }

    private void VideoDecodeCore()
    {
        long workingEpoch = -1;

        while (!_stopRequested)
        {
            MediaQueue<MediaPacket>? queue = _packetQueue;
            if (queue is null)
            {
                return;
            }

            long stamp = _videoDecodeNode.Stamp;

            long epoch = _epochs.Read();
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;

                try
                {
                    _videoDecoder?.Flush();
                }
                catch (Exception exception)
                {
                    PostFailure(exception.Message, exception);
                    return;
                }

                Interlocked.Exchange(ref _tailDecodedFrameTicks, -1);
                continue;
            }

            if (!HasRoomForVideoFrame())
            {
                _videoDecodeNode.Wait(stamp);
                continue;
            }

            if (!queue.TryDequeue(out MediaItem<MediaPacket>? entry))
            {
                if (queue.IsClosed)
                {
                    return;
                }

                _videoDecodeNode.Wait(stamp);
                continue;
            }

            TryWakeReader();

            MediaPacket? packet = entry.Item;
            long packetEpoch = entry.Epoch;

            using (packet)
            {
                if (workingEpoch != packetEpoch)
                {
                    continue;
                }

                if (entry.IsLast || packet is null)
                {
                    PlaybackOperationResult drained = DrainVideo(packetEpoch);
                    if (!drained.IsSuccessful)
                    {
                        PostFailure(drained);
                        return;
                    }

                    _videoDecodeNode.Wait(stamp);
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

                QueueFrames(frames, packetEpoch);
            }
        }
    }

    private PlaybackOperationResult DecodeVideo(MediaPacket packet, out IReadOnlyList<RawVideoFrame>? frames)
    {
        frames = null;

        IVideoDecoder? videoDecoder = _videoDecoder;
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

    private PlaybackOperationResult DrainVideo(long epoch)
    {
        IVideoDecoder? decoder = _videoDecoder;
        if (decoder is not null)
        {
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

            QueueFrames(frames, epoch);
        }

        _frameQueue?.EndStream(epoch);
        TryWakeVideoRender();

        return PlaybackOperationResult.Success;
    }

    private void QueueFrames(IReadOnlyList<RawVideoFrame> frames, long epoch)
    {
        bool fastExit = false;
        MediaQueue<RawVideoFrame>? frameQueue = _frameQueue;

        foreach (RawVideoFrame frame in frames)
        {
            if (fastExit || frameQueue is null || _epochs.Read() != epoch || !frameQueue.TryEnqueue(frame, epoch))
            {
                fastExit = true;
                frame.Dispose();
                continue;
            }

            Interlocked.Exchange(ref _tailDecodedFrameTicks, frame.Timestamp.Ticks);
        }

        if (!fastExit && frameQueue is not null)
        {
            TryWakeVideoRender();
        }
    }

    private bool HasRoomForVideoFrame()
    {
        MediaQueue<RawVideoFrame>? frames = _frameQueue;
        return frames is null || frames.Count < FrameQueueCapacity;
    }

    #endregion

    #region Video render

    private void TryWakeVideoRender()
    {
        MediaQueue<RawVideoFrame>? frames = _frameQueue;
        if (_stopRequested || frames is null || frames.TryPeek(out _))
        {
            _videoRenderNode.Wake();
        }
    }

    private void VideoRenderCore()
    {
        long showingEpoch = -1;
        long endingEpoch = -1;
        long clockEpoch = -1;
        long clockStamp = -1;

        while (!_stopRequested)
        {
            MediaQueue<RawVideoFrame>? queue = _frameQueue;
            if (queue is null)
            {
                return;
            }

            long stamp = _videoRenderNode.Stamp;
            long epoch = _epochs.Read();
            bool playing = CheckState(out MediaEngineState state, MediaEngineState.Playing);
            IPlaybackClock clock = _clock ?? throw new InvalidOperationException("The picture has no clock.");

            IVideoRenderer? renderer = _videoRenderer;
            if (renderer is not null && state is MediaEngineState.Paused or MediaEngineState.Ended)
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
                    PostFailure(refreshed);
                    return;
                }
            }

            MediaItem<RawVideoFrame>? entry = queue.TryPeek(out MediaItem<RawVideoFrame>? head) ? head : null;
            TimeSpan position = clock.Position;

            bool starved = _videoStarved;
            if (playing)
            {
                starved = IsVideoStarved(position, epoch, showingEpoch, entry is not null);
            }

            if (starved != _videoStarved || epoch != clockEpoch || stamp != clockStamp)
            {
                lock (_clockLock)
                {
                    _videoStarved = starved;
                    ApplyClock(clock, epoch, state);
                }

                clockEpoch = epoch;
                clockStamp = stamp;
            }

            bool offeringTheStill = state == MediaEngineState.Paused && showingEpoch != epoch && endingEpoch != epoch;
            if (!playing && !offeringTheStill)
            {
                _videoRenderNode.Wait(stamp);
                continue;
            }

            if (entry is null)
            {
                if (queue.IsClosed)
                {
                    return;
                }

                _videoRenderNode.Wait(stamp, playing ? VideoIdleTimeoutMilliseconds : Timeout.Infinite);
                continue;
            }

            RawVideoFrame? frame = entry.Item;

            if (playing && frame is not null && entry.Epoch == epoch
                && _synchronizer.Evaluate(frame.Timestamp, position) == FrameSyncAction.Wait)
            {
                int remaining = (int)Math.Clamp((frame.Timestamp - position).TotalMilliseconds, 1, VideoIdleTimeoutMilliseconds);
                _videoRenderNode.Wait(stamp, remaining);
                continue;
            }

            if (!queue.TryDequeue(entry))
            {
                continue;
            }

            TryWakeVideoDecode();

            using (frame)
            {
                if (entry.Epoch != epoch)
                {
                    continue;
                }

                if (entry.IsLast || frame is null)
                {
                    if (endingEpoch != entry.Epoch)
                    {
                        endingEpoch = entry.Epoch;

                        long epochCopy = entry.Epoch;
                        Submit(() => NotifyVideoRenderEnded(epochCopy));
                    }

                    continue;
                }

                if (playing)
                {
                    if (_audioDecoder is null)
                    {
                        AnchorClock(clock, frame.Timestamp, entry.Epoch);
                    }

                    if (Volatile.Read(ref _anchoredEpoch) == entry.Epoch
                        && _synchronizer.Evaluate(frame.Timestamp, position) == FrameSyncAction.Drop
                        && queue.TryPeek(out MediaItem<RawVideoFrame>? closer)
                        && closer.Item is not null
                        && closer.Epoch == entry.Epoch)
                    {
                        Interlocked.Increment(ref _droppedFrameCount);
                        continue;
                    }
                }

                PlaybackOperationResult shown = RenderVideoFrame(frame);
                if (!shown.IsSuccessful)
                {
                    PostFailure(shown);
                    return;
                }

                showingEpoch = entry.Epoch;
            }
        }
    }

    private PlaybackOperationResult RenderVideoFrame(RawVideoFrame frame)
    {
        IVideoRenderer? renderer = _videoRenderer;
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

    private bool IsVideoStarved(TimeSpan position, long epoch, long showingEpoch, bool hasEntry)
    {
        if (IsEnding(epoch))
        {
            return false;
        }

        if (showingEpoch != epoch && !hasEntry)
        {
            return true;
        }

        long tailTicks = Interlocked.Read(ref _tailDecodedFrameTicks);
        if (tailTicks < 0 || Volatile.Read(ref _anchoredEpoch) != epoch)
        {
            return false;
        }

        TimeSpan behind = position - TimeSpan.FromTicks(tailTicks);
        return _videoStarved
            ? behind > TimeSpan.FromMilliseconds(VideoStarvationResumeMilliseconds)
            : behind > TimeSpan.FromMilliseconds(VideoStarvationMilliseconds);
    }

    #endregion

    #region Audio render

    private void TryWakeAudioRender()
    {
        MediaQueue<RawAudioBuffer>? audio = _audioQueue;
        if (_stopRequested || audio is null || audio.TryPeek(out _))
        {
            _audioRenderNode.Wake();
        }
    }

    private void AudioRenderCore()
    {
        long workingEpoch = -1;
        long endingEpoch = -1;
        long playedOutEpoch = -1;
        long clockEpoch = -1;
        long clockStamp = -1;

        IAudioSubmit? inFlightSubmit = null;
        RawAudioBuffer? inFlightBuffer = null;

        void ReleaseInFlight()
        {
            inFlightSubmit?.Dispose();
            inFlightSubmit = null;
            inFlightBuffer?.Dispose();
            inFlightBuffer = null;
        }

        while (!_stopRequested)
        {
            MediaQueue<RawAudioBuffer>? queue = _audioQueue;
            if (queue is null)
            {
                goto CleanUp;
            }

            long stamp = _audioRenderNode.Stamp;
            long epoch = _epochs.Read();
            bool playing = CheckState(out MediaEngineState state, MediaEngineState.Playing);
            IPlaybackClock clock = _clock ?? throw new InvalidOperationException("The sound has no clock.");
            IAudioRenderer renderer = _audioRenderer ?? throw new InvalidOperationException("The sound has no renderer.");

            bool epochChanged = workingEpoch != epoch;
            if (epochChanged)
            {
                workingEpoch = epoch;
                ReleaseInFlight();
                renderer.Flush();
            }

            if (epochChanged || epoch != clockEpoch || stamp != clockStamp)
            {
                lock (_clockLock)
                {
                    if (epochChanged)
                    {
                        _audioStarved = true;
                    }

                    ApplyClock(clock, epoch, state);
                }

                clockEpoch = epoch;
                clockStamp = stamp;
            }

            if (!playing)
            {
                _audioRenderNode.Wait(stamp);
                continue;
            }

            CheckStalledAudioClock(clock);

            if (inFlightSubmit is not null)
            {
                if (!inFlightSubmit.MoveNext())
                {
                    PlaybackOperationResult submitted = inFlightSubmit.Result;
                    ReleaseInFlight();

                    if (!submitted.IsSuccessful)
                    {
                        PostFailure(submitted);
                        goto CleanUp;
                    }

                    continue;
                }

                _audioRenderNode.Wait(stamp, AudioIdleTimeoutMilliseconds);
                continue;
            }

            if (!queue.TryPeek(out MediaItem<RawAudioBuffer>? _))
            {
                bool deviceHasAudio = renderer.QueuedSampleCount > 0;
                bool ending = endingEpoch == epoch;
                SetAudioStarved(clock, epoch, state, !deviceHasAudio && !ending);

                if (ending && !deviceHasAudio && playedOutEpoch != endingEpoch)
                {
                    playedOutEpoch = endingEpoch;

                    long epochCopy = endingEpoch;
                    Submit(() => NotifyAudioRenderEnded(epochCopy));
                }

                _audioRenderNode.Wait(stamp, AudioIdleTimeoutMilliseconds);
                continue;
            }

            if (!queue.TryDequeue(out MediaItem<RawAudioBuffer>? entry))
            {
                if (queue.IsClosed)
                {
                    goto CleanUp;
                }

                continue;
            }

            TryWakeReader();

            RawAudioBuffer? buffer = entry.Item;
            long bufferEpoch = entry.Epoch;

            if (epoch != bufferEpoch)
            {
                buffer?.Dispose();
                continue;
            }

            if (entry.IsLast || buffer is null)
            {
                buffer?.Dispose();
                endingEpoch = entry.Epoch;
                continue;
            }

            SetAudioStarved(clock, epoch, state, false);
            AnchorClock(clock, buffer.Timestamp, epoch);

            inFlightSubmit = renderer.Submit(buffer);
            inFlightBuffer = buffer;
        }

    CleanUp:
        ReleaseInFlight();
    }

    private void CheckStalledAudioClock(IPlaybackClock clock)
    {
        long epoch = _epochs.Read();
        if (_deviceWatchEpoch != epoch)
        {
            _deviceWatchEpoch = epoch;
            _deviceCheckedAt = 0;
            _deviceStallReported = false;
        }

        long now = Stopwatch.GetTimestamp();

        if (_deviceCheckedAt == 0)
        {
            _deviceCheckedAt = now;
            _deviceMovedAt = now;
            _devicePosition = clock.Position;
            return;
        }

        if (now - _deviceCheckedAt < StalledAudioDeviceCheckIntervalTicks)
        {
            return;
        }

        _deviceCheckedAt = now;

        bool expected = _state == MediaEngineState.Playing
            && !_videoStarved
            && !_audioStarved
            && Volatile.Read(ref _anchoredEpoch) == epoch
            && clock.IsRunning;

        TimeSpan position = clock.Position;
        bool moved = position != _devicePosition;
        _devicePosition = position;

        if (moved)
        {
            _deviceMovedAt = now;
            _deviceStallReported = false;
            return;
        }

        if (!expected)
        {
            _deviceMovedAt = now;
            return;
        }

        if (now - _deviceMovedAt >= StalledAudioDeviceThresholdTicks && !_deviceStallReported)
        {
            _deviceStallReported = true;
            PostWarning(WarningAudioDeviceStalled);
        }
    }

    private void SetAudioStarved(IPlaybackClock clock, long epoch, MediaEngineState state, bool value)
    {
        if (_audioStarved == value)
        {
            return;
        }

        lock (_clockLock)
        {
            _audioStarved = value;
            ApplyClock(clock, epoch, state);
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

    private void ApplyClock(IPlaybackClock clock, long epoch, MediaEngineState state)
    {
        if (Volatile.Read(ref _anchoredEpoch) != epoch)
        {
            // This sync is inaccurate, but it is the best that can be done for now.
            clock.SetPosition(TimeSpan.FromTicks(Volatile.Read(ref _pendingSeekPosition)));
        }

        bool start = state == MediaEngineState.Playing && !_videoStarved && !_audioStarved;
        if (start == clock.IsRunning)
        {
            return;
        }

        if (start)
        {
            clock.Start();
        }
        else
        {
            clock.Pause();
        }
    }

    private void AnchorClock(IPlaybackClock clock, TimeSpan mediaTimestamp, long epoch)
    {
        lock (_clockLock)
        {
            if (_anchoredEpoch == epoch)
            {
                return;
            }

            _anchoredEpoch = epoch;
            clock.SetPosition(mediaTimestamp);
        }
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
