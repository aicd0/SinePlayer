// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

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

/// <summary>
/// Runs one playback: it composes a source, a decoder per stream and a renderer per stream, and moves
/// packets from the former to the latter on threads of its own.
/// <para>
/// The design in one sentence: <b>the sound leads, the picture follows, and nothing waits for anything that
/// waits for the clock.</b> Thread by thread, that means:
/// </para>
/// <list type="bullet">
/// <item>The <b>reader</b> reads the file and decodes audio on the spot, because audio is the one thing that
/// must never be late. It drops nothing, it is never stopped by a full queue — the queues it feeds have no
/// maximum — and it waits only for the file and for having read a minimum ahead of playback.</item>
/// <item>The <b>video decoding</b> thread turns packets into frames. It drops nothing and never looks at the
/// clock: the only thing that ever stops it is a full frame queue, which is the picture's own back-pressure
/// and therefore always ends when the picture moves.</item>
/// <item>The <b>presentation</b> thread shows the frame closest to the clock and gives up on one only when a
/// closer frame is already waiting; it never waits for a particular frame. It is the only thread that waits
/// for the clock, and it is the follower, so the clock can never be waiting for it. When the picture cannot
/// catch up, it halts the clock instead: the sound waits for the picture rather than running away from it.</item>
/// <item>The <b>audio</b> thread feeds the device, and the device is what moves the clock. When it has
/// nothing to hand over, the clock is halted until it does — a sound that has not arrived must not be one
/// the picture plays on without.</item>
/// </list>
/// <para>
/// A device that stops consuming needs no special case: its clock stops, and everything that follows the
/// clock stops with it until it moves again. Nothing is dropped except frames the clock has already left
/// behind while a closer frame was ready, and each of those is counted — losing one is worth knowing about,
/// and the counter is the only honest way to know.
/// </para>
/// <para>
/// The components are injected rather than created here, which is what lets the same orchestration run
/// against the real backends and against test doubles.
/// </para>
/// <para>
/// The design, the reasoning behind it, and the invariants a change to this class must preserve are stated in
/// <c>docs/PlaybackPipeline.md</c>; read it before changing anything here.
/// </para>
/// </summary>
public sealed class PlaybackPipeline : IDisposable
{
    private const int WaitForAudioMilliseconds = 2000;

    /// <summary>How full the device's buffer may be before the audio thread waits for it to drain.</summary>
    private const int QueuedLimitMilliseconds = 100;

    /// <summary>How long the clock may stand still at the end of the file before the remaining wait is given up on.</summary>
    private const int StalledClockMilliseconds = 500;

    /// <summary>How much audio the reader keeps queued before it idles, in milliseconds of the open stream.</summary>
    private const int AudioMinimumMilliseconds = 500;

    /// <summary>How many video packets the reader keeps queued before it idles.</summary>
    private const int VideoPacketMinimum = 64;

    /// <summary>How empty either queue may become before the reader fills them again.</summary>
    private const int VideoPacketWake = VideoPacketMinimum / 2;

    /// <summary>How far the picture may fall behind the clock before the clock is held back for it.</summary>
    private const int VideoStarvationMilliseconds = 200;

    /// <summary>How close the picture has to come back before the clock is let go again.</summary>
    private const int VideoStarvationResumeMilliseconds = 100;

    /// <summary>How often the device's position is looked at, and how long it may stand still before it is reported.</summary>
    private const int DeviceCheckMilliseconds = 100;
    private const int DeviceNoticeMilliseconds = 1000;
    private const string DeviceNotAdvancingWarning = "The audio device is not moving.";

    private static readonly long DeviceCheckTicks = Stopwatch.Frequency * DeviceCheckMilliseconds / 1000;
    private static readonly long DeviceNoticeTicks = Stopwatch.Frequency * DeviceNoticeMilliseconds / 1000;

    /// <summary>
    /// How many frames may be waiting to be shown before the decoder has to pause. Small on purpose: it is
    /// how much of the picture can be decoded ahead, and decoded frames are the largest thing held here.
    /// </summary>
    private const int FrameQueueCapacity = 8;

    private readonly ISourceProviderFactory _sourceFactory;
    private readonly IVideoDecoderFactory _videoDecoderFactory;
    private readonly IAudioDecoderFactory _audioDecoderFactory;
    private readonly IComponentFactory<IVideoRenderer> _videoRendererFactory;
    private readonly IAudioRenderer _audioRenderer;
    private readonly ISynchronizer _synchronizer;

    private readonly object _stateLock = new();
    private readonly object _mediaLock = new();
    private readonly object _videoDecodeLock = new();
    private readonly object _clockLock = new();
    private readonly EpochGate _epochs = new();
    private readonly ManualResetEventSlim _resume = new(false);

    private IMediaSource? _source;
    private IVideoDecoder? _videoDecoder;
    private IVideoRenderer? _videoRenderer;
    private IAudioDecoder? _audioDecoder;
    private PacketQueue? _packetQueue;
    private Thread? _decodeThread;
    private volatile bool _videoFlushPending;
    private volatile bool _decodeDrained;
    private FrameQueue? _frameQueue;
    private Thread? _videoThread;
    private AudioQueue? _audioQueue;
    private Thread? _audioThread;
    private StopwatchClock? _ownClock;
    private IPlaybackClock? _clock;

    private VideoStreamInfo? _videoStream;
    private AudioStreamInfo? _audioStream;
    private MediaInfo? _info;

    private Thread? _worker;
    private volatile bool _stopRequested;
    private MediaLocator? _pendingLocator;
    private IVideoRenderTarget? _pendingTarget;
    private PlaybackResult? _setupResult;
    private TaskCompletionSource<PlaybackResult>? _setupCompletion;
    private MediaEngineState _state = MediaEngineState.None;
    private string? _error;
    private string? _warning;

    /// <summary>True while the reader is filling the queues to their minimums, false while it is idle.</summary>
    private bool _readerFilling;

    /// <summary>How much audio the reader wants queued, in samples. Set when the stream is opened.</summary>
    private long _audioMinimumSamples;
    private long _audioWakeSamples;

    /// <summary>
    /// True once the file has been read to its end. A queue that empties after this is playing out rather
    /// than starving, which is why it also stops the clock from being halted for it.
    /// </summary>
    private volatile bool _sourceEnded;

    /// <summary>The clock is held back for the picture while this is set: it cannot catch up with time.</summary>
    private volatile bool _videoStarved;

    /// <summary>The clock is held back for the sound while this is set: there is nothing to hand to the device.</summary>
    private volatile bool _audioStarved;

    /// <summary>The last device position seen, and when it was seen. A device that stops moving is reported, not repaired.</summary>
    private TimeSpan _devicePosition;
    private long _deviceMovedAt;
    private long _deviceCheckedAt;

    private long _presentedFrameCount;
    private long _droppedFrameCount;
    private long _decodedFrameCount;
    private long _decodeTicks;
    private long _renderTicks;
    private long _pictureTicks;
    private int _anchorPending;

    /// <summary>
    /// The timestamp of the newest frame the decoder has handed over, or -1 while there is none. Written by
    /// the decoding thread and read by the presentation thread, which is how the picture knows whether it can
    /// still catch up with the clock.
    /// </summary>
    private long _decodedTailTicks = -1;
    private bool _disposed;

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

    /// <summary>Raised after <see cref="State"/> or <see cref="Error"/> changed, on the playback thread.</summary>
    public event Action? StateChanged;

    public MediaEngineState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    /// <summary>Why playback stopped, when the state is <see cref="MediaEngineState.Error"/>.</summary>
    public string? Error
    {
        get
        {
            lock (_stateLock)
            {
                return _error;
            }
        }
    }

    /// <summary>
    /// Something that was left out without stopping playback, for example audio the device would not
    /// accept. Keeping it separate from <see cref="Error"/> is what lets a file play anyway.
    /// </summary>
    public string? Warning
    {
        get
        {
            lock (_stateLock)
            {
                return _warning;
            }
        }
    }

    public MediaInfo? MediaInfo => _info;

    public bool HasVideo => _videoStream is not null;

    public bool HasAudio => _audioStream is not null;

    public TimeSpan Duration => _info?.Duration ?? TimeSpan.Zero;

    public TimeSpan Position => _clock?.Position ?? TimeSpan.Zero;

    public long PresentedFrameCount => Interlocked.Read(ref _presentedFrameCount);

    /// <summary>
    /// Frames decoded and never shown: the ones whose moment had already gone while a closer frame was waiting
    /// behind them. Nothing else in the pipeline discards video, so a number that keeps climbing means the
    /// picture is not being shown in step — a decoder or a renderer that cannot keep up with the clock.
    /// </summary>
    public long DroppedFrameCount => Interlocked.Read(ref _droppedFrameCount);

    /// <summary>
    /// True while the clock is being held back because a stage has nothing to work with: the sound waiting
    /// for data, or the picture unable to catch up. Playback is not failing when this is true — it is
    /// waiting for the stage to catch up, and it carries on by itself when it does.
    /// </summary>
    public bool IsStarving => _videoStarved || _audioStarved;

    /// <summary>Which stage the clock is being held back for: "video", "audio", or null when neither is.</summary>
    public string? StarvedFor => _videoStarved ? "video" : _audioStarved ? "audio" : null;

    /// <summary>
    /// The timestamp of the newest frame decoded. Diagnostics for the same reason as the rest: it says
    /// whether the decoder is keeping up, and where it has got to when it is not.
    /// </summary>
    internal TimeSpan LastDecodedTimestamp
    {
        get
        {
            long ticks = Interlocked.Read(ref _decodedTailTicks);
            return ticks > 0 ? TimeSpan.FromTicks(ticks) : TimeSpan.Zero;
        }
    }

    /// <summary>Average time one frame took to decode. Decoding is what limits playback when this is large.</summary>
    public double AverageDecodeMilliseconds
    {
        get
        {
            long frames = Interlocked.Read(ref _decodedFrameCount);
            return frames <= 0 ? 0 : Interlocked.Read(ref _decodeTicks) * 1000.0 / Stopwatch.Frequency / frames;
        }
    }

    /// <summary>
    /// Average time one frame took to hand to the renderer, including waiting for it. This is where a
    /// presenter that waits for the display shows up.
    /// </summary>
    public double AverageRenderMilliseconds
    {
        get
        {
            long frames = Interlocked.Read(ref _presentedFrameCount);
            return frames <= 0 ? 0 : Interlocked.Read(ref _renderTicks) * 1000.0 / Stopwatch.Frequency / frames;
        }
    }

    /// <summary>
    /// How far the picture is behind the sound. A small constant here is the synchronizer doing its job;
    /// a growing one means frames are arriving later than they are due. It is zero until a frame has been
    /// shown, and meaningless when the clock has not started.
    /// </summary>
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

    /// <summary>
    /// Opens a file and waits for it to be ready.
    /// <para>
    /// Setup runs on the playback thread and calls back into the host to attach the presentation
    /// surface. A caller on a thread the host marshals to — a UI thread — must await
    /// <see cref="OpenAsync"/> instead of blocking here, or the two threads end up waiting for each
    /// other.
    /// </para>
    /// </summary>
    public PlaybackResult Open(MediaLocator locator, IVideoRenderTarget? videoTarget = null)
    {
        return OpenAsync(locator, videoTarget).GetAwaiter().GetResult();
    }

    /// <summary>Opens a file without holding on to the caller's thread while the engine sets up.</summary>
    public async Task<PlaybackResult> OpenAsync(MediaLocator locator, IVideoRenderTarget? videoTarget = null)
    {
        ArgumentNullException.ThrowIfNull(locator);

        if (_disposed)
        {
            return PlaybackResult.Failure("The pipeline has already been disposed.");
        }

        MediaEngineState state = State;
        if (state is not (MediaEngineState.None or MediaEngineState.Ended or MediaEngineState.Error))
        {
            return PlaybackResult.Failure($"The pipeline is already {state}.");
        }

        Stop();
        SetState(MediaEngineState.Opening, null, null);

        // Setup runs on the playback thread, because everything it creates — the graphics device, the
        // swap chain, the audio client — belongs to the apartment it will then be used from. The host's
        // thread is an STA, so objects created there cannot simply be handed across.
        _setupResult = null;
        TaskCompletionSource<PlaybackResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _setupCompletion = completion;
        _pendingLocator = locator;
        _pendingTarget = videoTarget;
        _stopRequested = false;
        _worker = new Thread(OpenThenRun)
        {
            IsBackground = true,
            Name = "SinePlayer playback",

            // This thread reads the file, and reading the file is what feeds the sound. A video decoder has
            // its own thread to be slow on, so this one never has an excuse to wait.
            Priority = ThreadPriority.AboveNormal,
        };
        _worker.Start();

        return await completion.Task.ConfigureAwait(true);
    }

    /// <summary>The setup half, run on the playback thread.</summary>
    private PlaybackResult OpenCore(MediaLocator locator, IVideoRenderTarget? videoTarget)
    {
        PlaybackResult opened = _sourceFactory.TryOpen(locator, out IMediaSource? source);
        if (!opened.IsSuccessful || source is null)
        {
            return Fail(opened.Message);
        }

        _source = source;
        _info = source.Info;
        _videoStream = source.Info.VideoStreams.Count > 0 ? source.Info.VideoStreams[0] : null;
        _audioStream = source.Info.AudioStreams.Count > 0 ? source.Info.AudioStreams[0] : null;

        if (_videoStream is null && _audioStream is null)
        {
            return Fail("The file holds neither video nor audio.");
        }

        string? warning = null;
        if (_audioStream is not null && !TryOpenAudio(_audioStream, ref warning))
        {
            _audioStream = null;
        }

        if (_videoStream is not null)
        {
            if (videoTarget is null)
            {
                return Fail("The file has video but no presentation surface was supplied.");
            }

            PlaybackResult prepared = PrepareVideo(_videoStream, videoTarget);
            if (!prepared.IsSuccessful)
            {
                return Fail(prepared.Message);
            }
        }

        if (_clock is null)
        {
            // No audio to follow, so the wall clock drives video instead.
            _ownClock = new StopwatchClock();
            _clock = _ownClock;
        }

        SetState(MediaEngineState.Paused, null, warning);
        return PlaybackResult.Success;
    }

    public void Play()
    {
        if (_disposed || _source is null)
        {
            return;
        }

        if (State is MediaEngineState.Ended or MediaEngineState.Error)
        {
            return;
        }

        // Setting the state is what starts the clock: see ApplyClockState, which is the one place the clock
        // is touched and which will hold it back if a stage is already known to be starving.
        SetState(MediaEngineState.Playing, null, Warning);

        // The reader starts after the clock, so the first frame is never late by construction.
        _resume.Set();
    }

    public void Pause()
    {
        if (_disposed || State != MediaEngineState.Playing)
        {
            return;
        }

        _resume.Reset();
        SetState(MediaEngineState.Paused, null, Warning);
    }

    /// <summary>
    /// Jumps to a position. Playback is paused for the duration so the worker cannot decode into the
    /// state that is being flushed, and the clock is re-anchored once everything is clean.
    /// </summary>
    public PlaybackResult Seek(TimeSpan position)
    {
        if (_disposed)
        {
            return PlaybackResult.Failure("The pipeline has already been disposed.");
        }

        IMediaSource? source = _source;
        if (source is null)
        {
            return PlaybackResult.Failure("Nothing is open.");
        }

        bool wasPlaying = State == MediaEngineState.Playing;
        if (wasPlaying)
        {
            Pause();
        }

        // Everything already decoded belongs to where playback is leaving, so it is written off before
        // the lock is taken. That is what releases a worker waiting for room in a queue this seek is
        // about to empty — and the lock it holds while it waits.
        _epochs.Advance();

        PlaybackResult result;
        lock (_mediaLock)
        {
            PlaybackResult seeked = source.Seek(position);
            if (!seeked.IsSuccessful)
            {
                result = Fail(seeked.Message);
            }
            else
            {
                _audioDecoder?.Flush();

                _audioQueue?.Clear();
                _frameQueue?.Clear();
                _packetQueue?.Clear();

                // A new position means the decoder has a tail to produce again.
                _decodeDrained = false;

                // Nothing is starving at a position nothing has been read for yet: whether either stage is
                // depends on media that has not been read, and a flag left over from the old position would
                // hold the clock back when playback resumes.
                SetVideoStarved(false);
                SetAudioStarved(false);
                _sourceEnded = false;
                Interlocked.Exchange(ref _decodedTailTicks, -1);

                _audioRenderer.Flush();
                _clock?.SetPosition(position);

                // The position that was asked for is only where the file was asked to go. A demuxer lands
                // on the keyframe at or before it, so what will actually play first is earlier than this,
                // and the clock has to end up there instead — see AnchorClock.
                Interlocked.Exchange(ref _anchorPending, 1);

                // The video decoder belongs to the decoding thread and a frame-threaded decoder has to be
                // flushed by whoever owns it, so the flush is asked for rather than done: it happens on that
                // thread, before it decodes anything else.
                lock (_videoDecodeLock)
                {
                    _videoFlushPending = true;
                }

                result = PlaybackResult.Success;
            }
        }

        if (!result.IsSuccessful)
        {
            return result;
        }

        if (wasPlaying)
        {
            Play();
        }
        else if (State == MediaEngineState.Ended)
        {
            // A seek from the end is a new starting point rather than a finished one.
            SetState(MediaEngineState.Paused, null, Warning);
        }

        return result;
    }

    /// <summary>Stops playback and releases everything it was holding, leaving the pipeline reusable.</summary>
    public void Stop()
    {
        _stopRequested = true;
        _resume.Set();

        Thread? worker = _worker;
        _worker = null;
        if (worker is not null && worker != Thread.CurrentThread)
        {
            worker.Join(WaitForAudioMilliseconds);
        }

        ReleaseMedia();

        // A caller waiting on an open is released rather than left hanging.
        _setupCompletion?.TrySetResult(_setupResult ?? PlaybackResult.Failure("Opening was cancelled."));
        _setupCompletion = null;

        SetState(MediaEngineState.None, null, null);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        _resume.Dispose();
    }

    private bool TryOpenAudio(AudioStreamInfo stream, ref string? warning)
    {
        PlaybackResult created = _audioDecoderFactory.TryCreate(_source!, stream, DecoderOptions.Default, out IAudioDecoder? decoder);
        if (!created.IsSuccessful || decoder is null)
        {
            warning = $"Audio is not playing: {created.Message}";
            return false;
        }

        PlaybackResult configured = _audioRenderer.Configure(stream);
        if (!configured.IsSuccessful)
        {
            decoder.Dispose();
            warning = $"Audio is not playing: {configured.Message}";
            return false;
        }

        _audioDecoder = decoder;
        _clock = _audioRenderer.Clock;

        // How much audio the reader keeps queued ahead of the device, and how low it may fall before the
        // reader fills it again. The minimum is the sound's tolerance for a slow file: everything below it
        // has already been handed over, so a stall of that length is absorbed rather than heard.
        _audioMinimumSamples = Math.Max(1, (long)stream.SampleRate * AudioMinimumMilliseconds / 1000);
        _audioWakeSamples = _audioMinimumSamples / 2;
        _audioQueue?.Dispose();
        _audioQueue = new AudioQueue(_epochs);
        return true;
    }

    private PlaybackResult PrepareVideo(VideoStreamInfo stream, IVideoRenderTarget target)
    {
        PlaybackResult rendererResult = _videoRendererFactory.TryCreate(out IVideoRenderer? renderer);
        if (!rendererResult.IsSuccessful || renderer is null)
        {
            return rendererResult.IsSuccessful ? PlaybackResult.Failure("No video renderer was created.") : rendererResult;
        }

        PlaybackResult created = _videoDecoderFactory.TryCreate(_source!, stream, DecoderOptions.Default, out IVideoDecoder? decoder);
        if (!created.IsSuccessful || decoder is null)
        {
            renderer.Dispose();
            return created.IsSuccessful ? PlaybackResult.Failure("No video decoder was created.") : created;
        }

        PlaybackResult configured = renderer.Configure(stream, target);
        if (!configured.IsSuccessful)
        {
            decoder.Dispose();
            renderer.Dispose();
            return configured;
        }

        _videoRenderer = renderer;
        _videoDecoder = decoder;

        // The queue the reader fills: no maximum, because a reader stopped here is a reader that stops
        // producing sound whenever the picture is slow.
        _packetQueue?.Dispose();
        _packetQueue = new PacketQueue(_epochs);
        _decodeDrained = false;

        // Eight frames is about 130 ms of look-ahead at 60 fps: enough that a seek has little to discard,
        // and small enough that decoded frames — the largest thing held here — do not pile up.
        _frameQueue?.Dispose();
        _frameQueue = new FrameQueue(FrameQueueCapacity, _epochs);

        Interlocked.Exchange(ref _decodedTailTicks, -1);
        return PlaybackResult.Success;
    }

    private void OpenThenRun()
    {
        try
        {
            MediaLocator? locator = _pendingLocator;
            _setupResult = locator is null
                ? PlaybackResult.Failure("No file was given to open.")
                : OpenCore(locator, _pendingTarget);
        }
        catch (Exception exception)
        {
            _setupResult = Fail(exception.Message);
        }
        finally
        {
            _pendingLocator = null;
            _pendingTarget = null;

            // The caller is released only once the state is settled, so what it sees afterwards is final.
            _setupCompletion?.TrySetResult(_setupResult ?? PlaybackResult.Failure("Opening the file produced no result."));
        }

        if (_setupResult?.IsSuccessful != true)
        {
            return;
        }

        // Audio runs on its own thread so that the reader never waits on the device: doing that on the
        // same thread starved video decoding and froze the picture while the sound kept playing. Video
        // decoding runs on a third one, so that a slow frame cannot starve the sound the other way round.
        StartAudioThread();
        StartVideoThread();
        StartVideoDecodeThread();

        try
        {
            RunCore();
        }
        catch (Exception exception)
        {
            SetState(MediaEngineState.Error, exception.Message, Warning);
        }
    }

    private void RunCore()
    {
        IMediaSource? source = _source;
        if (source is null)
        {
            return;
        }

        while (!_stopRequested)
        {
            // Blocked while paused, and woken again by Play.
            _resume.Wait(10);
            if (_stopRequested)
            {
                return;
            }

            if (State != MediaEngineState.Playing)
            {
                continue;
            }

            // The reader is never stopped by a full queue — the queues it feeds have no maximum — so the
            // only thing that paces it is its own forward reading: fill both queues to their minimums, then
            // idle until one of them runs low. That is what keeps the picture's pace from becoming the
            // sound's pace, because the reader is the only cursor and the sound comes from it.
            if (!_readerFilling)
            {
                if (!NeedsBuffering())
                {
                    Thread.Sleep(2);
                    continue;
                }

                _readerFilling = true;
            }
            else if (IsBuffered())
            {
                _readerFilling = false;
                continue;
            }

            PlaybackResult routed = PlaybackResult.Success;
            MediaPacket? packet = null;
            bool ending = false;
            bool handedOn = false;

            // Held while one packet is read and handed on, so a seek can flush the decoders and the queues
            // without racing the worker. The epoch is taken before the lock, though: a seek that is already
            // waiting for it has moved playback on, and a packet read for where it used to be must not be
            // decoded as if it belonged to where it went — a flushed decoder would refuse it outright.
            long epoch = _epochs.Current;
            lock (_mediaLock)
            {
                PlaybackResult read = source.TryReadPacket(out packet);
                if (!read.IsSuccessful)
                {
                    routed = read;
                }
                else if (packet is null)
                {
                    // The end of the file is only the end of it if no seek is waiting to move it.
                    ending = epoch == _epochs.Current;
                }
                else if (epoch == _epochs.Current)
                {
                    routed = Route(packet, epoch, out handedOn);
                }
            }

            // A packet the decoder queue took is the queue's to release, and the one it did not is ours.
            if (!handedOn)
            {
                packet?.Dispose();
            }

            if (!routed.IsSuccessful)
            {
                Fail(routed.Message);
                return;
            }

            if (ending)
            {
                // Nothing more will be read, which is what tells the rest of the pipeline that a queue
                // emptying now is the file playing out rather than a stage that has fallen behind: the
                // clock is no longer held back for a sound that is merely finishing.
                _sourceEnded = true;

                // The worker stays alive past the end of the file and goes idle, because a seek from the
                // end has to be able to start playback again and the worker is the only thing that reads.
                Finish();
                _readerFilling = false;
            }
        }
    }

    /// <summary>True once both queues hold their minimum, which is what makes the reader idle.</summary>
    private bool IsBuffered()
    {
        AudioQueue? audio = _audioQueue;
        if (audio is not null && audio.QueuedSamples < _audioMinimumSamples)
        {
            return false;
        }

        PacketQueue? packets = _packetQueue;
        if (packets is not null && packets.Count < VideoPacketMinimum)
        {
            return false;
        }

        return true;
    }

    /// <summary>True once either queue has run low, which is what wakes the reader again.</summary>
    private bool NeedsBuffering()
    {
        AudioQueue? audio = _audioQueue;
        if (audio is not null && audio.QueuedSamples < _audioWakeSamples)
        {
            return true;
        }

        PacketQueue? packets = _packetQueue;
        if (packets is not null && packets.Count < VideoPacketWake)
        {
            return true;
        }

        return false;
    }

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
    private void AnchorClock(TimeSpan mediaTimestamp)
    {
        if (Interlocked.Exchange(ref _anchorPending, 0) == 0)
        {
            return;
        }

        _clock?.SetPosition(mediaTimestamp);
    }

    private PlaybackResult Route(MediaPacket packet, long epoch, out bool handedOn)
    {
        handedOn = false;

        IAudioDecoder? audioDecoder = _audioDecoder;
        if (audioDecoder is not null && packet.StreamIndex == _audioStream!.Index)
        {
            // Audio is decoded right here rather than handed on: it costs almost nothing, and every packet
            // passed to another thread is another chance for the sound to be late.
            return DecodeAudio(packet, epoch);
        }

        if (_videoDecoder is not null && packet.StreamIndex == _videoStream!.Index)
        {
            PacketQueue? packetQueue = _packetQueue;
            if (packetQueue is null)
            {
                return PlaybackResult.Failure("The video packet queue is not available.");
            }

            // Handed on rather than decoded here: a reader that waits for a video decoder is a reader that
            // cannot read the audio packets interleaved behind the frame being decoded. Waiting for room is
            // a different matter and is right — it is what paces the reader — and it is safe because the
            // thread that empties this queue never waits for the picture: it decodes, hands frames to a
            // queue that waits for the picture to take one, and comes back for more (see FrameQueue).
            handedOn = packetQueue.TryEnqueue(packet, epoch);
            return PlaybackResult.Success;
        }

        // Streams nobody selected, such as a second audio track, are simply skipped.
        return PlaybackResult.Success;
    }

    private PlaybackResult DecodeAudio(MediaPacket packet, long epoch)
    {
        IAudioDecoder? audioDecoder = _audioDecoder;
        if (audioDecoder is null)
        {
            return PlaybackResult.Success;
        }

        PlaybackResult decoded = audioDecoder.TryDecode(packet, out IReadOnlyList<RawAudioBuffer> buffers);
        if (!decoded.IsSuccessful)
        {
            return decoded;
        }

        AudioQueue? audioQueue = _audioQueue;
        if (audioQueue is null)
        {
            return PlaybackResult.Failure("The audio queue is not available.");
        }

        foreach (RawAudioBuffer buffer in buffers)
        {
            // The queue owns the buffer from here; the audio thread disposes it after playing it.
            if (!audioQueue.TryEnqueue(buffer, epoch))
            {
                buffer.Dispose();
            }
        }

        return PlaybackResult.Success;
    }

    private PlaybackResult DecodeVideo(MediaPacket packet, out IReadOnlyList<RawVideoFrame>? frames)
    {
        frames = null;

        IVideoDecoder? videoDecoder = _videoDecoder;
        if (videoDecoder is null)
        {
            return PlaybackResult.Success;
        }

        long started = Stopwatch.GetTimestamp();
        PlaybackResult decoded = videoDecoder.TryDecode(packet, out IReadOnlyList<RawVideoFrame> produced);
        Interlocked.Add(ref _decodeTicks, Stopwatch.GetTimestamp() - started);

        if (!decoded.IsSuccessful)
        {
            return decoded;
        }

        frames = produced;
        Interlocked.Add(ref _decodedFrameCount, produced.Count);
        return PlaybackResult.Success;
    }

    /// <summary>
    /// Hands decoded frames to the queue that shows them, and records how far the decoder has got. Frames go
    /// to the queue rather than straight to the renderer, so decoding can run on while the presentation
    /// thread waits for each frame's moment; the wait for room is the picture's own back-pressure.
    /// </summary>
    private void QueueFrames(IReadOnlyList<RawVideoFrame>? frames, long epoch)
    {
        if (frames is null)
        {
            return;
        }

        foreach (RawVideoFrame frame in frames)
        {
            FrameQueue? frameQueue = _frameQueue;
            if (frameQueue is null || !frameQueue.TryEnqueue(frame, epoch))
            {
                frame.Dispose();
                continue;
            }

            // What the decoder has produced is what says whether the picture can still catch up with the
            // clock; a frame handed over is a frame that will be shown.
            Interlocked.Exchange(ref _decodedTailTicks, frame.Timestamp.Ticks);
        }
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
        long tailTicks = Interlocked.Read(ref _decodedTailTicks);
        if (clock is null || tailTicks < 0 || _sourceEnded || Volatile.Read(ref _anchorPending) != 0)
        {
            // Nothing decoded yet, nothing left to decode, or a position whose clock has not been anchored:
            // none of those is a picture that has fallen behind.
            SetVideoStarved(false);
            return;
        }

        TimeSpan behind = clock.Position - TimeSpan.FromTicks(tailTicks);
        bool starved = _videoStarved
            ? behind > TimeSpan.FromMilliseconds(VideoStarvationResumeMilliseconds)
            : behind > TimeSpan.FromMilliseconds(VideoStarvationMilliseconds);
        SetVideoStarved(starved);
    }

    /// <summary>Shows one frame. Whether it was the closest frame there was decided before this.</summary>
    private PlaybackResult Show(RawVideoFrame frame)
    {
        IVideoRenderer? renderer = _videoRenderer;
        if (renderer is null)
        {
            return PlaybackResult.Failure("The video renderer is not available.");
        }

        long started = Stopwatch.GetTimestamp();
        PlaybackResult rendered = renderer.Render(frame);
        Interlocked.Add(ref _renderTicks, Stopwatch.GetTimestamp() - started);

        if (rendered.IsSuccessful)
        {
            Interlocked.Increment(ref _presentedFrameCount);
            Interlocked.Exchange(ref _pictureTicks, frame.Timestamp.Ticks);
        }

        return rendered;
    }

    private void Finish()
    {
        // What the decoders still hold was made for the position the file was read up to, so a seek that
        // has since moved on makes it worthless.
        long epoch = _epochs.Current;

        // The packets are all in, so the decoder can finish what the file holds.
        _packetQueue?.Complete();
        WaitForVideoDecodeToDrain();

        if (State == MediaEngineState.Error)
        {
            // The decoder failed on the way out, and ending playback cleanly would hide it.
            return;
        }

        PlaybackResult audio = DrainAudio(epoch);
        if (!audio.IsSuccessful)
        {
            Fail(audio.Message);
            return;
        }

        WaitForFramesToFinish();
        WaitForAudioToFinish();

        if (State == MediaEngineState.Error)
        {
            // Something failed while what was left played out — a device that stopped consuming, say — and
            // ending playback cleanly would hide it.
            return;
        }

        if (epoch != _epochs.Current)
        {
            // A seek arrived while the end of the file was being played out, so this is not the end of
            // where playback was sent: the reader goes back for more from the new position.
            return;
        }

        // The worker waits on this to be woken, and it goes idle once playback has ended, so leaving it
        // set would have it wake and find nothing to do in a loop.
        _resume.Reset();
        SetState(MediaEngineState.Ended, null, Warning);
    }

    private PlaybackResult DrainAudio(long epoch)
    {
        IAudioDecoder? decoder = _audioDecoder;
        if (decoder is null)
        {
            return PlaybackResult.Success;
        }

        PlaybackResult drained = decoder.Drain(out IReadOnlyList<RawAudioBuffer> buffers);
        if (!drained.IsSuccessful)
        {
            return drained;
        }

        AudioQueue? queue = _audioQueue;
        if (queue is null)
        {
            foreach (RawAudioBuffer buffer in buffers)
            {
                buffer.Dispose();
            }

            return PlaybackResult.Success;
        }

        foreach (RawAudioBuffer buffer in buffers)
        {
            if (!queue.TryEnqueue(buffer, epoch))
            {
                buffer.Dispose();
            }
        }

        return PlaybackResult.Success;
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
        long limit = _audioStream is not null && _audioStream.SampleRate > 0
            ? _audioStream.SampleRate * QueuedLimitMilliseconds / 1000
            : 0;

        while (!_stopRequested)
        {
            WatchDeviceProgress();

            if (_epochs.Current != epoch)
            {
                // A seek has moved on. The block is let go of by the caller, and waiting for room for it
                // would be waiting on behalf of a position nobody wants.
                return true;
            }

            if (State != MediaEngineState.Playing)
            {
                // Paused: the device holds what it has, so there is no room coming. The block waits here
                // and is submitted when playback resumes.
                Thread.Sleep(5);
                continue;
            }

            if (limit <= 0 || _audioRenderer.QueuedSampleCount < limit)
            {
                return true;
            }

            Thread.Sleep(2);
        }

        return false;
    }

    /// <summary>
    /// Says so when the device holds audio and its position stands still.
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

        long now = Stopwatch.GetTimestamp();
        if (now - _deviceCheckedAt < DeviceCheckTicks)
        {
            return;
        }

        _deviceCheckedAt = now;

        bool expected = State == MediaEngineState.Playing
            && !_videoStarved
            && !_audioStarved
            && !_sourceEnded
            && Volatile.Read(ref _anchorPending) == 0
            && clock.IsRunning;

        TimeSpan position = clock.Position;
        if (!expected || position != _devicePosition)
        {
            // Either nothing is expected of the device right now, or it has moved: both mean the question
            // of it being stuck does not arise.
            _devicePosition = position;
            _deviceMovedAt = now;

            if (Warning == DeviceNotAdvancingWarning)
            {
                SetWarning(null);
            }

            return;
        }

        if (now - _deviceMovedAt >= DeviceNoticeTicks && Warning != DeviceNotAdvancingWarning)
        {
            SetWarning(DeviceNotAdvancingWarning);
        }
    }

    private void StartAudioThread()
    {
        if (_audioQueue is null || _audioThread is not null)
        {
            return;
        }

        _audioThread = new Thread(RunAudio)
        {
            IsBackground = true,
            Name = "SinePlayer audio",

            // The device runs dry if this thread is kept off the processor for longer than its buffer
            // holds, and decoding runs on threads that are happy to take the processor from it. Audio is
            // the one stage that cannot be late.
            Priority = ThreadPriority.AboveNormal,
        };
        _audioThread.Start();
    }

    private void StartVideoThread()
    {
        if (_frameQueue is null || _videoThread is not null)
        {
            return;
        }

        _videoThread = new Thread(RunVideo)
        {
            IsBackground = true,
            Name = "SinePlayer video",
        };
        _videoThread.Start();
    }

    private void StartVideoDecodeThread()
    {
        if (_packetQueue is null || _decodeThread is not null)
        {
            return;
        }

        // Left at the ordinary priority on purpose. Decoding is the one stage whose lateness can be
        // absorbed — a frame that misses its moment is dropped — so when the machine is at its limit it
        // should be this thread that waits, and never the one feeding the sound.
        _decodeThread = new Thread(RunVideoDecode)
        {
            IsBackground = true,
            Name = "SinePlayer video decode",
        };
        _decodeThread.Start();
    }

    /// <summary>
    /// Decodes the packets the reader hands over, on a thread of its own, so that however long one frame
    /// takes — a 4K keyframe, say — the packets behind it keep moving. The audio packets in a file are
    /// interleaved with the video ones, so a reader that stops for a video decode stops producing sound.
    /// </summary>
    private void RunVideoDecode()
    {
        try
        {
            while (!_stopRequested)
            {
                PacketQueue? queue = _packetQueue;
                if (queue is null)
                {
                    return;
                }

                if (State != MediaEngineState.Playing)
                {
                    // Nothing to decode for: the picture is not moving, so no frame is being used up. The
                    // frame queue is a bound on how far ahead this thread could run anyway; standing still
                    // while the picture is standing still is what keeps a pause from spending the look-ahead
                    // the resume needs.
                    Thread.Sleep(5);
                    continue;
                }

                if (!queue.TryDequeue(out MediaPacket? packet, out long epoch, 20) || packet is null)
                {
                    if (queue.IsCompleted)
                    {
                        if (!_decodeDrained)
                        {
                            DrainVideoAtEndOfStream();
                        }

                        // The thread stays alive at the end of the file, as the reader does, because a
                        // replay has to have something to decode with. A seek clears the flag and the
                        // decoder has a tail to produce again.
                        Thread.Sleep(5);
                    }

                    continue;
                }

                using (packet)
                {
                    IReadOnlyList<RawVideoFrame>? frames = null;
                    PlaybackResult decoded = PlaybackResult.Success;
                    lock (_videoDecodeLock)
                    {
                        // Held while decoding, so the thread that owns the decoder is the only one that
                        // touches it. The epoch the packet was queued with is what decides whether there is
                        // any point: a seek since it was read means the decoder has moved on.
                        if (_videoFlushPending)
                        {
                            _videoFlushPending = false;
                            _videoDecoder?.Flush();

                            // A new position means no frame has been decoded for it yet.
                            Interlocked.Exchange(ref _decodedTailTicks, -1);
                        }

                        if (_epochs.Current == epoch)
                        {
                            decoded = DecodeVideo(packet, out frames);
                        }
                    }

                    if (!decoded.IsSuccessful)
                    {
                        Fail(decoded.Message);
                        return;
                    }

                    // Queued with a wait for room, which is the picture's own back-pressure: the frames
                    // behind this one cannot be taken until the picture has taken one, and nothing else in
                    // the pipeline depends on this thread making progress. The queue releases the wait for
                    // a seek as well, so a position nobody wants does not hold this thread either.
                    QueueFrames(frames, epoch);
                }
            }
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
        }
    }

    /// <summary>
    /// The end of the file is the end of the packets, not of the frames inside them: whatever the decoder
    /// still holds is decoded and queued here, and the reader is told the tail is on its way.
    /// </summary>
    private void DrainVideoAtEndOfStream()
    {
        PlaybackResult drained;
        lock (_videoDecodeLock)
        {
            drained = DrainVideo(_epochs.Current);
        }

        if (!drained.IsSuccessful)
        {
            Fail(drained.Message);
            return;
        }

        _decodeDrained = true;
    }

    /// <summary>
    /// Shows the frame closest to the moment, and holds the clock back when there is no frame close enough.
    /// <para>
    /// This is the only thread that waits for the clock, because the picture is the follower: the sound
    /// leads, and a thread waiting for the leader can never hold the leader up. Nothing here waits for a
    /// particular frame either — a frame whose moment has passed is shown anyway when it is the closest one
    /// there is, so the picture cannot end up waiting for something that is no longer coming, and a frame
    /// the clock has left behind is given up on only when a closer one is already waiting behind it.
    /// </para>
    /// </summary>
    private void RunVideo()
    {
        try
        {
            RawVideoFrame? held = null;
            long heldEpoch = 0;

            while (!_stopRequested)
            {
                FrameQueue? queue = _frameQueue;
                if (queue is null)
                {
                    held?.Dispose();
                    return;
                }

                if (State != MediaEngineState.Playing)
                {
                    // Paused: the queue keeps what it holds, the clock is not moving, and nothing is shown.
                    Thread.Sleep(5);
                    continue;
                }

                if (held is not null && _epochs.Current != heldEpoch)
                {
                    // A seek has moved playback on: this frame belongs to where it used to be.
                    held.Dispose();
                    held = null;
                }

                UpdateVideoStarvation();

                if (held is null)
                {
                    if (!queue.TryDequeue(out held, out heldEpoch, 20) || held is null)
                    {
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

                // Give up on a frame the clock has left behind, but only when the queue already holds a
                // closer one: the frame dropped is one whose moment has gone, and never the newest there is,
                // so a picture that is merely late keeps being shown rather than freezing.
                if (_synchronizer.Evaluate(held.Timestamp, Position) == FrameSyncAction.Drop
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
                    // and nothing in the pipeline waits on this thread.
                    Thread.Sleep(1);
                    continue;
                }

                PlaybackResult shown = Show(held);
                held.Dispose();
                held = null;

                if (!shown.IsSuccessful)
                {
                    Fail(shown.Message);
                    return;
                }
            }

            held?.Dispose();
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
        }
    }

    /// <summary>
    /// Hands audio to the device. The device is what moves the clock, so this thread is the one that decides
    /// whether time may pass: while there is nothing to hand over the clock is held back, and it is let go
    /// again the moment there is something. Nothing is ever dropped here, and the only wait is for room in
    /// the device's buffer — a wait that ends when the device plays, or never, in which case the whole
    /// pipeline waits with it and the device is reported as the reason.
    /// </summary>
    private void RunAudio()
    {
        try
        {
            while (!_stopRequested)
            {
                AudioQueue? queue = _audioQueue;
                if (queue is null)
                {
                    return;
                }

                if (State != MediaEngineState.Playing)
                {
                    // Paused: nothing new should reach the device, and the queue keeps what it holds.
                    Thread.Sleep(5);
                    continue;
                }

                WatchDeviceProgress();

                if (!queue.TryDequeue(out RawAudioBuffer? buffer, 50) || buffer is null)
                {
                    // Nothing to play. Time moving without the sound is what lets a picture run ahead of a
                    // sound that has not arrived, so the clock waits here — unless the file has ended, where
                    // an empty queue is the sound playing out rather than a sound falling behind.
                    SetAudioStarved(!_sourceEnded);
                    continue;
                }

                using (buffer)
                {
                    // There is something to hand over, so whatever the sound was waiting for has arrived.
                    SetAudioStarved(false);

                    long epoch = _epochs.Current;

                    // The sound is what the clock counts, so the first block handed to the device after a
                    // seek is the best anchor there is: it is the first thing that will be heard.
                    AnchorClock(buffer.Timestamp);

                    if (!WaitForAudioRoom(epoch))
                    {
                        return;
                    }

                    if (_epochs.Current != epoch)
                    {
                        // A seek landed while this block was waiting for room, so the block belongs to the
                        // position playback has left. It is let go of rather than played.
                        continue;
                    }

                    PlaybackResult submitted = _audioRenderer.Submit(buffer);
                    if (!submitted.IsSuccessful)
                    {
                        Fail(submitted.Message);
                        return;
                    }

                    if (_epochs.Current != epoch)
                    {
                        // This block was produced for where playback used to be and may already be in the
                        // device, which the seek has since refilled — a device that stopped consuming makes
                        // the submit of a block outlast the seek that interrupted it. Letting the device go
                        // of what it holds is what keeps a seek from playing a moment of where it came from.
                        _audioRenderer.Flush();
                    }
                }
            }
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
        }
    }

    private PlaybackResult DrainVideo(long epoch)
    {
        IVideoDecoder? decoder = _videoDecoder;
        if (decoder is null)
        {
            return PlaybackResult.Success;
        }

        PlaybackResult drained = decoder.Drain(out IReadOnlyList<RawVideoFrame> frames);
        if (!drained.IsSuccessful)
        {
            return drained;
        }

        QueueFrames(frames, epoch);
        return PlaybackResult.Success;
    }

    /// <summary>
    /// Waits for the end of the file to arrive, bounded by <see cref="WaitForAudioMilliseconds"/>. The bound
    /// is measured rather than counted: <c>Thread.Sleep(2)</c> rounds up to the system timer's 15.6 ms, so
    /// counting sleeps made every one of these bounds eight times longer than it read.
    /// </summary>
    private void WaitUntilFinished(Func<bool> finished)
    {
        var waitedFor = Stopwatch.StartNew();
        while (!_stopRequested && waitedFor.ElapsedMilliseconds < WaitForAudioMilliseconds && !finished())
        {
            Thread.Sleep(2);
        }
    }

    /// <summary>
    /// The decoder still holds packets that were read but not yet decoded, so the end of the file is not
    /// the end of the frames until it has said so.
    /// </summary>
    private void WaitForVideoDecodeToDrain()
    {
        if (_packetQueue is null)
        {
            return;
        }

        WaitUntilFinished(() => _decodeDrained);
    }

    /// <summary>
    /// The frames still queued play out at the clock's pace, so this waits for them rather than giving up on
    /// them — and it waits for a clock that is moving, because once the file has run out there may be nothing
    /// left to move it. A clock that has stood still for long enough ends the wait: frames nobody can show
    /// are not worth the time.
    /// </summary>
    private void WaitForFramesToFinish()
    {
        FrameQueue? queue = _frameQueue;
        if (queue is null)
        {
            return;
        }

        var waitedFor = Stopwatch.StartNew();
        var stalledFor = Stopwatch.StartNew();
        TimeSpan last = Position;

        while (!_stopRequested && waitedFor.ElapsedMilliseconds < WaitForAudioMilliseconds)
        {
            if (queue.Count == 0)
            {
                return;
            }

            TimeSpan position = Position;
            if (position != last)
            {
                last = position;
                stalledFor.Restart();
            }
            else if (stalledFor.ElapsedMilliseconds >= StalledClockMilliseconds)
            {
                return;
            }

            Thread.Sleep(2);
        }
    }

    /// <summary>
    /// The device still holds whatever was handed to it, so the end of playback is not the end of the
    /// stream: the file has not finished until what is queued has been heard.
    /// </summary>
    private void WaitForAudioToFinish()
    {
        if (_audioDecoder is null)
        {
            return;
        }

        WaitUntilFinished(() => _audioRenderer.QueuedSampleCount + (_audioQueue?.QueuedSamples ?? 0) <= 0);
    }

    private PlaybackResult Fail(string message)
    {
        SetState(MediaEngineState.Error, message, Warning);
        return PlaybackResult.Failure(message);
    }

    private void ReleaseMedia()
    {
        // Closing a queue releases the thread waiting on it.
        _frameQueue?.Dispose();
        _frameQueue = null;

        Thread? videoThread = _videoThread;
        _videoThread = null;
        if (videoThread is not null && videoThread != Thread.CurrentThread)
        {
            videoThread.Join(WaitForAudioMilliseconds);
        }

        _audioQueue?.Dispose();
        _audioQueue = null;

        // Undecoded packets are released before the thread reading them is joined, or it would wait for
        // more of them.
        _packetQueue?.Dispose();
        _packetQueue = null;

        Thread? decodeThread = _decodeThread;
        _decodeThread = null;
        if (decodeThread is not null && decodeThread != Thread.CurrentThread)
        {
            decodeThread.Join(WaitForAudioMilliseconds);
        }

        // A device that stopped consuming leaves the audio thread waiting inside a submit, which would
        // hold the join up for as long as it likes. Letting the device go of its buffer releases it.
        _audioRenderer.Flush();

        Thread? audioThread = _audioThread;
        _audioThread = null;
        if (audioThread is not null && audioThread != Thread.CurrentThread)
        {
            audioThread.Join(WaitForAudioMilliseconds);
        }

        if (_audioDecoder is not null)
        {
            try
            {
                _audioRenderer.Flush();
            }
            catch (Exception)
            {
                // The device may already be gone; there is nothing to flush in that case.
            }

            _audioDecoder.Dispose();
            _audioDecoder = null;
        }

        _videoDecoder?.Dispose();
        _videoDecoder = null;

        // The renderer was created by this pipeline, on its own thread, so it is the pipeline's to let go.
        _videoRenderer?.Dispose();
        _videoRenderer = null;

        _source?.Dispose();
        _source = null;

        _ownClock = null;
        _clock = null;
        _videoStream = null;
        _audioStream = null;
        _info = null;
        _readerFilling = false;
        _sourceEnded = false;
        _videoStarved = false;
        _audioStarved = false;
        _devicePosition = TimeSpan.Zero;
        _deviceMovedAt = 0;
        _deviceCheckedAt = 0;
        Interlocked.Exchange(ref _presentedFrameCount, 0);
        Interlocked.Exchange(ref _droppedFrameCount, 0);
        Interlocked.Exchange(ref _decodedFrameCount, 0);
        Interlocked.Exchange(ref _decodeTicks, 0);
        Interlocked.Exchange(ref _renderTicks, 0);
        Interlocked.Exchange(ref _pictureTicks, 0);
        Interlocked.Exchange(ref _decodedTailTicks, -1);
        Interlocked.Exchange(ref _anchorPending, 0);
    }

    /// <summary>
    /// Starts or stops the clock to match what playback is doing: it runs while playing and while nothing is
    /// waiting, and stands still otherwise.
    /// <para>
    /// Every wait in the pipeline ends here, which is why this is the one place the clock is touched. Pausing,
    /// ending, a picture that cannot catch up and a sound with nothing to play all arrive at the same
    /// decision: time stops, and every thread that follows time stops with it — and because the readers of
    /// the clock are the only ones waiting on it, stopping it can never stop the pipelines that would end
    /// the wait.
    /// </para>
    /// </summary>
    private void ApplyClockState()
    {
        IPlaybackClock? clock = _clock;
        if (clock is null)
        {
            return;
        }

        lock (_clockLock)
        {
            bool shouldRun = State == MediaEngineState.Playing && !_videoStarved && !_audioStarved;

            try
            {
                if (shouldRun)
                {
                    clock.Start();
                }
                else
                {
                    clock.Pause();
                }
            }
            catch (Exception)
            {
                // A device that cannot be started or stopped is a device whose position will not move, and
                // WatchDeviceProgress is what reports that. Letting it take playback down would be a worse
                // answer than the wait it has already become.
            }
        }
    }

    /// <summary>Holds the clock back for the picture, or lets it go, according to what the picture can do.</summary>
    private void SetVideoStarved(bool value)
    {
        if (_videoStarved == value)
        {
            return;
        }

        _videoStarved = value;
        ApplyClockState();
    }

    /// <summary>Holds the clock back for the sound, or lets it go, according to whether there is any to play.</summary>
    private void SetAudioStarved(bool value)
    {
        if (_audioStarved == value)
        {
            return;
        }

        _audioStarved = value;
        ApplyClockState();
    }

    /// <summary>Changes the warning without disturbing the state or the error, and only when it differs.</summary>
    private void SetWarning(string? warning)
    {
        if (Warning == warning)
        {
            return;
        }

        SetState(State, Error, warning);
    }

    private void SetState(MediaEngineState state, string? error, string? warning)
    {
        bool changed;
        lock (_stateLock)
        {
            changed = _state != state || _error != error || _warning != warning;
            _state = state;
            _error = error;
            _warning = warning;
        }

        if (!changed)
        {
            return;
        }

        // The clock follows the state of playback, and this is the one place every state change passes
        // through, so a pause, an end and a failure all stop time without any of them having to remember to.
        ApplyClockState();

        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception)
        {
            // A misbehaving subscriber must not take playback down with it.
        }
    }
}
