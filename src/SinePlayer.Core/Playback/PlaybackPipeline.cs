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

    private bool _disposed = false;
    private volatile MediaEngineState _state = MediaEngineState.None;
    private volatile string? _error;

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

    private volatile bool _stopRequested;
    private volatile bool _sourceEnded;
    private volatile bool _videoStarved;
    private volatile bool _audioStarved;

    private readonly EpochGate _epochs = new();
    private long _seekPositionTicks;

    private int _anchorPending;
    private long _tailDecodedFrameTicks = -1;

    #endregion

    #region Public API

    public event Action<MediaEngineState, string?>? StateChanged;
    public event Action<string>? WarningRaised;

    public MediaEngineState State => _state;
    public string? Error => _error;

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

        MediaEngineState state = State;
        if (state is not (MediaEngineState.None or MediaEngineState.Ended or MediaEngineState.Error))
        {
            return PlaybackOperationResult.Failure($"The pipeline is already {state}.");
        }

        StopCore();
        SetState(MediaEngineState.Opening);

        PlaybackOperationResult result = PreparePlayback(locator, videoTarget);
        if (!result.IsSuccessful)
        {
            StopCore();
            return result;
        }

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

        SetState(MediaEngineState.Paused);
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
        PlaybackOperationResult rendererResult = _videoRendererFactory.TryCreate(out IVideoRenderer? renderer);
        if (!rendererResult.IsSuccessful || renderer is null)
        {
            return rendererResult.IsSuccessful ? PlaybackOperationResult.Failure("No video renderer was created.") : rendererResult;
        }

        PlaybackOperationResult created = _videoDecoderFactory.TryCreate(_source!, stream, DecoderOptions.Default, out IVideoDecoder? decoder);
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
            _videoTarget?.SurfaceSizeChanged -= OnSurfaceSizeChanged;
            _videoTarget = target;
            target.SurfaceSizeChanged += OnSurfaceSizeChanged;
        }

        _packetQueue?.Dispose();
        _packetQueue = new MediaQueue<MediaPacket>();

        _frameQueue?.Dispose();
        _frameQueue = new MediaQueue<RawVideoFrame>(limit: FrameQueueCapacity);

        Interlocked.Exchange(ref _tailDecodedFrameTicks, -1);
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

        if (State == MediaEngineState.Ended)
        {
            return PlaybackOperationResult.Failure("Playback has ended.");
        }

        if (State == MediaEngineState.Error)
        {
            return PlaybackOperationResult.Failure($"Playback has failed: {_error}");
        }

        SetState(MediaEngineState.Playing);
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

        if (State != MediaEngineState.Playing)
        {
            return PlaybackOperationResult.Success;
        }

        SetState(MediaEngineState.Paused);
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

        bool wasPlaying = State == MediaEngineState.Playing;
        if (wasPlaying)
        {
            PauseCore();
        }

        Volatile.Write(ref _seekPositionTicks, position.Ticks);
        _epochs.Advance();

        _sourceEnded = false;
        SetVideoStarved(false);
        SetAudioStarved(false);

        Interlocked.Exchange(ref _tailDecodedFrameTicks, -1);

        _clock?.SetPosition(position);

        // The position that was asked for is only where the file was asked to go. A demuxer lands on the
        // keyframe at or before it, so what will actually play first is earlier than this, and the clock has
        // to end up there instead — see AnchorClock.
        Interlocked.Exchange(ref _anchorPending, 1);

        // The video decoder belongs to the decoding thread and a frame-threaded decoder has to be flushed by
        // whoever owns it, so the flush is asked for rather than done: it happens on that thread, before it
        // decodes anything else.
        lock (_videoDecodeLock)
        {
            _videoFlushPending = true;
        }

        // A seek is the one change that does not pass through the state machine — a seek while paused
        // changes nothing about the state and everything about the position — so the threads are told here:
        // the decoder holding a frame, the renderer holding a picture and the audio thread holding a block
        // all learn that what they have belongs to where playback used to be.
        SignalChange();

        if (wasPlaying)
        {
            // The pause above was only for the duration of the request, so what playback was doing it goes
            // back to doing. A request the reader turns out to be unable to carry out is reported by it and
            // leaves playback exactly where it is.
            PlayCore();
        }
        else if (State == MediaEngineState.Ended)
        {
            // A seek from the end is a new starting point rather than a finished one.
            SetState(MediaEngineState.Paused, null);
        }

        return PlaybackOperationResult.Success;
    }

    private PlaybackOperationResult StopCore()
    {
        _stopRequested = true;
        SignalChange();

        // What the threads are waiting on is released before they are joined, which is what lets the joins
        // below be plain joins. Three of the four waits end on their own — they are bounded, or they wake on
        // the flag — but the decoder's wait for room in a full frame queue ends only when the picture makes
        // room, and a picture that is stopping never will. Closing the queue ends it, and a decoder doing
        // exactly what it should is not left waiting for something that is never coming.
        _frameQueue?.Dispose();

        _readerThread?.Join();
        _readerThread = null;
        _videoDecodeThread?.Join();
        _videoDecodeThread = null;
        _videoRenderThread?.Join();
        _videoRenderThread = null;
        _audioRenderThread?.Join();
        _audioRenderThread = null;

        ReleaseMedia();

        SetState(MediaEngineState.None, null);
        return PlaybackOperationResult.Success;
    }

    private void ReleaseMedia()
    {
        // Closing a queue releases the thread waiting on it.
        _frameQueue?.Dispose();
        _frameQueue = null;

        Thread? videoThread = _videoRenderThread;
        _videoRenderThread = null;
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

        Thread? decodeThread = _videoDecodeThread;
        _videoDecodeThread = null;
        if (decodeThread is not null && decodeThread != Thread.CurrentThread)
        {
            decodeThread.Join(WaitForAudioMilliseconds);
        }

        // A device that stopped consuming leaves the audio thread waiting inside a submit, which would
        // hold the join up for as long as it likes. Letting the device go of its buffer releases it.
        _audioRenderer.Flush();

        Thread? audioThread = _audioRenderThread;
        _audioRenderThread = null;
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

        _videoTarget?.SurfaceSizeChanged -= OnSurfaceSizeChanged;
        _videoTarget = null;

        _source?.Dispose();
        _source = null;

        _clock = null;
        _videoStreamInfo = null;
        _audioStreamInfo = null;
        _info = null;
        _sourceEnded = false;
        _videoStarved = false;
        _audioStarved = false;
        _endingEpoch = -1;
        _videoProduced = false;
        _videoShown = false;
        _audioPlayed = false;
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
        Interlocked.Exchange(ref _anchorPending, 0);
    }

    /// <summary>
    /// The end of the file, handled on the orchestration thread because that is where playback is decided:
    /// the packets are all in and the sound's tail is already queued (the reader did both before it said
    /// anything), so what is left is for the picture and the sound to play out what they hold.
    /// <para>
    /// Nothing here waits for that. A wait would hold the queue every other command is posted to — for as
    /// long as the tail takes, which is seconds — and it would make the ending a timeout instead of a fact.
    /// Each stage says its own piece when it has nothing left instead, and the ending is taken when they all
    /// have; a stage that cannot finish leaves playback waiting where it is, named by whatever is holding it
    /// (§9), exactly as a stall in the middle of a file does.
    /// </para>
    /// </summary>
    private void SourceEndedCore(long epoch)
    {
        if (_stopRequested || epoch != _epochs.Current)
        {
            // A stop, or a seek that arrived before this was handled: the file that ran out is not where
            // playback is any more, and the reader reports the end of the new position for itself.
            return;
        }

        // Nothing more will be read, which is what tells the rest of the pipeline that a queue emptying now is
        // the file playing out rather than a stage that has fallen behind: the clock is no longer held back
        // for a sound that is merely finishing.
        _sourceEnded = true;

        // From here the ending is the queue's to decide. Every report below belongs to this position, because
        // a stage only reports after it has seen `_sourceEnded`, which has just been said — and a report that
        // arrives for any other position is ignored by name rather than by being expected not to arrive.
        _endingEpoch = epoch;
        _videoProduced = false;
        _videoShown = false;
        _audioPlayed = false;
    }

    /// <summary>
    /// The decoder's word that it has produced everything for a position: its tail is decoded and in the frame
    /// queue. Without it, "the picture has nothing to show" could mean "the tail has not come out yet", and
    /// the ending would be taken while frames were still inside the decoder.
    /// </summary>
    private void VideoProducedCore(long epoch)
    {
        if (epoch != _endingEpoch)
        {
            return;
        }

        _videoProduced = true;
        CompleteIfFinished(epoch);
    }

    /// <summary>
    /// The picture's word that it has shown everything: it found the frame queue empty and was holding nothing.
    /// One look is not evidence about a position — a look taken before the tail was produced is honest and says
    /// nothing — so it only counts once the decoder has said there is nothing more to produce, and the frame
    /// queue is looked at again here, because that queue is the queue's own to know. The stage repeats the word
    /// while it stays true, which is what makes a report arriving too early a small delay instead of a lost
    /// ending.
    /// </summary>
    private void VideoShownCore(long epoch)
    {
        if (epoch != _endingEpoch || (_frameQueue?.Count ?? 0) != 0)
        {
            return;
        }

        _videoShown = true;
        CompleteIfFinished(epoch);
    }

    /// <summary>The sound's word that it has played everything: its queue was empty and the device held nothing.</summary>
    private void AudioPlayedCore(long epoch)
    {
        if (epoch != _endingEpoch)
        {
            return;
        }

        _audioPlayed = true;
        CompleteIfFinished(epoch);
    }

    /// <summary>
    /// Ends playback when every stage that had media has said it has none: the picture produced and shown (or
    /// no picture at all), the sound played out (or no sound at all). A pause is respected rather than ended
    /// through — ending playback while the user has stopped it would take away the resume they asked for, and
    /// the stages say their piece again when they are playing.
    /// </summary>
    private void CompleteIfFinished(long epoch)
    {
        if (epoch != _endingEpoch || epoch != _epochs.Current || State != MediaEngineState.Playing)
        {
            return;
        }

        if (HasVideo && (!_videoProduced || !_videoShown))
        {
            return;
        }

        if (HasAudio && !_audioPlayed)
        {
            return;
        }

        _endingEpoch = -1;

        // The worker waits on this to be woken, and it goes idle once playback has ended, so ending is a
        // change like any other: the reader stops reading and waits for a seek or a stop to wake it.
        SetState(MediaEngineState.Ended, null);
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

    private PlaybackOperationResult Fail(string message)
    {
        SetState(MediaEngineState.Error, message);
        return PlaybackOperationResult.Failure(message);
    }

    private void SetState(MediaEngineState state, string? error = null)
    {
        // Only the orchestration queue writes these, which is what makes a lock unnecessary: a reader sees
        // the state it sees, and nothing can be writing while this is deciding.
        bool changed = _state != state || _error != error;
        _state = state;
        _error = error;

        if (!changed)
        {
            return;
        }

        // The clock follows the state of playback, and this is the one place every state change passes
        // through, so a pause, an end and a failure all stop time without any of them having to remember to.
        ApplyClockState();

        // The state is what the sleeping threads are waiting on, so a change here is what they have to wake
        // for — a pause, a resume and the end of the file all arrive this way.
        SignalChange();

        StateChanged?.Invoke(state, error);
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
        catch (Exception ex)
        {
            RaiseWarning($"Failed to apply clock state: {ex.Message}");
        }
    }

    /// <summary>
    /// Says something about playback that is not a change of state. Every call is a message of its own —
    /// nothing is compared against what was said before, because there is no before: what keeps a repeat
    /// from happening is the caller, which is the only place that knows whether this is a new situation.
    /// <para>
    /// Runs where it is called, which is the orchestration queue: every event of this class is raised there
    /// and nowhere else. A caller on a media thread uses <see cref="PostWarning"/>, which hands the message
    /// over rather than raising it off the queue.
    /// </para>
    /// </summary>
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

        // The epoch whose end has been reported. A seek moves the epoch on, which is what makes the reader
        // willing to read again from wherever it went — the reader stays alive past the end of the file for
        // exactly that reason, because it is the only thing that reads.
        long endedAtEpoch = -1;

        // The epoch this thread has carried out. Media is stamped with this and never with the epoch the gate
        // happens to hold: a packet read from the old cursor must not be labelled as belonging to the new
        // position, or every filter downstream would take it for new media and play it. Until the move has
        // been made, this stays behind the gate — which is exactly how the reader knows a seek is waiting.
        long myEpoch = _epochs.Current;

        // Whether this thread is filling the queues or idling until one of them runs low. It is the reader's
        // own work-state and it lives here rather than in a field: nobody else has any business nudging it,
        // and a copy in a field would be a shared flag with an owner nobody could name.
        bool filling = false;

        while (!_stopRequested)
        {
            // Blocked while paused, and woken again by Play. The stamp is read before the check, so a Play
            // that lands in between is seen by the check rather than by a signal nobody was waiting for.
            long changed = Volatile.Read(ref _changeStamp);

            // A seek is a request to this thread, because the cursor is this thread's: the source is moved
            // here, the decoder that rides on it is flushed here, and nothing else in the pipeline touches
            // either. The request carries the position and the epoch it belongs to; a second request before
            // this one is carried out makes the first one moot, which is why the position is read again for
            // the epoch that is being taken up.
            if (_epochs.Current != myEpoch)
            {
                long requested = _epochs.Current;
                var target = TimeSpan.FromTicks(Volatile.Read(ref _seekPositionTicks));
                if (_epochs.Current == requested)
                {
                    myEpoch = requested;

                    PlaybackOperationResult seeked = source.Seek(target);
                    if (seeked.IsSuccessful)
                    {
                        _audioDecoder?.Flush();
                    }
                    else
                    {
                        PostWarning($"The source refused to seek to {target:g}: {seeked.Message}");
                    }
                }

                continue;
            }

            if (State != MediaEngineState.Playing || endedAtEpoch == myEpoch)
            {
                // Either nothing is playing, or the file has run out at this position and only a seek will
                // make reading worth doing again.
                WaitForChange(changed, IdleWaitMilliseconds);
                continue;
            }

            // The reader is never stopped by a full queue — the queues it feeds have no maximum — so the
            // only thing that paces it is its own forward reading: fill both queues to their minimums, then
            // idle until one of them runs low. That is what keeps the picture's pace from becoming the
            // sound's pace, because the reader is the only cursor and the sound comes from it.
            if (!filling)
            {
                if (!NeedsBuffering())
                {
                    // Nothing to read for: the queues are full enough, and they are drained by the decoder
                    // and the device, which is what this waits to hear about.
                    WaitForChange(changed, RefillCheckMilliseconds);
                    continue;
                }

                filling = true;
            }
            else if (IsBuffered())
            {
                filling = false;
                continue;
            }

            PlaybackOperationResult routed = PlaybackOperationResult.Success;
            MediaPacket? packet = null;
            bool ending = false;
            bool handedOn = false;

            // The packet is stamped with the epoch this thread has carried out, so media read before a seek
            // was asked for keeps the old stamp and is given up on by every stage that meets it — the queues
            // refuse it, and the decoders, the picture and the sound drop what was already handed over.
            PlaybackOperationResult read = source.TryReadPacket(out packet);
            if (!read.IsSuccessful)
            {
                routed = read;
            }
            else if (packet is null)
            {
                // The end of the file is only the end of it if no seek is waiting to move it.
                ending = _epochs.Current == myEpoch;
            }
            else
            {
                routed = Route(packet, myEpoch, out handedOn);
            }

            // A packet the decoder queue took is the queue's to release, and the one it did not is ours.
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
                endedAtEpoch = myEpoch;

                // The audio decoder is this thread's, so the sound's tail is drained here rather than by the
                // queue, which would be a second thread on a decoder that has one owner. It is also what
                // makes "the file has ended" a statement about a file whose tail is already queued: a stage
                // that later says it has nothing left can only be talking about media it was given.
                PlaybackOperationResult drained = DrainAudio(myEpoch);
                if (!drained.IsSuccessful)
                {
                    PostFailure(drained.Message);
                    return;
                }

                Submit(() => SourceEndedCore(myEpoch));
            }
        }
    }

    #endregion

    private const int WaitForAudioMilliseconds = 2000;
    /// <summary>How full the device's buffer may be before the audio thread waits for it to drain.</summary>
    private const int QueuedLimitMilliseconds = 100;
    /// <summary>How often the reader looks again while it has read enough, in milliseconds.</summary>
    private const int RefillCheckMilliseconds = 15;
    /// <summary>
    /// How long a thread sleeps when it has nothing to do at all, in milliseconds. This is the backstop,
    /// not the mechanism: what ends these waits is a change — a resume, a seek, a stop, a new surface — and
    /// four wake-ups a second is what an idle pipeline costs when nothing happens.
    /// </summary>
    private const int IdleWaitMilliseconds = 250;
    /// <summary>
    /// How often a stage says again, at the end of the file, that it has nothing left — while it stays true.
    /// A stage cannot be asked a question it is not running to answer, so the word is repeated rather than
    /// held; this is the pace of that repetition, not the pace of the look that finds it true.
    /// </summary>
    private const int EndingReportMilliseconds = 250;
    /// <summary>How much audio the reader keeps queued before it idles, in milliseconds of the open stream.</summary>
    private const int AudioMinimumMilliseconds = 500;
    /// <summary>How many video packets the reader keeps queued before it idles.</summary>
    private const int VideoPacketMinimum = 64;
    /// <summary>How empty either queue may become before the reader fills them again.</summary>
    private const int VideoPacketWake = VideoPacketMinimum / 2;
    /// <summary>How long the audio thread waits for the device to take what it is holding, in milliseconds.</summary>
    private const int DeviceRoomCheckMilliseconds = 5;
    /// <summary>How often the device's position is looked at, and how long it may stand still before it is reported.</summary>
    private const int DeviceCheckMilliseconds = 100;
    private const int DeviceNoticeMilliseconds = 1000;
    private const string DeviceNotAdvancingWarning = "The audio device is not moving.";
    private const string DeviceAdvancingWarning = "The audio device is moving again.";

    private static readonly long DeviceCheckTicks = Stopwatch.Frequency * DeviceCheckMilliseconds / 1000;
    private static readonly long EndingReportTicks = Stopwatch.Frequency * EndingReportMilliseconds / 1000;
    private static readonly long DeviceNoticeTicks = Stopwatch.Frequency * DeviceNoticeMilliseconds / 1000;

    //
    // The components, fixed for the life of the pipeline
    //

    private readonly ISourceProviderFactory _sourceFactory;
    private readonly IVideoDecoderFactory _videoDecoderFactory;
    private readonly IAudioDecoderFactory _audioDecoderFactory;
    private readonly IComponentFactory<IVideoRenderer> _videoRendererFactory;
    private readonly IAudioRenderer _audioRenderer;
    private readonly ISynchronizer _synchronizer;

    /// <summary>The flush request against the decoder handover: asked on the queue, taken by the decoding thread.</summary>
    private readonly Lock _videoDecodeLock = new();

    /// <summary>Guards <see cref="_changeStamp"/> and the waits taken on it.</summary>
    private readonly object _wakeLock = new();

    /// <summary>
    /// Counts the things a sleeping thread has to know about: the state, the position, and the presentation
    /// surface. A thread reads it before deciding there is nothing to do and then waits for it to differ,
    /// which is what makes a signal arriving at exactly that moment impossible to miss. Written under
    /// <see cref="_wakeLock"/> — by the queue, and by the host when the surface it gave us moves — and read
    /// without it, which is why those reads are volatile ones.
    /// </summary>
    private long _changeStamp;

    //
    // Open state: written on the queue only while the media threads are stopped — before they start, or after
    // they have been joined. That invariant is what replaces a lock around all of it, and it is why there are
    // two things in here that are marked as belonging to a stage as well.
    //
    private MediaInfo? _info;

    //
    // Shared between a stage and the queue: volatile, and the comment says which direction each one travels
    //

    /// <summary>Asked for by the queue, taken by the decoding thread, which clears it as it honours it.</summary>
    private volatile bool _videoFlushPending;

    /// <summary>
    /// The audio thread's device watch, owned by that thread and nothing else: what it has seen of the device,
    /// when, and whether the stall it is looking at has been reported yet. It is re-seeded when the thread
    /// notices the epoch move, which is how a seek — the device's own jump is movement, not a recovery — is
    /// taken up without the queue reaching into this state at all.
    /// </summary>

    private TimeSpan _devicePosition;
    private long _deviceMovedAt;
    private long _deviceCheckedAt;
    private long _deviceWatchEpoch = -1;
    private bool _deviceStallReported;

    //
    // The ending, owned by the queue and touched by nothing else: which position is playing out, and which
    // of the stages that still had media have said they have none left. The stages do not read any of it —
    // each of them says its own piece to the queue when it observes it, which is what makes the end of the
    // file a decision the queue takes rather than a state it waits for.
    //

    private long _endingEpoch = -1;
    private bool _videoProduced;
    private bool _videoShown;
    private bool _audioPlayed;

    //
    // Counters and hand-offs: written by whichever thread has the fact, read by anyone including the host
    //

    private long _presentedFrameCount;
    private long _droppedFrameCount;
    private long _displayedFrameCount = -1;
    private long _decodedFrameCount;
    private long _decodeTicks;
    private long _renderTicks;
    private long _pictureTicks;

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

    public MediaInfo? MediaInfo => _info;

    public bool HasVideo => _videoStreamInfo is not null;

    public bool HasAudio => _audioStreamInfo is not null;

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
    /// How many of the frames handed over reached the display, when the renderer can tell, or null when it
    /// cannot. The difference from <see cref="PresentedFrameCount"/> is what the compositor superseded — the
    /// drops that occur after the picture has left the pipeline, which no counter in it can see.
    /// <para>
    /// The render thread asks the renderer after each present and publishes the answer here, so it is the
    /// same shape as the other counters: one thread that has the fact, anyone reading it. It lags by the
    /// frame or two that are still in flight, and a backend that will not report leaves it null rather than
    /// at zero.
    /// </para>
    /// </summary>
    public long? DisplayedFrameCount
    {
        get
        {
            long displayed = Interlocked.Read(ref _displayedFrameCount);
            return displayed < 0 ? null : displayed;
        }
    }

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
            long ticks = Interlocked.Read(ref _tailDecodedFrameTicks);
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

    /// <summary>True once both queues hold their minimum, which is what makes the reader idle.</summary>
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

    /// <summary>True once either queue has run low, which is what wakes the reader again.</summary>
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

    private PlaybackOperationResult Route(MediaPacket packet, long epoch, out bool handedOn)
    {
        handedOn = false;

        IAudioDecoder? audioDecoder = _audioDecoder;
        if (audioDecoder is not null && packet.StreamIndex == _audioStreamInfo!.Index)
        {
            // Audio is decoded right here rather than handed on: it costs almost nothing, and every packet
            // passed to another thread is another chance for the sound to be late.
            return DecodeAudio(packet, epoch);
        }

        if (_videoDecoder is not null && packet.StreamIndex == _videoStreamInfo!.Index)
        {
            MediaQueue<MediaPacket>? packetQueue = _packetQueue;
            if (packetQueue is null)
            {
                return PlaybackOperationResult.Failure("The video packet queue is not available.");
            }

            // Handed on rather than decoded here: a reader that waits for a video decoder is a reader that
            // cannot read the audio packets interleaved behind the frame being decoded. Waiting for room is
            // a different matter and is right — it is what paces the reader — and it is safe because the
            // thread that empties this queue never waits for the picture: it decodes, hands frames to a
            // queue that waits for the picture to take one, and comes back for more (see MediaQueue's limit).
            handedOn = packetQueue.TryEnqueue(packet, epoch);
            return PlaybackOperationResult.Success;
        }

        // Streams nobody selected, such as a second audio track, are simply skipped.
        return PlaybackOperationResult.Success;
    }

    private PlaybackOperationResult DecodeAudio(MediaPacket packet, long epoch)
    {
        IAudioDecoder? audioDecoder = _audioDecoder;
        if (audioDecoder is null)
        {
            return PlaybackOperationResult.Success;
        }

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
            // The queue owns the buffer from here; the audio thread disposes it after playing it.
            if (!audioQueue.TryEnqueue(buffer, epoch))
            {
                buffer.Dispose();
            }
        }

        return PlaybackOperationResult.Success;
    }

    private PlaybackOperationResult DecodeVideo(MediaPacket packet, out IReadOnlyList<RawVideoFrame>? frames)
    {
        frames = null;

        IVideoDecoder? videoDecoder = _videoDecoder;
        if (videoDecoder is null)
        {
            return PlaybackOperationResult.Success;
        }

        long started = Stopwatch.GetTimestamp();
        PlaybackOperationResult decoded = videoDecoder.TryDecode(packet, out IReadOnlyList<RawVideoFrame> produced);
        Interlocked.Add(ref _decodeTicks, Stopwatch.GetTimestamp() - started);

        if (!decoded.IsSuccessful)
        {
            return decoded;
        }

        frames = produced;
        Interlocked.Add(ref _decodedFrameCount, produced.Count);
        return PlaybackOperationResult.Success;
    }

    /// <summary>
    /// Hands decoded frames to the queue that shows them, and records how far the decoder has got. Frames go
    /// to the queue rather than straight to the renderer, so decoding can run on while the presentation
    /// thread waits for each frame's moment; the wait for room is the picture's own back-pressure.
    /// <para>
    /// The epoch is checked here, before anything is handed over, because handing over is what books a frame
    /// as the decoder's progress: what the decoder has produced for a position playback has left must not be
    /// recorded as how far it has got for the new one, where it would read as a picture seconds behind and
    /// hold the clock back for a decoder that is not behind at all. It is reachable when a decode that was
    /// already running returns frames — an immediate-output codec, or any decoder whose output is not
    /// delayed — and a seek lands while it runs; the flush that follows discards what a decoder *holds*, but
    /// not the frames a call is in the middle of returning.
    /// </para>
    /// </summary>
    private void QueueFrames(IReadOnlyList<RawVideoFrame>? frames, long epoch)
    {
        if (frames is null)
        {
            return;
        }

        foreach (RawVideoFrame frame in frames)
        {
            MediaQueue<RawVideoFrame>? frameQueue = _frameQueue;
            if (frameQueue is null || _epochs.Current != epoch || !frameQueue.TryEnqueue(frame, epoch))
            {
                frame.Dispose();
                continue;
            }

            // What the decoder has produced is what says whether the picture can still catch up with the
            // clock; a frame handed over is a frame that will be shown.
            Interlocked.Exchange(ref _tailDecodedFrameTicks, frame.Timestamp.Ticks);
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
        long tailTicks = Interlocked.Read(ref _tailDecodedFrameTicks);
        if (clock is null || tailTicks < 0 || _sourceEnded || Volatile.Read(ref _anchorPending) != 0)
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
        PlaybackOperationResult rendered = renderer.Render(frame);
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

    private PlaybackOperationResult DrainAudio(long epoch)
    {
        IAudioDecoder? decoder = _audioDecoder;
        if (decoder is null)
        {
            return PlaybackOperationResult.Success;
        }

        PlaybackOperationResult drained = decoder.Drain(out IReadOnlyList<RawAudioBuffer> buffers);
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
            if (!queue.TryEnqueue(buffer, epoch))
            {
                buffer.Dispose();
            }
        }

        return PlaybackOperationResult.Success;
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
            long changed = Volatile.Read(ref _changeStamp);

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
                WaitForChange(changed, IdleWaitMilliseconds);
                continue;
            }

            if (limit <= 0 || _audioRenderer.QueuedSampleCount < limit)
            {
                return true;
            }

            // Room comes back from the device, which is the one thing here that cannot be signalled — the
            // device has no way to tell us it has played something — so this stays a poll, at the pace the
            // device drains a buffer rather than at the pace of a processor loop.
            WaitForChange(changed, DeviceRoomCheckMilliseconds);
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
        long epoch = _epochs.Current;
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

        bool expected = State == MediaEngineState.Playing
            && !_videoStarved
            && !_audioStarved
            && Volatile.Read(ref _anchorPending) == 0
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

    /// <summary>
    /// Decodes the packets the reader hands over, on a thread of its own, so that however long one frame
    /// takes — a 4K keyframe, say — the packets behind it keep moving. The audio packets in a file are
    /// interleaved with the video ones, so a reader that stops for a video decode stops producing sound.
    /// </summary>
    private void VideoDecodeCore()
    {
        try
        {
            // The last position this thread produced a tail for, so an end of file drains the decoder once
            // rather than once per look. It is a local on purpose: no other thread has any business with it.
            long producedForEpoch = -1;

            while (!_stopRequested)
            {
                MediaQueue<MediaPacket>? queue = _packetQueue;
                if (queue is null)
                {
                    return;
                }

                // Read before anything is decided below, so a change arriving between a decision and the
                // wait is caught by the wait's own check rather than by a pulse nobody was listening for.
                long changed = Volatile.Read(ref _changeStamp);

                if (State != MediaEngineState.Playing)
                {
                    // Nothing to decode for: the picture is not moving, so no frame is being used up. The
                    // frame queue is a bound on how far ahead this thread could run anyway; standing still
                    // while the picture is standing still is what keeps a pause from spending the look-ahead
                    // the resume needs.
                    WaitForChange(changed, IdleWaitMilliseconds);
                    continue;
                }

                if (!queue.TryDequeue(out MediaPacket? packet, out long epoch, 20) || packet is null)
                {
                    // Nothing to dequeue: either the decoder is keeping up and the reader has not read on
                    // yet, or the file has run out. The second is the reader's statement and this thread
                    // reads it there (`_sourceEnded`), rather than the queue carrying a copy of it.
                    if (_sourceEnded)
                    {
                        // The tail is produced once per position and not once per look, and the memory of
                        // having done it is this thread's own — which is why nothing about it is published.
                        long position = _epochs.Current;
                        if (producedForEpoch != position)
                        {
                            long produced = DrainTheTail();
                            if (produced < 0)
                            {
                                return;
                            }

                            producedForEpoch = produced;

                            // "No more frames from me for this position." It goes to the queue rather than
                            // into a flag for others to poll, because deciding the ending is the only thing
                            // that needs it — and a report that never arrived is a file that waits at its
                            // end, which is where it was anyway, rather than a file that ends wrong.
                            Submit(() => VideoProducedCore(produced));
                        }

                        // The thread stays alive at the end of the file, as the reader does, because a
                        // replay has to have something to decode with. A seek is what wakes this wait.
                        WaitForChange(changed, IdleWaitMilliseconds);
                    }

                    continue;
                }

                using (packet)
                {
                    IReadOnlyList<RawVideoFrame>? frames = null;
                    PlaybackOperationResult decoded = PlaybackOperationResult.Success;
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
                            Interlocked.Exchange(ref _tailDecodedFrameTicks, -1);
                        }

                        if (_epochs.Current == epoch)
                        {
                            decoded = DecodeVideo(packet, out frames);
                        }
                    }

                    if (!decoded.IsSuccessful)
                    {
                        PostFailure(decoded.Message);
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
            PostFailure(exception.Message);
        }
    }

    /// <summary>
    /// The end of the file is the end of the packets, not of the frames inside them: whatever the decoder
    /// still holds is decoded and queued, and the position it was drained for is returned so the report says
    /// which ending it belongs to. Returns -1 when the decoder failed, having reported it — a decoder that
    /// cannot be drained has no tail to speak of.
    /// </summary>
    private long DrainTheTail()
    {
        long epoch;
        PlaybackOperationResult drained;

        // Held while draining, so the flush a seek asked for cannot land between the drain and the position
        // it belongs to.
        lock (_videoDecodeLock)
        {
            epoch = _epochs.Current;
            drained = DrainVideo(epoch);
        }

        if (!drained.IsSuccessful)
        {
            PostFailure(drained.Message);
            return -1;
        }

        return epoch;
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
    private void VideoRenderCore()
    {
        try
        {
            RawVideoFrame? held = null;
            long heldEpoch = 0;

            // When this thread last told the queue it had nothing to show. A stage cannot be asked a question
            // it is not running to answer, so the word is repeated while it stays true — at the pace of an
            // idle wait rather than the pace of a look, which is what keeps a level-triggered report cheap.
            long shownReportedAt = 0;

            while (!_stopRequested)
            {
                MediaQueue<RawVideoFrame>? queue = _frameQueue;
                if (queue is null)
                {
                    held?.Dispose();
                    return;
                }

                // Read before anything is decided below — including the state check — so a change arriving
                // between a decision and the wait is caught by the wait's own check rather than by a pulse
                // nobody was listening for. It is also what makes a surface change arriving while the paused
                // refresh below runs end that refresh's wait at once, which is what keeps a drag of the window
                // edge following the mouse instead of the idle timeout.
                long changed = Volatile.Read(ref _changeStamp);

                if (State != MediaEngineState.Playing)
                {
                    // Paused: the queue keeps what it holds, the clock is not moving, and nothing is shown.
                    // The window can still change while nothing is being shown, though, and a renderer left
                    // on its old surface has the host stretch the paused picture into the new one — so it is
                    // given the chance to take the change up, which is what keeps a paused video following
                    // the window. It does nothing unless the surface actually changed.
                    IVideoRenderer? pausedRenderer = _videoRenderer;
                    if (pausedRenderer is not null && State != MediaEngineState.Error)
                    {
                        PlaybackOperationResult refreshed = pausedRenderer.RefreshSurface();
                        if (!refreshed.IsSuccessful)
                        {
                            PostFailure(refreshed.Message);
                            return;
                        }
                    }

                    WaitForChange(changed, IdleWaitMilliseconds);
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
                        // Nothing to show. At the end of the file that is worth saying as well as waiting
                        // for: the picture is one of the two things the ending waits on, and a stage can only
                        // be asked about itself. Before the end an empty queue is the decoder being behind,
                        // so there is nothing to say.
                        if (_sourceEnded && Stopwatch.GetTimestamp() - shownReportedAt >= EndingReportTicks)
                        {
                            shownReportedAt = Stopwatch.GetTimestamp();
                            long shownEpoch = _epochs.Current;
                            Submit(() => VideoShownCore(shownEpoch));
                        }

                        continue;
                    }

                    if (_epochs.Current != heldEpoch)
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
                    // and nothing in the pipeline waits on this thread. It sleeps until the frame is due
                    // rather than in steps, so a frame costs one wake-up instead of a tick every millisecond,
                    // and it is woken early by anything that changes what it would do — a seek, a pause, a
                    // stop — because those are what the stamp is for.
                    int remaining = (int)Math.Clamp((held.Timestamp - Position).TotalMilliseconds, 1, IdleWaitMilliseconds);
                    WaitForChange(changed, remaining);
                    continue;
                }

                PlaybackOperationResult shown = Show(held);
                held.Dispose();
                held = null;

                if (!shown.IsSuccessful)
                {
                    PostFailure(shown.Message);
                    return;
                }
            }

            held?.Dispose();
        }
        catch (Exception exception)
        {
            PostFailure(exception.Message);
        }
    }

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
            long deviceEpoch = _epochs.Current;

            // When this thread last told the queue the sound had played out; repeated while it stays true, at
            // the pace of an idle wait rather than of a look.
            long playedReportedAt = 0;

            while (!_stopRequested)
            {
                MediaQueue<RawAudioBuffer>? queue = _audioQueue;
                if (queue is null)
                {
                    return;
                }

                // Read before anything is decided below, so a change arriving between a decision and the
                // wait is caught by the wait's own check rather than by a pulse nobody was listening for.
                long changed = Volatile.Read(ref _changeStamp);

                // Taken up at the top of the loop, which is the earliest this thread can act on it: whether
                // it is playing or paused, and whether or not the reader has produced anything for the new
                // position yet. A flush on a device that holds nothing is what it costs, and that is nothing.
                long currentEpoch = _epochs.Current;
                if (deviceEpoch != currentEpoch)
                {
                    deviceEpoch = currentEpoch;
                    _audioRenderer.Flush();
                }

                if (State != MediaEngineState.Playing)
                {
                    // Paused: nothing new should reach the device, and the queue keeps what it holds. A
                    // resume, a seek or a stop is what ends this wait, which is why the device is not polled
                    // here at all.
                    WaitForChange(changed, IdleWaitMilliseconds);
                    continue;
                }

                WatchDeviceProgress();

                if (!queue.TryDequeue(out RawAudioBuffer? buffer, out long bufferEpoch, 50) || buffer is null)
                {
                    // Nothing to play. Time moving without the sound is what lets a picture run ahead of a
                    // sound that has not arrived, so the clock waits here — unless the file has ended, where
                    // an empty queue is the sound playing out rather than a sound falling behind.
                    RequestAudioStarved(!_sourceEnded);

                    // And at the end of the file the sound has one more thing to say, once there is nothing
                    // left in the queue or in the device: it has played everything. Only this thread can know
                    // that, the device being its own, so the word is repeated while it stays true.
                    if (_sourceEnded
                        && _audioRenderer.QueuedSampleCount <= 0
                        && Stopwatch.GetTimestamp() - playedReportedAt >= EndingReportTicks)
                    {
                        playedReportedAt = Stopwatch.GetTimestamp();
                        long playedEpoch = _epochs.Current;
                        Submit(() => AudioPlayedCore(playedEpoch));
                    }

                    continue;
                }

                using (buffer)
                {
                    // There is something to hand over, so whatever the sound was waiting for has arrived.
                    RequestAudioStarved(false);

                    if (_epochs.Current != bufferEpoch)
                    {
                        // The block was produced for where playback used to be. It is given up on here rather
                        // than played, and before anything else is done with it: anchoring the clock to it
                        // would set time back to the position the seek has just left.
                        continue;
                    }

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

                    if (_epochs.Current != epoch)
                    {
                        // This block was produced for where playback used to be and may already be in the
                        // device, which the seek has since refilled — a device that stopped consuming makes
                        // the submit of a block outlast the seek that interrupted it. Letting the device go
                        // of what it holds is what keeps a seek from playing a moment of where it came from.
                        deviceEpoch = _epochs.Current;
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

    private PlaybackOperationResult DrainVideo(long epoch)
    {
        IVideoDecoder? decoder = _videoDecoder;
        if (decoder is null)
        {
            return PlaybackOperationResult.Success;
        }

        PlaybackOperationResult drained = decoder.Drain(out IReadOnlyList<RawVideoFrame> frames);
        if (!drained.IsSuccessful)
        {
            return drained;
        }

        QueueFrames(frames, epoch);
        return PlaybackOperationResult.Success;
    }

    /// <summary>
    /// The host's presentation surface moved: it is a different size, or the display under it is. The
    /// renderer will take it up (that is what its own subscription is for), but a thread that is waiting for
    /// something to change has no other way to hear about it — the paused video thread is the one that
    /// matters, because a paused picture still has to fit the window it is in.
    /// </summary>
    private void OnSurfaceSizeChanged()
    {
        SignalChange();
    }

    /// <summary>
    /// Wakes every thread that is waiting for something to change. Called from the state machine, from a
    /// seek, from a stop, and whenever the host reports that the presentation surface moved — the four
    /// things a sleeping thread cannot discover for itself.
    /// </summary>
    private void SignalChange()
    {
        lock (_wakeLock)
        {
            _changeStamp++;
            Monitor.PulseAll(_wakeLock);
        }
    }

    /// <summary>
    /// Waits until something changes, or until the given time has passed, whichever comes first. A thread
    /// with nothing to do blocks here instead of polling, so a paused pipeline costs four wake-ups a second
    /// rather than two hundred, and the wake-ups it does take are for the things it is waiting for.
    /// <para>
    /// The stamp is what makes this exact rather than best-effort, and the order of the call is part of it:
    /// the caller reads the stamp <em>before</em> it decides there is nothing to do, so a change that arrives
    /// while it decides is caught by the check below rather than by a pulse that nobody was waiting for. The
    /// timeout is the backstop for everything else, and it is what keeps every wait ending on its own.
    /// </para>
    /// </summary>
    private void WaitForChange(long stamp, int milliseconds)
    {
        lock (_wakeLock)
        {
            if (_changeStamp != stamp)
            {
                return;
            }

            Monitor.Wait(_wakeLock, milliseconds);
        }
    }

    #region Communication

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
