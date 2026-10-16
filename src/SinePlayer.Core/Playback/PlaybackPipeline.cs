// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

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

    private const int FrameQueueCapacity = 8;
    private const int VideoPacketMinimum = 64;
    private const int VideoPacketWake = VideoPacketMinimum / 2;

    private const int AudioMinimumMilliseconds = 500;
    private const int ReaderCheckIntervalMilliseconds = 15;
    private const int VideoStarvationMilliseconds = 200;
    private const int VideoStarvationResumeMilliseconds = 100;

    #endregion

    #region Static members

    private static readonly ITaskDispatcher s_playbackQueue = TaskDispatcher.Factory.NewQueue("Pipeline - Playback");

    private static Task Submit(Action work)
    {
        return s_playbackQueue.Submit(work);
    }

    private static Task<T> Submit<T>(Func<T> work)
    {
        return s_playbackQueue.Submit(work);
    }

    #endregion

    #region Variables

    private readonly ISourceProviderFactory _sourceFactory;
    private readonly IVideoDecoderFactory _videoDecoderFactory;
    private readonly IAudioDecoderFactory _audioDecoderFactory;
    private readonly IComponentFactory<IVideoRenderer> _videoRendererFactory;
    private readonly IAudioRenderer _audioRenderer;
    private readonly ISynchronizer _synchronizer;

    private MediaInfo? _info;
    private VideoStreamInfo? _videoStreamInfo;
    private AudioStreamInfo? _audioStreamInfo;
    private long _audioMinimumSamples;
    private long _audioWakeSamples;

    private IVideoRenderTarget? _videoTarget;
    private IMediaSource? _source;
    private IPlaybackClock? _clock;
    private IVideoDecoder? _videoDecoder;
    private IAudioDecoder? _audioDecoder;
    private IVideoRenderer? _videoRenderer;

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
    private readonly object _playbackStateLock = new();
    private long _playbackStateStamp;
    private long _pendingSeekPosition;
    private int _anchorClockPending;
    private long _endingEpoch = -1;
    private long _videoRenderEndedEpoch = -1;
    private long _audioRenderEndedEpoch = -1;

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
        IAudioRenderer audioRenderer,
        ISynchronizer synchronizer)
    {
        _sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
        _videoDecoderFactory = videoDecoderFactory ?? throw new ArgumentNullException(nameof(videoDecoderFactory));
        _videoRendererFactory = videoRendererFactory ?? throw new ArgumentNullException(nameof(videoRendererFactory));
        _audioDecoderFactory = audioDecoderFactory ?? throw new ArgumentNullException(nameof(audioDecoderFactory));
        _audioRenderer = audioRenderer ?? throw new ArgumentNullException(nameof(audioRenderer));
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

        _clock ??= new StopwatchClock();

        return PlaybackOperationResult.Success;
    }

    private PlaybackOperationResult PrepareAudio(IMediaSource source, AudioStreamInfo stream)
    {
        PlaybackOperationResult created = _audioDecoderFactory.TryCreate(source, stream, DecoderOptions.Default, out IAudioDecoder? decoder);
        if (!created.IsSuccessful)
        {
            return created;
        }

        if (decoder is null)
        {
            return PlaybackOperationResult.Failure("No audio decoder was created.");
        }

        PlaybackOperationResult configured = _audioRenderer.Configure(stream);
        if (!configured.IsSuccessful)
        {
            decoder.Dispose();
            return configured;
        }

        _audioDecoder = decoder;
        _clock = _audioRenderer.Clock;

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
        _frameQueue = new MediaQueue<RawVideoFrame>(limit: FrameQueueCapacity);

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
        SignalPlaybackStateChange();
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
        SignalPlaybackStateChange();
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

            SetVideoStarved(false);
            SetAudioStarved(false);

            Interlocked.Exchange(ref _anchorClockPending, 1);
            _clock?.SetPosition(position);
        });

        SignalPlaybackStateChange();
        return PlaybackOperationResult.Success;
    }

    private PlaybackOperationResult StopCore()
    {
        _stopRequested = true;
        SignalPlaybackStateChange();

        _audioQueue?.Dispose();
        _audioQueue = null;
        _packetQueue?.Dispose();
        _packetQueue = null;
        _frameQueue?.Dispose();
        _frameQueue = null;

        _audioRenderer.Flush();

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

        _videoTarget?.SurfaceSizeChanged -= VideoTarget_SurfaceSizeChanged;
        _videoTarget = null;

        _source?.Dispose();
        _source = null;

        _clock = null;
        _videoStreamInfo = null;
        _audioStreamInfo = null;
        _info = null;
        Interlocked.Exchange(ref _endingEpoch, -1);
        _videoStarved = false;
        _audioStarved = false;
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
        Interlocked.Exchange(ref _anchorClockPending, 0);

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
        SignalPlaybackStateChange();
    }

    private void SetVideoStarved(bool value)
    {
        if (_videoStarved == value)
        {
            return;
        }

        _videoStarved = value;
        StartOrStopClock();
    }

    private void SetAudioStarved(bool value)
    {
        if (_audioStarved == value)
        {
            return;
        }

        _audioStarved = value;
        StartOrStopClock();
    }

    private PlaybackOperationResult Fail(string message)
    {
        SetState(MediaEngineState.Error, message);
        SignalPlaybackStateChange();
        return PlaybackOperationResult.Failure(message);
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

        StartOrStopClock();

        StateChanged?.Invoke(state, error);
    }

    private void StartOrStopClock()
    {
        IPlaybackClock? clock = _clock;
        if (clock is null)
        {
            return;
        }

        bool start = _state == MediaEngineState.Playing && !_videoStarved && !_audioStarved;
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

    private void RaiseWarning(string warning)
    {
        WarningRaised?.Invoke(warning);
    }

    #endregion

    #region Reader

    private void ReaderCore()
    {
        IMediaSource? source = _source;
        if (source is null)
        {
            return;
        }

        long workingEpoch = _epochs.Read();
        long endingEpoch = -1;
        bool buffering = false;

        while (!_stopRequested)
        {
            long stamp = Volatile.Read(ref _playbackStateStamp);

            if (!CheckState(out _, MediaEngineState.Playing, MediaEngineState.Paused))
            {
                WaitForPlaybackStateChange(stamp);
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
                WaitForPlaybackStateChange(stamp);
                continue;
            }

            if (!buffering)
            {
                if (!NeedsBuffering())
                {
                    WaitForPlaybackStateChange(stamp, ReaderCheckIntervalMilliseconds);
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
                PostFailure(routed.Message);
                return;
            }

            if (ending)
            {
                endingEpoch = workingEpoch;

                PlaybackOperationResult drained = DrainAudioDecoder(workingEpoch);
                if (!drained.IsSuccessful)
                {
                    PostFailure(drained.Message);
                    return;
                }

                _packetQueue?.EndStream(workingEpoch);
                _audioQueue?.EndStream(workingEpoch);
                Submit(() => NotifySourceEnded(workingEpoch));
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

                if (packetQueue.Enqueue(packet, epoch))
                {
                    handedOn = true;
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
            if (!audioQueue.Enqueue(buffer, epoch))
            {
                buffer.Dispose();
            }
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

        foreach (RawAudioBuffer buffer in buffers)
        {
            if (!queue.Enqueue(buffer, epoch))
            {
                buffer.Dispose();
            }
        }

        return PlaybackOperationResult.Success;
    }

    #endregion

    #region Video decode

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

            long stamp = Volatile.Read(ref _playbackStateStamp);

            if (!queue.Dequeue(out MediaPacket? packet, out long packetEpoch, out bool isLast))
            {
                return;
            }

            using (packet)
            {
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
                        PostFailure(exception.Message);
                        return;
                    }

                    Interlocked.Exchange(ref _tailDecodedFrameTicks, -1);
                }

                if (workingEpoch != packetEpoch)
                {
                    continue;
                }

                if (isLast || packet is null)
                {
                    PlaybackOperationResult drained = DrainVideo(packetEpoch);
                    if (!drained.IsSuccessful)
                    {
                        PostFailure(drained.Message);
                        return;
                    }

                    WaitForPlaybackStateChange(stamp);
                    continue;
                }

                PlaybackOperationResult decoded = DecodeVideo(packet, out IReadOnlyList<RawVideoFrame>? frames);
                if (!decoded.IsSuccessful)
                {
                    PostFailure(decoded.Message);
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
        IReadOnlyList<RawVideoFrame> produced;
        try
        {
            decoded = videoDecoder.TryDecode(packet, out produced);
        }
        catch (Exception ex)
        {
            Interlocked.Add(ref _decodeTicks, Stopwatch.GetTimestamp() - startTicks);
            return PlaybackOperationResult.Failure(ex.Message);
        }

        Interlocked.Add(ref _decodeTicks, Stopwatch.GetTimestamp() - startTicks);

        if (!decoded.IsSuccessful)
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
        return PlaybackOperationResult.Success;
    }

    private void QueueFrames(IReadOnlyList<RawVideoFrame> frames, long epoch)
    {
        bool fastExit = false;
        MediaQueue<RawVideoFrame>? frameQueue = _frameQueue;

        foreach (RawVideoFrame frame in frames)
        {
            if (fastExit || frameQueue is null || _epochs.Read() != epoch || !frameQueue.Enqueue(frame, epoch))
            {
                fastExit = true;
                frame.Dispose();
                continue;
            }

            Interlocked.Exchange(ref _tailDecodedFrameTicks, frame.Timestamp.Ticks);
        }
    }

    #endregion

    #region Video render

    private void VideoRenderCore()
    {
        RawVideoFrame? held = null;
        long heldEpoch = 0;

        // The position whose picture is on the screen — set wherever a frame is handed to the renderer,
        // playing or paused. It is what tells a still from a frame this thread has already shown, so a pause
        // does not show the same position's picture twice (see TryShowTheStill).
        long shownEpoch = -1;

        // The position whose end of stream this thread has taken and answered for. A word is said once per
        // position; a seek makes this one old rather than something to retract.
        long endedForEpoch = -1;

        while (!_stopRequested)
        {
            MediaQueue<RawVideoFrame>? queue = _frameQueue;
            if (queue is null)
            {
                held?.Dispose();
                return;
            }

            long stamp = Volatile.Read(ref _playbackStateStamp);

            if (!CheckState(out MediaEngineState state, MediaEngineState.Playing))
            {
                // Nothing is being presented. The window can still change while that is so, and a renderer
                // left on its old surface has the host stretch the paused picture into the new one — so it is
                // given the chance to take the change up, which is what keeps a paused video following the
                // window. It does nothing unless the surface actually changed.
                IVideoRenderer? pausedRenderer = _videoRenderer;
                if (pausedRenderer is not null && state is MediaEngineState.Paused or MediaEngineState.Ended)
                {
                    PlaybackOperationResult refreshed;
                    try
                    {
                        refreshed = pausedRenderer.RefreshSurface();
                    }
                    catch (Exception exception)
                    {
                        refreshed = PlaybackOperationResult.Failure(exception.Message);
                    }

                    if (!refreshed.IsSuccessful)
                    {
                        PostFailure(refreshed.Message);
                        return;
                    }
                }

                // What a paused file shows is the picture of where it is — one frame, once per position, and
                // this is where the seek that left it paused gets its still (see TryShowTheStill).
                if (state == MediaEngineState.Paused && !TryShowTheStill(queue, ref shownEpoch, ref endedForEpoch))
                {
                    held?.Dispose();
                    return;
                }

                WaitForPlaybackStateChange(stamp, IdleWaitMilliseconds);
                continue;
            }

            if (held is not null && _epochs.Read() != heldEpoch)
            {
                // A seek has moved playback on: this frame belongs to where it used to be.
                held.Dispose();
                held = null;
            }

            UpdateVideoStarvation();

            if (held is null)
            {
                if (!queue.Dequeue(out RawVideoFrame? dequeued, out heldEpoch, out bool isLast))
                {
                    // False is the queue closed, which is a stopping pipeline: nothing more will be shown,
                    // and the frame being held belongs to a playback that is over.
                    held?.Dispose();
                    return;
                }

                if (isLast)
                {
                    // The decoder has ended the frame stream for `heldEpoch`, and this entry is behind every
                    // frame it produced — and nothing is held here, or this dequeue would not have happened —
                    // so everything this position has is on the screen already. That is the picture's word
                    // for the ending, and the entry carries the position to say it about. Nothing else may
                    // read that position in the meantime: the report is about this moment, so it is posted
                    // with a copy of it rather than with the loop's variable for it.
                    long endedEpoch = heldEpoch;
                    if (endedForEpoch != endedEpoch)
                    {
                        endedForEpoch = endedEpoch;
                        Submit(() => NotifyVideoRenderEnded(endedEpoch));
                    }

                    continue;
                }

                if (dequeued is null)
                {
                    // An entry is media or the end of the stream, and the end has been answered above, so
                    // this cannot happen — it is the check that tells the rest of the loop the frame is there.
                    continue;
                }

                held = dequeued;

                if (_epochs.Read() != heldEpoch)
                {
                    // A seek can land between the check above and this dequeue, in which case the frame
                    // it handed over is the last of the position playback has left — and it must not be
                    // used to anchor the clock, which is what the first frame of the new position is for.
                    held.Dispose();
                    held = null;
                    continue;
                }
            }

            if (_audioDecoder is null)
            {
                // With nothing counting device samples, the first frame of a new position is what the
                // clock has to start from — otherwise the picture is late by however far the file
                // resumed before the position that was asked for.
                AnchorClock(held.Timestamp);
            }

            // Give up on a frame the clock has left behind, but only when the queue already holds a closer
            // one — the frame dropped is one whose moment has gone, and never the newest there is — and
            // only when the clock is a fact about media, which it is once the first media of the position
            // has anchored it. Until then it reads the position the seek *asked for*, which a demuxer may
            // have landed before, and a frame judged against that is judged against a position nothing is
            // playing: the picture would skip the distance between the two. Shown late instead, which is
            // invisible beside a sound that has not started either.
            if (Volatile.Read(ref _anchorClockPending) == 0
                && _synchronizer.Evaluate(held.Timestamp, Position) == FrameSyncAction.Drop
                && queue.TryPeek(out RawVideoFrame? closer, out long closerEpoch)
                && closer is not null
                && closerEpoch == heldEpoch)
            {
                held.Dispose();
                held = null;
                Interlocked.Increment(ref _droppedFrameCount);
                continue;
            }

            if (_synchronizer.Evaluate(held.Timestamp, Position) == FrameSyncAction.Wait)
            {
                // Early: wait for its moment. This is the wait that makes the picture follow the sound,
                // and nothing in the pipeline waits on this thread. It sleeps until the frame is due
                // rather than in steps, so a frame costs one wake-up instead of a tick every millisecond,
                // and it is woken early by anything that changes what it would do — a seek, a pause, a
                // stop — because those are what the stamp is for.
                int remaining = (int)Math.Clamp((held.Timestamp - Position).TotalMilliseconds, 1, IdleWaitMilliseconds);
                WaitForPlaybackStateChange(stamp, remaining);
                continue;
            }

            PlaybackOperationResult shown = Show(held);

            if (!shown.IsSuccessful)
            {
                held.Dispose();
                held = null;
                PostFailure(shown.Message);
                return;
            }

            // The picture on the screen is this position's, which is what keeps a pause from showing a still
            // of a frame it has just been showing.
            shownEpoch = heldEpoch;

            held.Dispose();
            held = null;
        }

        held?.Dispose();
    }

    /// <summary>
    /// Offers the picture of where playback is while nothing is being presented: the position's first frame
    /// that is still to be seen, taken from the queue that holds it and handed to the renderer once.
    /// <para>
    /// It is not a presentation: the clock is not consulted, nothing is anchored, dropped or starved, and no
    /// frame is lost — it is what a paused file shows, and what a seek that left playback paused shows
    /// instead of the picture of the position it came from. Untimed by anything on purpose: the position's
    /// frames may already be waiting, in which case this is over at once, or the decoder may still be on its
    /// way to them, in which case this waits for the frame and the frame is the wake. Frames of a position
    /// playback has left are given up on rather than shown — that is the check every consumer makes before
    /// using what it took — and the position that is current is re-read as it goes, because a seek landing
    /// while this waits leaves the frame it is about to be handed belonging to nobody.
    /// </para>
    /// </summary>
    /// <returns>False when playback is stopping, which is the caller's cue to finish.</returns>
    private bool TryShowTheStill(MediaQueue<RawVideoFrame> queue, ref long shownEpoch, ref long endedForEpoch)
    {
        while (!_stopRequested)
        {
            long epoch = _epochs.Read();
            if (shownEpoch == epoch || endedForEpoch == epoch)
            {
                // This position's picture is on the screen already, or the position has none to show. A seek
                // that moved on lands here as well — it is why the epoch is read here rather than taken once.
                return true;
            }

            if (!queue.Dequeue(out RawVideoFrame? frame, out long frameEpoch, out bool isLast))
            {
                return false;
            }

            if (isLast)
            {
                if (frameEpoch == epoch)
                {
                    // The position has no frame to show — its stream is over before anything was offered. That
                    // is the picture's word for the ending, and it is said here rather than left for a
                    // presentation that is not coming: a paused file whose picture has nothing left has shown
                    // all it has. Said with a copy of the position, as everywhere else.
                    endedForEpoch = frameEpoch;
                    long endedEpoch = frameEpoch;
                    Submit(() => NotifyVideoRenderEnded(endedEpoch));
                }

                return true;
            }

            if (frame is null)
            {
                continue;
            }

            if (frameEpoch != epoch || _epochs.Read() != epoch)
            {
                frame.Dispose();
                continue;
            }

            PlaybackOperationResult shown = Show(frame);
            frame.Dispose();

            if (!shown.IsSuccessful)
            {
                PostFailure(shown.Message);
                return false;
            }

            shownEpoch = epoch;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Holds the clock back while the picture cannot catch up with it, and lets it go again once the picture
    /// is close.
    /// <para>
    /// Measured as how far the newest frame the decoder has produced is behind the clock, with a wide
    /// threshold to start and a narrow one to stop: a decoder that hovers around the threshold must not make
    /// playback stop-start, and a picture a frame behind is not a reason to hold the sound back. While the
    /// clock is held the decoder keeps producing and the picture keeps being shown, so the gap closes by
    /// itself — which is what makes this a wait that always ends rather than a stall.
    /// </para>
    /// </summary>
    private void UpdateVideoStarvation()
    {
        IPlaybackClock? clock = _clock;
        long tailTicks = Interlocked.Read(ref _tailDecodedFrameTicks);
        if (clock is null || tailTicks < 0 || IsEnding(_epochs.Read()) || Volatile.Read(ref _anchorClockPending) != 0)
        {
            // Nothing decoded yet, nothing left to decode, or a position whose clock has not been anchored:
            // none of those is a picture that has fallen behind.
            RequestVideoStarved(false);
            return;
        }

        TimeSpan behind = clock.Position - TimeSpan.FromTicks(tailTicks);

        // The published flag is what the hysteresis reads, because it is the same fact the queue owns: a
        // picture that kept its own copy of "am I currently starved" would be holding on to something only
        // the queue can retract, which is what a seek does.
        bool starved = _videoStarved
            ? behind > TimeSpan.FromMilliseconds(VideoStarvationResumeMilliseconds)
            : behind > TimeSpan.FromMilliseconds(VideoStarvationMilliseconds);
        RequestVideoStarved(starved);
    }

    /// <summary>Shows one frame. Whether it was the closest frame there was decided before this.</summary>
    private PlaybackOperationResult Show(RawVideoFrame frame)
    {
        IVideoRenderer? renderer = _videoRenderer;
        if (renderer is null)
        {
            return PlaybackOperationResult.Failure("The video renderer is not available.");
        }

        long started = Stopwatch.GetTimestamp();
        PlaybackOperationResult rendered;
        try
        {
            rendered = renderer.Render(frame);
        }
        catch (Exception exception)
        {
            rendered = PlaybackOperationResult.Failure(exception.Message);
        }

        Interlocked.Add(ref _renderTicks, Stopwatch.GetTimestamp() - started);

        if (rendered.IsSuccessful)
        {
            Interlocked.Increment(ref _presentedFrameCount);
            Interlocked.Exchange(ref _pictureTicks, frame.Timestamp.Ticks);

            // What the display did with that frame is the renderer's to know and nobody else's; publishing
            // it here is what keeps the answer off the renderer's reference and in the same place as the
            // count it is subtracted from.
            if (renderer.TryGetDisplayedFrameCount(out long displayed))
            {
                Interlocked.Exchange(ref _displayedFrameCount, displayed);
            }
        }

        return rendered;
    }

    #endregion

    #region Audio render

    /// <summary>
    /// Hands audio to the device. The device is what moves the clock, so this thread is the one that decides
    /// whether time may pass: while there is nothing to hand over the clock is held back, and it is let go
    /// again the moment there is something. Nothing is ever dropped here, and the only wait is for room in
    /// the device's buffer — a wait that ends when the device plays, or never, in which case the whole
    /// pipeline waits with it and the device is reported as the reason.
    /// </summary>
    private void AudioRenderCore()
    {
        try
        {
            // The epoch whose audio the device is holding. The device is this thread's, so a move to a new
            // position is taken up here and not by whoever asked for the move: what the device still holds
            // was produced for where playback used to be, and leaving it there is what plays a moment of the
            // old position over the start of the new one — and what leaves the clock counting from a device
            // that was never reset, which is a clock that jumps and takes the picture with it.
            long deviceEpoch = _epochs.Read();

            // The position whose stream this queue has ended — from there nothing more will be played for it
            // — and the position this thread has already told playback about, both taken from the end entry
            // rather than from the epoch. A seek makes them old rather than retracting them.
            long endedEpoch = -1;
            long playedOutEpoch = -1;

            while (!_stopRequested)
            {
                MediaQueue<RawAudioBuffer>? queue = _audioQueue;
                if (queue is null)
                {
                    return;
                }

                // Read before anything is decided below, so a change arriving between a decision and the
                // wait is caught by the wait's own check rather than by a pulse nobody was listening for.
                long changed = Volatile.Read(ref _playbackStateStamp);

                // Taken up at the top of the loop, which is the earliest this thread can act on it: whether
                // it is playing or paused, and whether or not the reader has produced anything for the new
                // position yet. A flush on a device that holds nothing is what it costs, and that is nothing.
                long currentEpoch = _epochs.Read();
                if (deviceEpoch != currentEpoch)
                {
                    deviceEpoch = currentEpoch;
                    _audioRenderer.Flush();
                }

                if (_state != MediaEngineState.Playing)
                {
                    // Paused: nothing new should reach the device, and the queue keeps what it holds. A
                    // resume, a seek or a stop is what ends this wait, which is why the device is not polled
                    // here at all.
                    WaitForPlaybackStateChange(changed, IdleWaitMilliseconds);
                    continue;
                }

                WatchDeviceProgress();

                if (!queue.TryPeek(out RawAudioBuffer? _, out long _))
                {
                    // Nothing at all to hand over — not even the end of the stream, which is an entry like any
                    // other and is taken below — so the device is the only thing left that can answer
                    // anything, and the only thing in this thread's world that cannot signal. That is why
                    // this is a wait on the clock rather than on the queue.
                    //
                    // Two questions are asked of it, and both are about what is being heard rather than about
                    // what is in the queue. Time should pass while the device still holds audio, because what
                    // it holds is playing; and the file having run out is never a reason to hold it, because
                    // the tail playing out is not a stall. So the clock is held only when the device holds
                    // nothing *and* this position's stream has not ended — the sound having nothing to play,
                    // which is a fact about the device, not about how far behind the reader is.
                    bool deviceHasAudio = _audioRenderer.QueuedSampleCount > 0;
                    bool ending = endedEpoch == currentEpoch;
                    RequestAudioStarved(!deviceHasAudio && !ending);

                    // And then the sound has one more thing to say, which only this thread can say, the
                    // device being its own: it has played everything. Said once per position — a copy of it,
                    // because a seek can move the loop's variable on before the report is posted — and only
                    // when the device is empty, because until then the last of the sound has not been heard.
                    long playedOut = endedEpoch;
                    if (ending && !deviceHasAudio && playedOutEpoch != playedOut)
                    {
                        playedOutEpoch = playedOut;
                        Submit(() => NotifyAudioRenderEnded(playedOut));
                    }

                    WaitForPlaybackStateChange(changed, DeviceRoomCheckMilliseconds);
                    continue;
                }

                if (!queue.Dequeue(out RawAudioBuffer? buffer, out long bufferEpoch, out bool isLast))
                {
                    // False is the queue closed, which is a stopping pipeline: nothing more will be played,
                    // and this is one of the four returns the stop's joins depend on.
                    return;
                }

                if (isLast)
                {
                    // The end of the audio stream, handed over after the last block of it: nothing more will
                    // be played for this position. What is left to say about the sound is said above, when
                    // the device has played out what it holds.
                    endedEpoch = bufferEpoch;
                    continue;
                }

                if (buffer is null)
                {
                    // An entry is media or the end of the stream, and the end has been answered above, so
                    // this cannot happen — it is the check that tells the block below it is there.
                    continue;
                }

                using (buffer)
                {
                    // There is something to hand over, so whatever the sound was waiting for has arrived.
                    RequestAudioStarved(false);

                    if (_epochs.Read() != bufferEpoch)
                    {
                        // The block was produced for where playback used to be. It is given up on here rather
                        // than played, and before anything else is done with it: anchoring the clock to it
                        // would set time back to the position the seek has just left.
                        continue;
                    }

                    long epoch = _epochs.Read();

                    // The sound is what the clock counts, so the first block handed to the device after a
                    // seek is the best anchor there is: it is the first thing that will be heard.
                    AnchorClock(buffer.Timestamp);

                    if (!WaitForAudioRoom(epoch))
                    {
                        return;
                    }

                    if (_epochs.Read() != epoch)
                    {
                        // A seek landed while this block was waiting for room, so the block belongs to the
                        // position playback has left. It is let go of rather than played.
                        continue;
                    }

                    // The device is let go of here as well as at the top of the loop, because a seek that
                    // lands while this thread is waiting for a block can hand it one from the new position
                    // before the top of the loop is reached again: the device would then be given the new
                    // position's sound while still holding the old one, and left counting from a position
                    // nobody is playing. Anything the device holds for a position this block does not belong
                    // to is dropped before the block reaches it.
                    if (deviceEpoch != epoch)
                    {
                        deviceEpoch = epoch;
                        _audioRenderer.Flush();
                    }

                    PlaybackOperationResult submitted = _audioRenderer.Submit(buffer);
                    if (!submitted.IsSuccessful)
                    {
                        PostFailure(submitted.Message);
                        return;
                    }

                    deviceEpoch = epoch;

                    if (_epochs.Read() != epoch)
                    {
                        // This block was produced for where playback used to be and may already be in the
                        // device, which the seek has since refilled — a device that stopped consuming makes
                        // the submit of a block outlast the seek that interrupted it. Letting the device go
                        // of what it holds is what keeps a seek from playing a moment of where it came from.
                        deviceEpoch = _epochs.Read();
                        _audioRenderer.Flush();
                    }
                }
            }
        }
        catch (Exception exception)
        {
            PostFailure(exception.Message);
        }
    }

    /// <summary>
    /// Waits until the device has room for more audio. The wait has no deadline, because a device that is
    /// not consuming is a device whose clock is not moving, and a clock that is not moving stops the picture
    /// with it — that is the design working, not the pipeline failing, and the device is reported by
    /// <see cref="WatchDeviceProgress"/> rather than worked around here.
    /// <para>
    /// What the wait does have is all the ways out that matter: pausing, seeking and stopping all release it,
    /// so it is never a wait with no end, and the block being carried is kept rather than dropped so a pause
    /// costs nothing but time.
    /// </para>
    /// </summary>
    /// <returns>False when playback is stopping and this thread should finish.</returns>
    private bool WaitForAudioRoom(long epoch)
    {
        long limit = _audioStreamInfo is not null && _audioStreamInfo.SampleRate > 0
            ? _audioStreamInfo.SampleRate * QueuedLimitMilliseconds / 1000
            : 0;

        while (!_stopRequested)
        {
            // Read before anything is decided, so a resume, a seek or a stop arriving during a check below
            // ends the wait at once rather than at the next timeout.
            long changed = Volatile.Read(ref _playbackStateStamp);

            WatchDeviceProgress();

            if (_epochs.Read() != epoch)
            {
                // A seek has moved on. The block is let go of by the caller, and waiting for room for it
                // would be waiting on behalf of a position nobody wants.
                return true;
            }

            if (_state != MediaEngineState.Playing)
            {
                // Paused: the device holds what it has, so there is no room coming. The block waits here
                // and is submitted when playback resumes.
                WaitForPlaybackStateChange(changed, IdleWaitMilliseconds);
                continue;
            }

            if (limit <= 0 || _audioRenderer.QueuedSampleCount < limit)
            {
                return true;
            }

            // Room comes back from the device, which is the one thing here that cannot be signalled — the
            // device has no way to tell us it has played something — so this stays a poll, at the pace the
            // device drains a buffer rather than at the pace of a processor loop.
            WaitForPlaybackStateChange(changed, DeviceRoomCheckMilliseconds);
        }

        return false;
    }

    /// <summary>
    /// Says so when the device holds audio and its position stands still, and says so again when it moves.
    /// <para>
    /// Nothing is done about it, and that is deliberate: a clock that is not moving is a picture that waits
    /// and a sound that has stopped, which is the right behaviour and needs no special case. It does need
    /// saying, though, or what the user sees is a picture that froze with no reason given. Called from the
    /// audio thread, which is the thread holding the device, and throttled because it is called from a loop.
    /// </para>
    /// </summary>
    private void WatchDeviceProgress()
    {
        IPlaybackClock? clock = _clock;
        if (clock is null || _audioDecoder is null)
        {
            return;
        }

        // A seek moved playback on: the device's position jumps with it, which is movement rather than
        // recovery, and whatever was being watched belongs to where playback used to be. Noticing that here,
        // where the state is owned, is what keeps the queue from having to reach into any of it.
        long epoch = _epochs.Read();
        if (_deviceWatchEpoch != epoch)
        {
            _deviceWatchEpoch = epoch;
            _deviceCheckedAt = 0;
            _deviceStallReported = false;
        }

        long now = Stopwatch.GetTimestamp();

        // The first look of a playback, told apart by a time that has never been set. The device has not had
        // a chance to move yet, and counting the wait for movement from before playback started — or from
        // whenever the pipeline was built — is how a device that is working gets called stuck. Both the stamp
        // and the position start here instead.
        if (_deviceCheckedAt == 0)
        {
            _deviceCheckedAt = now;
            _deviceMovedAt = now;
            _devicePosition = clock.Position;
            return;
        }

        if (now - _deviceCheckedAt < DeviceCheckTicks)
        {
            return;
        }

        _deviceCheckedAt = now;

        bool expected = _state == MediaEngineState.Playing
            && !_videoStarved
            && !_audioStarved
            && Volatile.Read(ref _anchorClockPending) == 0
            && clock.IsRunning;

        TimeSpan position = clock.Position;
        bool moved = position != _devicePosition;
        _devicePosition = position;

        if (moved)
        {
            _deviceMovedAt = now;

            // Movement is the fact a reported stall ends on, and it is movement of the device's own position —
            // not whether anything is expected of it right now. Speaking once per stall is what keeps a
            // warning from being a stream of them, and the device coming back is worth the one line.
            if (_deviceStallReported)
            {
                _deviceStallReported = false;
                PostWarning(DeviceAdvancingWarning);
            }

            return;
        }

        if (!expected)
        {
            // Nothing is expected of the device right now, so a device that is standing still is not a stall.
            // The timer is restarted rather than left running, so the second this stops being true is the
            // first second that is counted.
            _deviceMovedAt = now;
            return;
        }

        if (now - _deviceMovedAt >= DeviceNoticeTicks && !_deviceStallReported)
        {
            _deviceStallReported = true;
            PostWarning(DeviceNotAdvancingWarning);
        }
    }

    #endregion

    // Holding the clock back for a picture that cannot catch up: a wide threshold to start and a narrow one
    // to stop, so a decoder hovering at the boundary cannot make playback stop-start
    // The audio thread's device: how full it may be before the thread waits, how long that wait lasts, and
    // how a device that accepts audio and stops moving is told from one that is working
    /// <summary>How full the device's buffer may be before the audio thread waits for it to drain.</summary>
    private const int QueuedLimitMilliseconds = 100;
    /// <summary>
    /// How long the audio thread waits when only the device can change anything: for it to take what the
    /// thread is holding, and for it to play out what it holds while the thread has nothing to hand over.
    /// In milliseconds.
    /// </summary>
    private const int DeviceRoomCheckMilliseconds = 5;
    /// <summary>How often the device's position is looked at, and how long it may stand still before it is reported.</summary>
    private const int DeviceCheckMilliseconds = 100;
    private const int DeviceNoticeMilliseconds = 1000;
    private const string DeviceNotAdvancingWarning = "The audio device is not moving.";
    private const string DeviceAdvancingWarning = "The audio device is moving again.";

    // The end of a file, and what an idle thread costs
    /// <summary>
    /// How long a thread sleeps when it has nothing to do at all, in milliseconds. This is the backstop,
    /// not the mechanism: what ends these waits is a change — a resume, a seek, a stop, a new surface — and
    /// four wake-ups a second is what an idle pipeline costs when nothing happens.
    /// </summary>
    private const int IdleWaitMilliseconds = 250;

    private static readonly long DeviceCheckTicks = Stopwatch.Frequency * DeviceCheckMilliseconds / 1000;
    private static readonly long DeviceNoticeTicks = Stopwatch.Frequency * DeviceNoticeMilliseconds / 1000;

    /// <summary>
    /// Points the clock at the first piece of media that actually plays after a seek, which is the only
    /// place it can be anchored honestly: the requested position can be up to a keyframe away from where
    /// the file resumes, and the clock counts what the device renders. Anchoring to the request would
    /// leave the picture permanently that far behind the sound, every frame of it late enough to be
    /// dropped, and the picture would never move again.
    /// <para>
    /// The first call wins. With audio playing that is the first block handed to the device; without it,
    /// the first frame the presentation thread takes up.
    /// </para>
    /// </summary>

    #region Events

    private void VideoTarget_SurfaceSizeChanged()
    {
        SignalPlaybackStateChange();
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

    private void AnchorClock(TimeSpan mediaTimestamp)
    {
        if (Interlocked.Exchange(ref _anchorClockPending, 0) == 0)
        {
            return;
        }

        _clock?.SetPosition(mediaTimestamp);
    }

    private void SignalPlaybackStateChange()
    {
        lock (_playbackStateLock)
        {
            _playbackStateStamp++;
            Monitor.PulseAll(_playbackStateLock);
        }
    }

    private void WaitForPlaybackStateChange(long stamp, int milliseconds = -1)
    {
        lock (_playbackStateLock)
        {
            if (_playbackStateStamp != stamp)
            {
                return;
            }

            if (milliseconds < 0)
            {
                Monitor.Wait(_playbackStateLock);
            }
            else
            {
                Monitor.Wait(_playbackStateLock, milliseconds);
            }
        }
    }

    private void RequestVideoStarved(bool value)
    {
        if (_videoStarved == value)
        {
            return;
        }

        Submit(() => SetVideoStarved(value));
    }

    private void RequestAudioStarved(bool value)
    {
        if (_audioStarved == value)
        {
            return;
        }

        Submit(() => SetAudioStarved(value));
    }

    private void PostWarning(string warning)
    {
        Submit(() => RaiseWarning(warning));
    }

    private void PostFailure(string message)
    {
        Submit(() => Fail(message));
    }

    #endregion
}
