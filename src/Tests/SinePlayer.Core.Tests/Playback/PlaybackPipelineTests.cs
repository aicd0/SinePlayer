// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Diagnostics;
using System.Text;

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Abstractions.Timing;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Pipeline;
using SinePlayer.Core.Playback.Timing;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Drives the whole orchestration with the real source and decoders but stand-in renderers, so
/// playback can be run to the end without a window and without a device. The fixtures are files the
/// test writes: a bitmap is a one frame video stream and a wave file is an audio stream.
/// </summary>
[TestFixture]
public class PlaybackPipelineTests
{
    private const int Width = 32;
    private const int Height = 24;
    private const int SampleRate = 44100;
    private const int ChannelCount = 2;
    private const int SampleCount = 8820;

    [Test]
    public void PlaysAVideoOnlyFileToTheEnd()
    {
        string path = CreateBitmapFile();
        try
        {
            FakeVideoRenderer videoRenderer = new();
            FakeAudioRenderer audioRenderer = new() { CanConfigure = false };
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, audioRenderer);

            PlaybackOperationResult opened = Open(pipeline, path, new FakeRenderTarget());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(opened.IsSuccessful, Is.True, opened.Message);
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused));
                Assert.That(pipeline.HasVideo, Is.True);
                Assert.That(pipeline.HasAudio, Is.False);
                Assert.That(pipeline.MediaInfo?.VideoStreams, Has.Count.EqualTo(1));
            }

            pipeline.Play();
            WaitForPlaybackToFinish(pipeline);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);
                Assert.That(videoRenderer.RenderedFrames, Is.GreaterThan(0), "the frame should have been presented");
                Assert.That(videoRenderer.ConfiguredStream?.Width, Is.EqualTo(Width));
                Assert.That(videoRenderer.ConfiguredStream?.Height, Is.EqualTo(Height));
                Assert.That(videoRenderer.Target, Is.Not.Null, "the renderer must be given the host's surface");
                Assert.That(pipeline.PresentedFrameCount, Is.GreaterThan(0));
                Assert.That(pipeline.Position, Is.GreaterThanOrEqualTo(TimeSpan.Zero));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ReportsWhatTheDisplayTookWhenTheRendererCanTell()
    {
        // The frames the compositor supersedes are drops no counter in the pipeline can see, so what the
        // display took is passed on from the renderer — and a renderer that cannot tell says nothing, which
        // is a different statement from zero.
        //
        // Two frames rather than one: a file that is open shows the picture of where it is (the still of a
        // paused position), and a one-frame file would have shown all of it before the renderer was able to
        // tell anything — so the second frame is what carries the answer this test is about.
        string path = CreateY4mFile(frameCount: 2, frameRate: "30:1");
        try
        {
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer());

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            Assert.That(pipeline.DisplayedFrameCount, Is.Null, "a renderer that cannot tell is not a renderer that reported zero");

            videoRenderer.DisplayedFrameCount = 0;
            pipeline.Play();
            WaitForPlaybackToFinish(pipeline);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.PresentedFrameCount, Is.GreaterThan(0));
                Assert.That(pipeline.DisplayedFrameCount, Is.Zero, "the renderer's answer is what is reported, however disappointing");
                Assert.That(pipeline.DisplayedFrameCount, Is.Not.Null, "and zero is an answer, not silence");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A file longer than the read-ahead is what makes the reader wait at all: it fills the minimum, parks,
    /// and every block the sound plays brings it back. Nothing in the pipeline tells it that — the sound is
    /// the only thread that knows it took something — so this is what pins the wake from the consumer to the
    /// reader. A missing one is not a slow pipeline: the reader parks for good and the file never ends.
    /// </summary>
    [Test]
    public void KeepsTheSoundComingAfterTheReadAheadIsUsedUp()
    {
        int samples = SampleRate * 3;
        string path = CreateWaveFile(samples);
        try
        {
            // A device that takes a block every so often, so the read-ahead is used up in steps rather than
            // in one gulp.
            FakeAudioRenderer audioRenderer = new() { SubmitDelayMilliseconds = 20 };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            PlaybackOperationResult opened = Open(pipeline, path);
            Assert.That(opened.IsSuccessful, Is.True, opened.Message);

            pipeline.Play();
            WaitForPlaybackToFinish(pipeline);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);
                Assert.That(audioRenderer.SubmittedSamples, Is.EqualTo(samples), "every sample of a file read in several passes has to reach the device");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The picture's side of the same arrangement: a file longer than the frame window fills the queue, the
    /// decoder parks for room, and every frame the picture takes is what brings it back. Nothing else tells
    /// it — the picture is the only thread that knows it made room — so a missing wake here is a picture that
    /// stops after one window and never reaches the end of the file.
    /// </summary>
    [Test]
    public void KeepsThePictureComingAfterTheFrameWindowIsUsedUp()
    {
        // A real thirty-frame-a-second stream, so the window is used up many times over in a couple of
        // seconds rather than once every two.
        string path = VideoFixtureFile.CreateAv1();
        try
        {
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer());

            PlaybackOperationResult opened = Open(pipeline, path, new FakeRenderTarget());
            Assert.That(opened.IsSuccessful, Is.True, opened.Message);

            pipeline.Play();
            WaitForPlaybackToFinish(pipeline);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);
                Assert.That(videoRenderer.RenderedFrames, Is.GreaterThan(VideoFixtureFile.FrameCount / 2),
                    "a file read into the window in several passes has to keep being shown");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A surface change has to reach the picture thread even when there is no frame to show: a paused
    /// position whose picture is owed leaves that thread waiting for one, and a window resize is not a frame.
    /// Nothing about the queue changes, so a thread waiting *in* the queue cannot be told — which is what
    /// made this case unreachable before every wait moved onto the thread's own node.
    /// </summary>
    [Test]
    public void TakesUpASurfaceChangeWhileThePictureWaitsForItsPosition()
    {
        string path = CreateY4mFile(frameCount: 200);
        try
        {
            FakeRenderTarget target = new();
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(
                videoRenderer,
                new FakeAudioRenderer(),
                new ThrottledVideoDecoderFactory { DecodeMilliseconds = 1300, FastPackets = 1 });

            Assert.That(Open(pipeline, path, target).IsSuccessful, Is.True);
            pipeline.Play();
            WaitForPresentedFrames(pipeline, 1);

            // Paused and then moved: the position's picture is owed, the decoder is quiet for over a second,
            // so the thread is waiting for a frame that is not there.
            Assert.That(Await(pipeline.Pause()).IsSuccessful, Is.True);
            SeekAndWait(pipeline, TimeSpan.FromSeconds(20));

            int before = videoRenderer.RefreshedSurfaces;
            target.RaiseSizeChanged();

            WaitFor(() => videoRenderer.RefreshedSurfaces > before,
                "a surface change has to reach a picture that is waiting for a frame", 1500);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The same edge from the other side: a second seek has to reach the thread that is still waiting to be
    /// handed the first one's picture. The clock's position is the observable — it is the render thread that
    /// places a new request, and it can only do that by waking.
    /// </summary>
    [Test]
    public void TakesUpASeekWhileThePictureWaitsForItsPosition()
    {
        string path = CreateY4mFile(frameCount: 200);
        try
        {
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(
                videoRenderer,
                new FakeAudioRenderer(),
                // Slow enough that the decoder cannot rescue the thread inside the window this checks.
                new ThrottledVideoDecoderFactory { DecodeMilliseconds = 3000, FastPackets = 1 });

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            pipeline.Play();
            WaitForPresentedFrames(pipeline, 1);

            Assert.That(Await(pipeline.Pause()).IsSuccessful, Is.True);
            SeekAndWait(pipeline, TimeSpan.FromSeconds(20));

            // The first seek's picture is still owed, so the thread is waiting for it — and the second seek
            // is not a frame.
            SeekAndWait(pipeline, TimeSpan.FromSeconds(40));

            WaitFor(() => pipeline.Position >= TimeSpan.FromSeconds(39),
                "a second seek has to reach a picture that is waiting for the first one", 1500);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The picture holds the clock until this position has something to show. Without it the sound starts
    /// the clock on its first block and the picture is left behind before it has produced a single frame —
    /// measured at ten frames dropped and the picture starting 666 ms into the file, and nothing anywhere
    /// said so.
    /// </summary>
    [Test]
    public void HoldsTheClockBackUntilThePictureHasSomethingToShow()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            FakeAudioRenderer audioRenderer = new() { SubmitDelayMilliseconds = 30 };
            using PlaybackPipeline pipeline = CreatePipeline(
                new FakeVideoRenderer(),
                audioRenderer,
                new SlowFirstDecodeVideoDecoderFactory { FirstDecodeMilliseconds = 400 });

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            // The sound has something to hand over almost at once; the picture takes four hundred
            // milliseconds. Nothing may run in the meantime.
            WaitFor(() => pipeline.StarvedFor == "video", "the picture has nothing to show, so time has to be held");
            // The device is the audio thread's to run, so the hold reaches it a beat after the verdict.
            WaitFor(() => !audioRenderer.ClockIsRunning, "a held clock is a stopped clock");
            Assert.That(pipeline.Position, Is.LessThan(TimeSpan.FromMilliseconds(200)), "the clock is held where the position starts");

            WaitForPresentedFrames(pipeline, 1);

            WaitFor(() => audioRenderer.ClockIsRunning, "the first frame is what lets time pass");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.DroppedFrameCount, Is.Zero, "the first frames of a position are not late when the clock waited for them");
                Assert.That(pipeline.Position, Is.LessThan(TimeSpan.FromMilliseconds(300)), "and the picture starts at the start of the file, not past it");
            }

            WaitForPresentedFrames(pipeline, 3);
            Assert.That(pipeline.DroppedFrameCount, Is.Zero, "and nothing after it is dropped either");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The same hold for a position reached by a seek.
    /// </summary>
    [Test]
    public void HoldsTheClockBackUntilThePictureHasSomethingToShowAfterASeek()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            FakeAudioRenderer audioRenderer = new() { SubmitDelayMilliseconds = 30 };
            using PlaybackPipeline pipeline = CreatePipeline(
                new FakeVideoRenderer(),
                audioRenderer,
                new SlowFirstDecodeVideoDecoderFactory { FirstDecodeMilliseconds = 400 });

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            // Far enough in that the decoded tail is ahead of where the seek will go back to.
            WaitFor(() => pipeline.Position > TimeSpan.FromMilliseconds(500), "playback has to get past the position the seek goes back to");

            SeekAndWait(pipeline, TimeSpan.FromMilliseconds(100));
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            // The new position's first decode takes four hundred milliseconds, and nothing may run in the
            // meantime — the old tail is not a fact about this position.
            WaitFor(() => pipeline.StarvedFor == "video", "a position that has produced nothing holds the clock", 150);
            WaitFor(() => !audioRenderer.ClockIsRunning, "the clock is held, so the sound is stopped with it");

            long shown = pipeline.PresentedFrameCount;
            WaitForPresentedFrames(pipeline, shown + 1);
            WaitFor(() => audioRenderer.ClockIsRunning, "the first frame of the position lets time pass");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The case that decides between "this position has produced nothing" and "the decoder's tail says it has
    /// produced nothing": a seek that lands **while the decoder is inside a decode**. The tail is then still
    /// the position playback has left — a value *ahead* of the new request — so a tail-based verdict finds the
    /// picture comfortably ahead and lets the clock run through the whole of that decode. The gate makes the
    /// window deterministic: the decoder is held mid-decode while the position moves underneath it.
    /// </summary>
    [Test]
    public void HoldsTheClockBackWhenAPositionMovesDuringADecode()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            FakeAudioRenderer audioRenderer = new() { SubmitDelayMilliseconds = 30 };
            GatedVideoDecoderFactory videoDecoders = new();
            using PlaybackPipeline pipeline = CreatePipeline(
                new FakeVideoRenderer(),
                audioRenderer,
                videoDecoders);

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.Position > TimeSpan.FromMilliseconds(500), "playback has to get past the position the seek goes back to");

            // Hold the decoder inside a decode, so its tail keeps the value of the position playback is about
            // to leave, and move the position underneath it.
            videoDecoders.Close();
            Assert.That(videoDecoders.WaitUntilDecoding(), Is.True, "the decoder has to be inside a decode");

            SeekAndWait(pipeline, TimeSpan.FromMilliseconds(100));
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitFor(() => pipeline.StarvedFor == "video", "a position that has produced nothing holds the clock", 300);
            WaitFor(() => !audioRenderer.ClockIsRunning, "the old position's tail is not a fact about this one");

            videoDecoders.Open();
            long shown = pipeline.PresentedFrameCount;
            WaitForPresentedFrames(pipeline, shown + 1);
            WaitFor(() => audioRenderer.ClockIsRunning, "the first frame of the position lets time pass");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A decoder that can be held inside a decode, so a test can move the position while the decoder is
    /// working on the old one — which is what a real seek does to a real decoder, and the window in which its
    /// tail describes a position that no longer exists.
    /// </summary>
    private sealed class GatedVideoDecoderFactory : IVideoDecoderFactory
    {
        private readonly FFmpegVideoDecoderFactory _inner = new();
        private readonly ManualResetEventSlim _gate = new(true);
        private readonly ManualResetEventSlim _decoding = new(false);

        public ComponentCapabilities Capabilities => _inner.Capabilities;

        internal void Close()
        {
            _gate.Reset();
            _decoding.Reset();
        }

        internal void Open()
        {
            _gate.Set();
        }

        /// <summary>Waits for the decoder to be inside a decode, which is when the gate holds it.</summary>
        internal bool WaitUntilDecoding() => _decoding.Wait(TimeSpan.FromSeconds(5));

        public PlaybackOperationResult TryCreate(IMediaSource source, VideoStreamInfo stream, DecoderOptions options, out IVideoDecoder? decoder)
        {
            PlaybackOperationResult created = _inner.TryCreate(source, stream, options, out IVideoDecoder? inner);
            if (!created.IsSuccessful || inner is null)
            {
                decoder = null;
                return created;
            }

            decoder = new GatedDecoder(inner, this);
            return PlaybackOperationResult.Success;
        }

        private sealed class GatedDecoder(IVideoDecoder inner, GatedVideoDecoderFactory owner) : IVideoDecoder
        {
            public MediaStreamInfo Stream => inner.Stream;

            public bool IsHardwareAccelerated => inner.IsHardwareAccelerated;

            public PixelFormat OutputFormat => inner.OutputFormat;

            public PlaybackOperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
            {
                owner._decoding.Set();
                owner._gate.Wait(TimeSpan.FromSeconds(5));
                return inner.TryDecode(packet, out frames);
            }

            public PlaybackOperationResult Drain(out IReadOnlyList<RawVideoFrame> frames) => inner.Drain(out frames);

            public PlaybackOperationResult Flush() => inner.Flush();

            public void Dispose() => inner.Dispose();
        }
    }

    /// <summary>
    /// A decoder that is slow on the first packet it is given, and on the first packet after every flush —
    /// which is what a seek costs a real decoder, and what makes the first frames of the new position late
    /// unless the clock waited for them.
    /// </summary>
    private sealed class SlowFirstDecodeVideoDecoderFactory : IVideoDecoderFactory
    {
        private readonly FFmpegVideoDecoderFactory _inner = new();

        internal int FirstDecodeMilliseconds { get; init; }

        /// <summary>How long each packet after the first takes, for a decoder that is slow at the start and
        /// then merely behind.</summary>
        internal int SubsequentDecodeMilliseconds { get; init; }

        public ComponentCapabilities Capabilities => _inner.Capabilities;

        public PlaybackOperationResult TryCreate(IMediaSource source, VideoStreamInfo stream, DecoderOptions options, out IVideoDecoder? decoder)
        {
            PlaybackOperationResult created = _inner.TryCreate(source, stream, options, out IVideoDecoder? inner);
            if (!created.IsSuccessful || inner is null)
            {
                decoder = null;
                return created;
            }

            decoder = new SlowFirstDecoder(inner, FirstDecodeMilliseconds, SubsequentDecodeMilliseconds);
            return PlaybackOperationResult.Success;
        }

        private sealed class SlowFirstDecoder(IVideoDecoder inner, int firstDecodeMilliseconds, int subsequentDecodeMilliseconds) : IVideoDecoder
        {
            private int _pending = 1;

            public MediaStreamInfo Stream => inner.Stream;

            public bool IsHardwareAccelerated => inner.IsHardwareAccelerated;

            public PixelFormat OutputFormat => inner.OutputFormat;

            public PlaybackOperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
            {
                bool first = Interlocked.Exchange(ref _pending, 0) == 1;
                int delay = first ? firstDecodeMilliseconds : subsequentDecodeMilliseconds;
                if (delay > 0)
                {
                    Thread.Sleep(delay);
                }

                return inner.TryDecode(packet, out frames);
            }

            public PlaybackOperationResult Drain(out IReadOnlyList<RawVideoFrame> frames) => inner.Drain(out frames);

            public PlaybackOperationResult Flush()
            {
                PlaybackOperationResult flushed = inner.Flush();
                Interlocked.Exchange(ref _pending, 1);
                return flushed;
            }

            public void Dispose() => inner.Dispose();
        }
    }

    [Test]
    public void PlaysAnAudioFileThroughTheRenderer()
    {
        string path = CreateWaveFile();
        try
        {
            FakeVideoRenderer videoRenderer = new();
            FakeAudioRenderer audioRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, audioRenderer);
            WarningLog warnings = new(pipeline);

            PlaybackOperationResult opened = Open(pipeline, path);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(opened.IsSuccessful, Is.True, opened.Message);
                Assert.That(pipeline.HasAudio, Is.True);
                Assert.That(pipeline.HasVideo, Is.False);
                Assert.That(warnings.Messages, Is.Empty, "nothing has to be said about a file that plays");
                Assert.That(audioRenderer.ConfiguredStream?.SampleRate, Is.EqualTo(SampleRate));
            }

            pipeline.Play();
            WaitForPlaybackToFinish(pipeline);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);
                Assert.That(audioRenderer.SubmittedBuffers, Is.GreaterThan(0));
                Assert.That(audioRenderer.SubmittedSamples, Is.EqualTo(SampleCount), "every sample has to reach the device");
                Assert.That(videoRenderer.RenderedFrames, Is.Zero);
                Assert.That(pipeline.Position, Is.GreaterThan(TimeSpan.Zero));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A device that holds everything it is given and never asks for more is not a failure and not a special
    /// case: the device is the clock, so a device that is not taking audio is time that is not moving, and
    /// everything that follows the clock waits with it. What is checked is that nothing is thrown away while
    /// the wait lasts, and that every sample reaches the device once it opens up again.
    /// </summary>
    [Test]
    public void WaitsForADeviceThatNeverTakesAudio()
    {
        string path = CreateWaveFile();
        try
        {
            FakeAudioRenderer audioRenderer = new() { QueuedSampleCount = SampleRate, NoRoom = true };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);

            pipeline.Play();
            Thread.Sleep(500);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "a device that takes nothing is a wait, not a failure");
                Assert.That(pipeline.Error, Is.Null);
                Assert.That(audioRenderer.SubmittedBuffers, Is.Zero, "the device never made room, so nothing was handed over");
                Assert.That(audioRenderer.AbandonedSubmits, Is.Zero, "waiting for room is not letting go of the block");
            }

            // Letting the device take audio again ends the wait, and what was carried through it is played
            // rather than dropped.
            audioRenderer.QueuedSampleCount = 0;
            audioRenderer.NoRoom = false;
            WaitForPlaybackToFinish(pipeline);
            Assert.That(audioRenderer.SubmittedSamples, Is.EqualTo(SampleCount), "every sample reaches the device once it opens up");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A device with no room parks the sound's thread with the hand-off suspended, not with the thread inside
    /// the renderer — and a seek has to reach it there. The hand-off belongs to the position it was made for,
    /// so it is let go of rather than resumed against the new one: the rest of it is a moment nobody is
    /// playing, and the seek's flush takes back what the device already holds.
    /// </summary>
    [Test]
    public void LetsGoOfTheBlockWhenThePositionMovesWhileTheDeviceHasNoRoom()
    {
        string path = CreateWaveFile(SampleRate);
        try
        {
            FakeAudioRenderer audioRenderer = new() { QueuedSampleCount = SampleRate, NoRoom = true };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            // The first block is offered to a device that will not take it, so the thread waits for room.
            WaitFor(() => audioRenderer.SubmitsStarted > 0, "a block has to be offered before it can be stuck");
            Thread.Sleep(100);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "a device with no room is a wait, not a failure");
                Assert.That(audioRenderer.SubmittedBuffers, Is.Zero, "the block is still on its way, so nothing has been taken");
                Assert.That(audioRenderer.AbandonedSubmits, Is.Zero, "waiting for room is not letting go of the block");
            }

            // The position moves on while the hand-off is suspended: this is the wait that used to live inside
            // the renderer, where a seek could not reach it.
            SeekAndWait(pipeline, TimeSpan.FromSeconds(0.5));
            WaitFor(() => audioRenderer.AbandonedSubmits > 0, "the seek has to reach the hand-off that is waiting for room");

            // And the sound carries on from where the seek went.
            audioRenderer.QueuedSampleCount = 0;
            audioRenderer.NoRoom = false;
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForPlaybackToFinish(pipeline);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A decoder that cannot keep up must not take the sound down with it, and must not let the picture drift
    /// away from the sound either: the clock is held back for the picture instead, and the picture catches up
    /// while the sound waits for it. What is checked is that the sound keeps arriving, the picture keeps
    /// being shown, and neither is ever left far behind by the other.
    /// </summary>
    [Test]
    public void HoldsTheClockBackWhenTheVideoCannotCatchUp()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            // One decode every 80 ms against a stream that needs one every 33 ms: the decoder is two and a
            // half times too slow, and the sound is given to the device at the rate a device would take it,
            // so the file lasts as long as it says it does.
            FakeAudioRenderer audioRenderer = new() { SubmitDelayMilliseconds = 30 };
            using PlaybackPipeline pipeline = CreatePipeline(
                new FakeVideoRenderer(),
                audioRenderer,
                new ThrottledVideoDecoderFactory { DecodeMilliseconds = 80 });

            PlaybackOperationResult opened = Open(pipeline, path, new FakeRenderTarget());
            Assert.That(opened.IsSuccessful, Is.True, opened.Message);
            pipeline.Play();

            double worstLag = 0;
            long samplesHalfway = 0;
            bool heldForThePicture = false;
            for (int attempt = 0; attempt < 40; attempt++)
            {
                Thread.Sleep(50);
                worstLag = Math.Max(worstLag, pipeline.PictureLag.TotalMilliseconds);
                heldForThePicture |= pipeline.StarvedFor == "video";
                if (attempt == 19)
                {
                    samplesHalfway = audioRenderer.SubmittedSamples;
                }
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.Error, Is.Null);
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "the file is nowhere near its end");
                Assert.That(pipeline.PresentedFrameCount, Is.GreaterThan(0), "the picture keeps being shown");
                Assert.That(audioRenderer.SubmittedSamples, Is.GreaterThan(samplesHalfway), "the sound keeps arriving while the decoder is behind");
                Assert.That(heldForThePicture, Is.True, "a picture that cannot keep up is what has to hold the clock");
                Assert.That(worstLag, Is.LessThan(700), "the picture is never left far behind the sound");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A renderer that needs longer than a frame interval is what the drop rule exists for, and the rule says
    /// what may be lost and when: only a frame whose moment has gone, only when a closer one is already
    /// waiting, and never any of the sound. The picture stays in step by skipping, not by falling further
    /// behind on every frame.
    /// </summary>
    /// <summary>
    /// The picture's starvation is decided against the clock, and the thread that decides it is the one the
    /// decoder's quiet leaves parked on an empty queue. Waiting there for an entry with no deadline is how a
    /// starving picture went unreported: the clock ran on while nothing was being decoded, the hold arrived
    /// only when the next frame turned up, and by then the picture was as far behind as the decoder had been
    /// quiet for. What the hold needs is a wait that ends when the *verdict* can change — which, while the
    /// clock is still the sound's to claim, is not even a question about the media yet.
    /// </summary>
    [Test]
    public void HoldsTheClockBackWhileTheDecoderIsQuiet()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            // The sound's own first block is what claims the clock, and it is handed over on another thread —
            // so the claim is held back here until after this thread has had its say, which is the arrangement
            // in which nothing the picture is waiting for would ever make the verdict readable. The device
            // holding audio is what keeps the sound itself from being the reason time is stopped.
            FakeAudioRenderer audioRenderer = new() { SubmitDelayMilliseconds = 30, QueuedSampleCount = SampleRate };
            using PlaybackPipeline pipeline = CreatePipeline(
                new FakeVideoRenderer(),
                audioRenderer,
                new ThrottledVideoDecoderFactory { DecodeMilliseconds = 1500, FastPackets = 1 },
                audioDecoders: new PacedAudioDecoderFactory { DecodeMilliseconds = 150 });

            PlaybackOperationResult opened = Open(pipeline, path, new FakeRenderTarget());
            Assert.That(opened.IsSuccessful, Is.True, opened.Message);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForPresentedFrames(pipeline, 1);

            // The hold has to arrive while the decoder is still quiet: the threshold is 200 ms and the next
            // frame is a second and a half away, so this waits well inside the quiet and a wait that ends when
            // a frame does fails it — which is what the wait this replaces would do.
            WaitFor(() => pipeline.StarvedFor == "video", "the picture has nothing to show, so time has to be held", 800);

            // Held near the threshold rather than at whatever the clock had run to by the time a frame turned
            // up: a picture left behind for the whole of the decoder's quiet shows as a gap of about that
            // length between the clock and the last frame the picture showed.
            TimeSpan held = pipeline.Position;
            Assert.That(pipeline.PictureLag, Is.LessThan(TimeSpan.FromMilliseconds(700)),
                "the hold is what keeps the clock from running away from a picture that has nothing to show");

            // A held clock is a clock that does not move: the sound waits for the picture rather than leaving
            // it further behind, which is what reporting the verdict is for.
            Thread.Sleep(300);
            Assert.That(pipeline.Position, Is.EqualTo(held), "a held clock does not move");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The cadence itself: the verdict is read at the top of the loop, and the thread is idle between
    /// iterations, so the wait has to end when the *clock* reaches the point at which a picture this far
    /// behind counts as starving — a wait that ends only when a frame does reports the starvation as many
    /// seconds late as the decoder was quiet.
    /// <para>
    /// A file with no sound is what makes this the only thing being tested: the clock is claimed by this
    /// thread as it shows the first frame that plays, so by the time the decoder goes quiet the verdict is
    /// judgeable and false — the clock is level with the picture — and nothing but the clock's own progress
    /// would change it. And the cadence is fixed rather than derived from the clock's state, so the hold does
    /// not depend on the clock having been running, claimed or at any particular rate when the thread went
    /// idle.
    /// </para>
    /// </summary>
    [Test]
    public void HoldsTheClockBackWhenTheClockReachesTheThreshold()
    {
        // Long enough that the reader is still reading when the decoder goes quiet: a file whose packets have
        // all been read is a file that has run out, and a picture is not "behind" a file that has run out.
        string path = CreateY4mFile(frameCount: 200);
        try
        {
            using PlaybackPipeline pipeline = CreatePipeline(
                new FakeVideoRenderer(),
                new FakeAudioRenderer(),
                // Slow enough on the first packet that playback is certainly running before the first frame
                // arrives — so the frame is a presentation that claims the clock, not the paused position's
                // still, which claims nothing — and then quiet for 1300 ms with the clock level with it.
                new SlowFirstDecodeVideoDecoderFactory { FirstDecodeMilliseconds = 400, SubsequentDecodeMilliseconds = 1300 });

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitForPresentedFrames(pipeline, 1);

            // Without the cadence the verdict is only read when a frame arrives — and by then the decoder is
            // ahead again, so the hold never arrives at all.
            WaitFor(() => pipeline.StarvedFor == "video", "the clock has run past a picture that has nothing more to show", 1800);

            TimeSpan held = pipeline.Position;
            Assert.That(pipeline.PictureLag, Is.LessThan(TimeSpan.FromMilliseconds(700)),
                "the hold is what keeps the clock from running away from a picture that has nothing to show");

            Thread.Sleep(300);
            Assert.That(pipeline.Position, Is.EqualTo(held), "a held clock does not move");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The clock is told what to do only when the answer has changed. Several things decide it in a row — a
    /// state change, then each starvation flag, or the reverse — and a clock told the same thing twice is a
    /// clock whose Start and Pause have to tolerate it, which the interface does not promise: a naïve Start
    /// re-anchors from now, and a naïve Pause records a stop the device never had.
    /// <para>
    /// The starvation hold is what makes the difference visible: with the picture behind, the clock has
    /// already been stopped by the hold, so a pause that arrives while it is held changes nothing about what
    /// the clock should do — and a pipeline that tells the clock anyway says "pause" twice for one fact.
    /// </para>
    /// </summary>
    [Test]
    public void TellsTheClockOnlyWhenWhatItShouldDoChanges()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            // A decoder far too slow for the stream, so the hold lasts: the picture is behind for seconds, not
            // for the moment it takes to notice it once.
            FakeAudioRenderer audioRenderer = new() { SubmitDelayMilliseconds = 30 };
            using PlaybackPipeline pipeline = CreatePipeline(
                new FakeVideoRenderer(),
                audioRenderer,
                new ThrottledVideoDecoderFactory { DecodeMilliseconds = 200 });

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitFor(() => pipeline.StarvedFor == "video", "the picture has to fall behind for the clock to be held");

            // Held: the clock is stopped because time must not pass while the picture catches up, and the
            // count is taken while that is the state of affairs. The device is the audio thread's to stop, so
            // it follows the verdict a beat later.
            WaitFor(() => !audioRenderer.ClockIsRunning, "a held clock is a stopped clock");
            int pauses = audioRenderer.ClockPauses;
            int starts = audioRenderer.ClockStarts;

            // A pause while the clock is already stopped — because it is *held*, not because time had started —
            // does not change what the clock should do, so the clock is not told to pause again.
            Assert.That(Await(pipeline.Pause()).IsSuccessful, Is.True);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused));
                Assert.That(pipeline.StarvedFor, Is.EqualTo("video"), "the arrangement only means something while the picture is still behind");
                Assert.That(audioRenderer.ClockPauses, Is.EqualTo(pauses), "the clock is not told what it already knows");
                Assert.That(audioRenderer.ClockStarts, Is.EqualTo(starts));
                Assert.That(audioRenderer.ClockIsRunning, Is.False);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Time must not pass while nothing is playing. A sound with nothing to hand over leaves the device
    /// holding what it was given, and that is being heard — so the clock is held only once the device is
    /// empty, which is a fact about what is audible rather than about how far behind the reader is. The
    /// device cannot signal, so this is the one wait in the pipeline paced by the clock instead of by
    /// something arriving.
    /// </summary>
    [Test]
    public void HoldsTheClockBackWhenTheSoundHasNothingToPlay()
    {
        string path = CreateWaveFile();
        try
        {
            // One block every 200 ms is a pipe slower than the device: the sound hands each block over as it
            // arrives and has nothing left for most of the time, and a device reporting nothing queued is a
            // device with nothing left to play.
            FakeAudioRenderer audioRenderer = new() { QueuedSampleCount = 0 };
            using PlaybackPipeline pipeline = CreatePipeline(
                new FakeVideoRenderer(),
                audioRenderer,
                audioDecoders: new PacedAudioDecoderFactory { DecodeMilliseconds = 200 });

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => audioRenderer.SubmittedSamples > 0, "the first block has to reach the device");

            // Being starved and letting time pass at once is what this pins against, and the two are read
            // together so a stop/start cycle cannot pass for an answer.
            WaitFor(
                () => pipeline.StarvedFor == "audio" && !audioRenderer.ClockIsRunning,
                "a sound with nothing to play and a device with nothing in it has to hold the clock");

            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "waiting for the sound is not a state change");
            Assert.That(pipeline.Error, Is.Null);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void DropsOnlyTheFramesWhoseMomentHasGone()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            // A hundred milliseconds to show a frame that is due every thirty-three: the picture cannot keep
            // up whatever it does, so it has to choose what not to show.
            FakeVideoRenderer videoRenderer = new() { RenderDelayMilliseconds = 100 };
            FakeAudioRenderer audioRenderer = new() { SubmitDelayMilliseconds = 30 };
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, audioRenderer);

            PlaybackOperationResult opened = Open(pipeline, path, new FakeRenderTarget());
            Assert.That(opened.IsSuccessful, Is.True, opened.Message);
            pipeline.Play();

            Thread.Sleep(1500);

            long presented = pipeline.PresentedFrameCount;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.Error, Is.Null);
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "falling behind is not failing");
                Assert.That(presented, Is.GreaterThan(0), "something has to be shown");
                Assert.That(pipeline.DroppedFrameCount, Is.GreaterThan(0), "a picture that cannot keep up has to give up on frames");
                Assert.That(pipeline.PictureLag, Is.LessThan(TimeSpan.FromMilliseconds(400)), "and what it gives up on is what keeps it in step");
                Assert.That(audioRenderer.SubmittedSamples, Is.GreaterThan(0), "the sound is not what pays for the picture");
            }

            // The picture carries on from there rather than stopping on the frames it let go of.
            WaitForPresentedFrames(pipeline, presented + 2);
            Assert.That(pipeline.Error, Is.Null);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A paused picture still has to follow the window: the host reports a new surface size or scale, and
    /// nothing is being presented, so the renderer is never asked to draw — which is how a paused video
    /// ends up stretched into a resized window. The presentation thread offers the change up while paused,
    /// and offers it while presenting nothing else.
    /// </summary>
    [Test]
    public void LetsTheRendererFollowTheSurfaceWhilePaused()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer { SubmitDelayMilliseconds = 30 });

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            pipeline.Play();
            WaitForPresentedFrames(pipeline, 2);
            pipeline.Pause();

            // The refresh is what the paused branch does, and when it arrives depends on when the pause reaches
            // that thread — so it is waited for rather than assumed to come after the line below. What must not
            // happen is a frame being shown, and that is what the count is checked for afterwards.
            int refreshesWhenPaused = videoRenderer.RefreshedSurfaces;
            var watch = Stopwatch.StartNew();
            while (videoRenderer.RefreshedSurfaces == refreshesWhenPaused && watch.ElapsedMilliseconds < 2000)
            {
                Thread.Sleep(5);
            }

            long presentedWhenPaused = videoRenderer.RenderedFrames;
            Thread.Sleep(150);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused));
                Assert.That(videoRenderer.RefreshedSurfaces, Is.GreaterThan(refreshesWhenPaused), "a paused renderer has to be given the chance to follow the surface");
                Assert.That(videoRenderer.RenderedFrames, Is.EqualTo(presentedWhenPaused), "and nothing is presented while paused");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A thread with nothing to do waits rather than polls, so the host's word is what has to reach it: each
    /// surface change arriving while paused has to be taken up as it arrives, not at the next time the
    /// sleeping thread happens to look. The test counts the offers while several changes arrive in quick
    /// succession: a pipeline that leaned on its quarter-second backstop would take up at most one of them.
    /// </summary>
    [Test]
    public void WakesForEachSurfaceChangeWhilePausedRatherThanWaitingForTheBackstop()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            FakeRenderTarget target = new();
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer { SubmitDelayMilliseconds = 30 });

            Assert.That(Open(pipeline, path, target).IsSuccessful, Is.True);
            pipeline.Play();
            WaitForPresentedFrames(pipeline, 1);
            pipeline.Pause();

            // Long enough that the thread has certainly finished the frame it was waiting for and is now
            // parked, where only a signal or the backstop can wake it.
            Thread.Sleep(400);

            const int changes = 4;
            int before = videoRenderer.RefreshedSurfaces;
            for (int index = 0; index < changes; index++)
            {
                target.RaiseSizeChanged();
                Thread.Sleep(30);
            }

            // The whole window is kept well inside the quarter-second backstop, so a pipeline that did not
            // wake for the changes could take up at most one of them and the count cannot reach four by
            // waiting: what it counts is the waking, not the time that passed.
            var watch = Stopwatch.StartNew();
            while (videoRenderer.RefreshedSurfaces < before + changes && watch.ElapsedMilliseconds < 150)
            {
                Thread.Sleep(5);
            }

            Assert.That(
                videoRenderer.RefreshedSurfaces,
                Is.GreaterThanOrEqualTo(before + changes),
                "each surface change has to wake the thread that is waiting for one");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void PausesAndResumes()
    {
        string path = CreateWaveFile(SampleRate);
        try
        {
            FakeAudioRenderer audioRenderer = new() { SubmitDelayMilliseconds = 10 };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);

            pipeline.Play();
            Thread.Sleep(50);
            pipeline.Pause();

            // Posted, so where the pause takes effect is what says it happened.
            WaitForState(pipeline, MediaEngineState.Paused);

            // The clock is stopped by the render threads rather than by the pause itself — they are the ones
            // that decide it, and the pause is what wakes them — so the device stops a moment after the state
            // does, which is the moment this waits for.
            WaitFor(() => !audioRenderer.ClockIsRunning, "a paused pipeline has to stop the clock");

            TimeSpan paused = pipeline.Position;
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused));

            Thread.Sleep(50);
            Assert.That(pipeline.Position, Is.EqualTo(paused), "a paused pipeline must not move");

            // Pausing what is already paused names a state the pipeline is not in, so it is refused like any
            // other command that cannot be carried out — and nothing about playback changes with it.
            PlaybackOperationResult pausedAgain = Await(pipeline.Pause());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pausedAgain.IsSuccessful, Is.False);
                Assert.That(pausedAgain.Message, Does.Contain("Paused"), "the refusal names the state that made it a refusal");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused));
            }

            // Playing what is already playing is the same kind of refusal, from the other side.
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.False);

            WaitForPlaybackToFinish(pipeline);

            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void SeeksWhilePlayingAndLeavesItPaused()
    {
        string path = CreateWaveFile(SampleRate);
        try
        {
            FakeAudioRenderer audioRenderer = new() { SubmitDelayMilliseconds = 5 };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            Thread.Sleep(40);

            var target = TimeSpan.FromSeconds(0.5);
            SeekAndWait(pipeline, target);

            // The seek is carried out before the pipeline says so, so everything about it can be read here:
            // playback is paused whatever it was doing before — a seek is a state change of its own, and
            // whether the new position goes on playing is the host's call.
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused), "a seek leaves playback paused, whatever it was");

            // Where the clock is, though, is not the seek's to say: the clock belongs to the render threads,
            // and the one that wakes first places it at the position that was asked for — a moment after the
            // seek has answered, which is why this is waited for rather than read.
            WaitFor(
                () => pipeline.Position >= target - TimeSpan.FromMilliseconds(50),
                $"The clock is at {pipeline.Position:mm\\:ss\\.fff} where the seek asked for {target:mm\\:ss\\.fff}.");

            // And the host's call is one command: play, and the file runs on from there.
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitForPlaybackToFinish(pipeline);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void SeeksWhilePausedAndStaysPaused()
    {
        string path = CreateWaveFile(SampleRate);
        try
        {
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), new FakeAudioRenderer());

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);

            var target = TimeSpan.FromSeconds(0.25);
            SeekAndWait(pipeline, target);

            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused), "seeking must not start playback");

            // The clock is placed at the position a seek asked for by the first render thread to wake, which
            // is a moment after the seek itself has answered.
            WaitFor(
                () => pipeline.Position >= target - TimeSpan.FromMilliseconds(50),
                $"The clock is at {pipeline.Position:mm\\:ss\\.fff} where the seek asked for {target:mm\\:ss\\.fff}.");

            pipeline.Play();
            WaitForPlaybackToFinish(pipeline);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A seek while paused has to move the reader and the decoder, not just the clock. Both are parked by the
    /// picture being paused rather than by a state check — the decoder waiting for room in a full frame queue
    /// that only the picture can empty, the reader idling on a packet queue it has filled — so a seek that
    /// left the queues alone left both of them on the old position: the new position's frames were not
    /// decoded until something else made room, which on resume means playing the old position out first.
    /// Resetting the queues is what releases them.
    /// </summary>
    [Test]
    public void SeeksWhilePausedAndMovesTheReaderAndDecoder()
    {
        string path = CreateY4mFile(frameCount: 200);
        try
        {
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), new FakeAudioRenderer());

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForPresentedFrames(pipeline, 2);
            Assert.That(Await(pipeline.Pause()).IsSuccessful, Is.True);

            // Long enough for the decoder to fill the frame queue and stop, and for the reader to fill the
            // packet queue: both are parked on the old position, which is the state the seek has to break.
            Thread.Sleep(400);

            // The position asked for is far beyond what the old one can reach: the decoder stops with the
            // frame queue full, so the newest frame it has produced is only a few frames past the last one
            // the picture took — around 20 s on this fixture — whatever the reader has queued behind it.
            SeekAndWait(pipeline, TimeSpan.FromSeconds(60));

            // Still paused, and a picture of the position that was asked for has already been produced: the
            // decoder was released and told to take the position up, and the reader is reading it rather than
            // sitting on the old position's queue. Nothing but the reset can put either of them there, which
            // is what this asserts — the old position cannot reach 50 s.
            WaitFor(() => pipeline.LastPresentedTimestamp >= TimeSpan.FromSeconds(50),
                "the picture of the position a paused seek asked for must have been shown");

            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused), "seeking must not start playback");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A paused file shows the picture of where it is: after a seek that landed while nothing was being
    /// presented, that is the new position's first frame — offered by the picture thread from the queue the
    /// decoder filled, once, because it is a still and not a presentation. The wait for it has no clock in
    /// it: the frame arriving is the wake, which is why the seek's own signal only has to reach the thread.
    /// </summary>
    [Test]
    public void ShowsTheStillOfThePositionASeekMovesToWhilePaused()
    {
        string path = CreateY4mFile(frameCount: 200);
        try
        {
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer());

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForPresentedFrames(pipeline, 1);
            Assert.That(Await(pipeline.Pause()).IsSuccessful, Is.True);

            long shownBefore = videoRenderer.RenderedFrames;
            SeekAndWait(pipeline, TimeSpan.FromSeconds(20));

            WaitFor(() => videoRenderer.RenderedFrames >= shownBefore + 1,
                "a paused seek has to show the new position's picture");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused), "showing a still is not playing");

                // A still claims no anchor: the clock is left where the seek put it (20 s) rather than moved
                // to the frame the still showed (18 s — the keyframe a demuxer lands on). Anchoring it would
                // make the next frame, due at 20 s, two seconds early on resume, and the picture would wait
                // that out.
                Assert.That(pipeline.Position, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(19)), "a still must not move the clock");
                Assert.That(pipeline.DroppedFrameCount, Is.Zero, "a still is not a presentation, so nothing about it is late");
                Assert.That(videoRenderer.RenderedFrames, Is.EqualTo(shownBefore + 1), "one frame is offered, not several");
            }

            // One frame per position, not a slideshow: the screen keeps the position's picture while nothing
            // moves, however long that is.
            Thread.Sleep(400);
            Assert.That(videoRenderer.RenderedFrames, Is.EqualTo(shownBefore + 1), "the still is offered once per position");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Pausing without moving shows nothing new: the picture on the screen is already this position's, and a
    /// still of a frame that has just been shown would be a frame shown twice — worse than that, it would jump
    /// backwards to the oldest frame the decoder is holding.
    /// <para>
    /// Two frames are shown while playing before the pause, because that is what makes this a test of the
    /// guard rather than of a position nothing was ever shown for: with one frame, the still of the *open*
    /// would be the position's picture already.
    /// </para>
    /// </summary>
    [Test]
    public void DoesNotShowAStillWhenNothingMoved()
    {
        string path = CreateY4mFile(frameCount: 200, frameRate: "1:1");
        try
        {
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer());

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForPresentedFrames(pipeline, 2);

            // Counted before the pause, and the next frame is a second away, so nothing can be shown between
            // the count and the pause taking effect.
            long shownBeforePause = videoRenderer.RenderedFrames;
            Assert.That(Await(pipeline.Pause()).IsSuccessful, Is.True);

            Thread.Sleep(400);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused));
                Assert.That(videoRenderer.RenderedFrames, Is.EqualTo(shownBeforePause), "a pause that moved nothing shows nothing");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A position with no frame at all is a position whose picture has been shown to its end, and that is the
    /// picture's word for the ending — said where it is learned rather than left for a presentation that is
    /// not coming. Nothing is played here: the file is opened, its video has nothing to offer, and the file
    /// is over.
    /// </summary>
    [Test]
    public void EndsAtOnceWhenThePausedPositionHasNoPicture()
    {
        string path = CreateY4mFile(frameCount: 0);
        try
        {
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), new FakeAudioRenderer());

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            WaitForState(pipeline, MediaEngineState.Ended);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A seek from the end has to move the reader, and nothing plays afterwards to help it: the reader is
    /// parked where a file that has run out leaves it, with no timeout to wake it, so the seek's signal is
    /// the only thing that can — and it only helps if it arrives after the epoch does. Woken in between, the
    /// reader reads the position playback has left, waits again, and the file never moves.
    /// </summary>
    [Test]
    public void TakesUpASeekFromTheEndWithoutAnotherCommand()
    {
        string path = CreateBitmapFile();
        try
        {
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer());

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForPlaybackToFinish(pipeline);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);

            long shownBefore = videoRenderer.RenderedFrames;
            SeekAndWait(pipeline, TimeSpan.Zero);

            // The still of the new position can only be a frame the reader read for it, so this is the reader
            // taking the seek up on its own — with no play, no pause and no second seek to wake it.
            WaitFor(() => videoRenderer.RenderedFrames > shownBefore,
                "the reader has to take up a seek from the end without another command");
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void PlaysAgainAfterTheEndOfTheFile()
    {
        // The end of the file used to be the end of the video as well: once the stream was declared
        // finished nothing more was accepted, so replaying showed a picture that never came back.
        string path = CreateBitmapFile();
        try
        {
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer());

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            pipeline.Play();
            WaitForPlaybackToFinish(pipeline);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);

            long once = pipeline.PresentedFrameCount;
            Assert.That(once, Is.GreaterThan(0), "the frame should have been shown once");

            // A seek from the end is posted like everything else, and the play after it is queued behind it,
            // so the position is back at the beginning by the time playback starts again.
            SeekAndWait(pipeline, TimeSpan.Zero);
            pipeline.Play();

            WaitForPresentedFrames(pipeline, once + 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A stop has to release every thread, and a thread parked on an empty queue is the one with no other way
    /// out: there is no clock in that wait, so the close is the word it is waiting for, and the stop's joins
    /// depend on it arriving. The arrangement is the one where the picture is parked most of the time — a
    /// decoder far too slow for the stream, so the queue it waits on is empty — and the stop is bounded, so a
    /// thread left parked fails this test rather than hanging the suite.
    /// </summary>
    [Test]
    public void StopsWhileAThreadIsParkedOnAnEmptyQueue()
    {
        string path = CreateY4mFile(frameCount: 200);
        try
        {
            using PlaybackPipeline pipeline = CreatePipeline(
                new FakeVideoRenderer(),
                new FakeAudioRenderer(),
                new ThrottledVideoDecoderFactory { DecodeMilliseconds = 80 });

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForPresentedFrames(pipeline, 2);

            // The picture has just shown a frame and the decoder is 80 ms from producing the next one, which
            // is where the queue it takes frames from is empty and it is parked.
            Thread.Sleep(20);

            Assert.That(Await(pipeline.Stop()).IsSuccessful, Is.True, "a stop has to release a parked consumer");
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.None), "a stop leaves nothing open");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A stop ends a playback, not the pipeline: the same instance opens the file again and plays it, which
    /// is the path the stop's epoch advance exists for — everything in flight belongs to a position that no
    /// longer exists afterwards, so a report posted by a thread on its way out cannot become the next
    /// playback's statement that the file has run out. The window is milliseconds wide and this test does not
    /// hit it (the advance was removed and the suite still passed, which is what that costs); what it covers
    /// is the path: the second playback runs the file to the end and hands every sample over.
    /// </summary>
    [Test]
    public void PlaysAgainAfterAStop()
    {
        string path = CreateWaveFile();
        try
        {
            FakeAudioRenderer audioRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForPlaybackToFinish(pipeline);

            Assert.That(Await(pipeline.Stop()).IsSuccessful, Is.True);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.None), "a stop leaves nothing open");

            int playedOnce = audioRenderer.SubmittedSamples;
            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForPlaybackToFinish(pipeline);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);
                Assert.That(audioRenderer.SubmittedSamples - playedOnce, Is.EqualTo(SampleCount),
                    "the second playback plays the whole file rather than believing it has already run out");
                Assert.That(pipeline.Position, Is.GreaterThan(TimeSpan.Zero));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void SeeksWhileTheNextFrameIsStillWaiting()
    {
        // Long enough that the reader is still in the middle of the file when the seek arrives: with the
        // frame queue full and the picture thread waiting on a frame two seconds away, every thread that
        // matters is in the state the seek has to get through.
        string path = CreateY4mFile(frameCount: 200);
        try
        {
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer());

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            pipeline.Play();

            // The first frame is due straight away, and the second one is two seconds away, so the thread
            // showing frames settles on it and the queue behind it fills up.
            WaitForPresentedFrames(pipeline, 1);
            Thread.Sleep(300);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.PresentedFrameCount, Is.EqualTo(1), "the second frame must not be due yet");
                Assert.That(videoRenderer.RenderedFrames, Is.EqualTo(1));
                Assert.That(pipeline.DroppedFrameCount, Is.Zero, "waiting two seconds for a frame is not a reason to give up on it");
            }

            // The reader is now waiting for room that only that frame's moment can free, and it holds the
            // lock a seek needs. Seeking has to get past it without waiting for the picture.
            SeekFromAnotherThread(pipeline, TimeSpan.FromSeconds(20));

            // Where the clock ends up is where the file resumed rather than where the seek asked to go: this
            // fixture holds a frame every two seconds, so a seek to 20 s resumes at 18 s and the clock follows
            // it once the first frame of the new position arrives. Both are far ahead of the two seconds
            // playback was at. The seek leaves playback paused, which is its own state change and not a
            // failure of the same thing.
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused), "a seek leaves playback paused, whatever it was");

            // Where the clock is, though, is not the seek's to say: the clock belongs to the render threads,
            // and the one that wakes first places it at the position that was asked for — a moment after the
            // seek has answered, which is why this is waited for rather than read.
            WaitFor(
                () => pipeline.Position >= TimeSpan.FromSeconds(18),
                "the clock has to be the new position's rather than the one playback has left");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A seek has to let go of what the device still holds, and the device is the one part of the pipeline
    /// whose state no seek can rewrite from outside: it belongs to the audio thread, so that is where the
    /// move is taken up.
    /// <para>
    /// It matters twice over. What the device holds was produced for where playback used to be, so leaving
    /// it there plays a moment of the old position over the start of the new one. And because the clock
    /// counts what the device plays, a device that was never reset keeps counting from the old position's
    /// travel — a clock that jumps forward the moment it is re-anchored, and a picture that races to catch
    /// up with it. That is what "the video speeds up after a seek" was.
    /// </para>
    /// </summary>
    /// <summary>
    /// The end of a file is played out by the stages that still hold media, and the queue is told about it
    /// rather than held by it: the queue every command is posted to has to stay free while the tail plays
    /// out, or a seek is answered when the file has finished instead of when it was asked for. The device is
    /// jammed here after it has taken the whole of the sound, which leaves the ending outstanding for as long
    /// as the sound has not been heard.
    /// </summary>
    [Test]
    public void AnswersACommandWhileTheSoundHasNotPlayedOut()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            FakeAudioRenderer audioRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            // The fixture is 200 frames at 30 fps with 44.1 kHz sound, so a device that has been handed this
            // many samples has been handed the whole of the file's audio — and the reader, which is ahead of
            // the device everywhere else, has read the end of the file to do it.
            const int totalSamples = SampleRate * 200 / 30;
            WaitFor(() => audioRenderer.SubmittedSamples >= totalSamples, "the reader has to reach the end of the file");
            audioRenderer.QueuedSampleCount = SampleRate;
            audioRenderer.NoRoom = true;

            var askedAt = Stopwatch.StartNew();
            PlaybackOperationResult seeked = Await(pipeline.Seek(TimeSpan.FromSeconds(1)));
            askedAt.Stop();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(seeked.IsSuccessful, Is.True, seeked.Message);
                Assert.That(askedAt.ElapsedMilliseconds, Is.LessThan(1000),
                    "a command must not wait for the end of the file to play out");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused), "the seek is answered at once and leaves playback paused, rather than being the end of a file");
                Assert.That(pipeline.Error, Is.Null);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The other half of the same model: an ending that is waiting for the sound is waiting for the sound, not
    /// for a timeout — so the file ends when the device has played the last of it, at the moment it does.
    /// </summary>
    [Test]
    public void EndsWhenTheDeviceHasPlayedOutTheSound()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            FakeAudioRenderer audioRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            const int totalSamples = SampleRate * 200 / 30;
            WaitFor(() => audioRenderer.SubmittedSamples >= totalSamples, "the reader has to reach the end of the file");

            // Now that the whole sound has been handed over, the device holds audio and takes none of it,
            // and the seek puts playback near the end so the picture has almost nothing left to show: what
            // holds the ending up is the sound and nothing else.
            audioRenderer.QueuedSampleCount = SampleRate;
            audioRenderer.NoRoom = true;
            Assert.That(Await(pipeline.Seek(TimeSpan.FromSeconds(6))).IsSuccessful, Is.True);
            Thread.Sleep(200);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused), "a sound that has not played out is not an ending");

            // The seek left playback paused — that is what a seek does — so putting it back on the road to
            // the end is the host's call, and only then can the ending be earned.
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            // The device takes audio again, so the last of the sound is played and the ending is earned.
            audioRenderer.QueuedSampleCount = 0;
            audioRenderer.NoRoom = false;
            WaitForPlaybackToFinish(pipeline);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A file whose picture outlasts its sound is the ordinary shape — the container's duration is the longest
    /// stream's, so the last video frame is usually a frame or two past the last audio sample, and in an edited or
    /// damaged file it can be seconds past it. The device's clock stops on that last sample; if the pipeline's clock
    /// were the device's, every frame due after it would wait for a moment that can never arrive and the file would
    /// never end. It is the pipeline's own clock, so time goes on and the tail plays.
    /// </summary>
    [Test]
    public void EndsWhenThePictureOutlastsTheSound()
    {
        string path = RawAudioVideoFixtureFile.Create(frameCount: 40, videoFramesAfterTheSound: 3);
        try
        {
            FakeAudioRenderer audioRenderer = new() { PlaysWhatItIsGiven = true };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitForPlaybackToFinish(pipeline);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);
                Assert.That(pipeline.PresentedFrameCount, Is.GreaterThan(0), "the picture is shown, including its tail");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The same shape the other way round: a sound that runs on after the picture has ended. The picture has nothing
    /// left here, and the hold it would otherwise take — "the picture is behind the clock" — is a hold on the clock
    /// the rest of the sound still needs, so playback would stop dead at the moment the picture ran out. What tells
    /// the two apart is what the reader has already taken in: nothing more of the picture at this position, and
    /// plenty of the sound (§8).
    /// </summary>
    [Test]
    public void KeepsPlayingWhenThePictureEndsLongBeforeTheSound()
    {
        // Two seconds of picture and four of sound: the last frame is two seconds before the last sample, far beyond
        // any queue that a picture which was merely behind would be waiting for.
        string path = RawAudioVideoFixtureFile.Create(frameCount: 60, soundSecondsAfterThePicture: 2);
        try
        {
            FakeAudioRenderer audioRenderer = new() { PlaysWhatItIsGiven = true };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            PlaybackOperationResult opened = Open(pipeline, path, new FakeRenderTarget());
            Assert.That(opened.IsSuccessful, Is.True, opened.Message);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitFor(
                () => pipeline.Position > TimeSpan.FromSeconds(3),
                "the sound has to carry playback past the end of the picture",
                6000);

            Assert.That(pipeline.StarvedFor, Is.Not.EqualTo("video"), "a picture with nothing left here is not one that is behind");

            WaitForPlaybackToFinish(pipeline);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A stream that stops arriving while the rest of the file goes on, from a source that never reports the end of
    /// the file: the one shape in which only what the reader has taken in can say whether a stage is spent or merely
    /// behind (§8). A hold taken for the picture here stops the clock, and the sound with it, for good.
    /// </summary>
    [Test]
    public void KeepsTheSoundPlayingWhenThePictureStopsArriving()
    {
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(3),
            VideoStopsAt = TimeSpan.FromSeconds(1),
            ReportsTheEndOfTheFile = false,
        };

        FakeVideoRenderer videoRenderer = new();
        FakeAudioRenderer audioRenderer = new() { PlaysWhatItIsGiven = true };
        SyntheticSourceProviderFactory sources = new(script);
        PlaybackPipeline pipeline = CreatePipeline(
            videoRenderer,
            audioRenderer,
            videoDecoders: new SyntheticVideoDecoderFactory(),
            sourceFactory: sources,
            audioDecoders: new SyntheticAudioDecoderFactory());

        try
        {
            PlaybackOperationResult opened = Open(pipeline, SyntheticPath(), new FakeRenderTarget());
            Assert.That(opened.IsSuccessful, Is.True, opened.Message);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            // The picture is given nothing after its first second and the sound runs to three, with no end of file
            // in sight: the sound carrying playback past two seconds is what says the picture did not stop it.
            WaitFor(
                () => pipeline.Position > TimeSpan.FromSeconds(2),
                "a picture that has nothing left here must not hold the clock the sound is still using",
                6000);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.StarvedFor, Is.Null, "nothing is behind; the picture simply has nothing left to show");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), pipeline.Error);
                Assert.That(videoRenderer.RenderedFrames, Is.GreaterThan(0), "the picture showed what it was given before the stream went quiet");
            }
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// The other side of the same rule, which is what keeps it from being "never hold the clock": when the stream
    /// that went quiet is the only one left — nothing has been read past the clock by either stream — the picture is
    /// not spent, it is waiting, and time must stop with it.
    /// </summary>
    [Test]
    public void HoldsTheClockWhenNothingIsLeftOfEitherStream()
    {
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(3),
            VideoStopsAt = TimeSpan.FromSeconds(1),
            AudioStopsAt = TimeSpan.FromSeconds(1),
            ReportsTheEndOfTheFile = false,
        };

        FakeAudioRenderer audioRenderer = new() { PlaysWhatItIsGiven = true };
        SyntheticSourceProviderFactory sources = new(script);
        PlaybackPipeline pipeline = CreatePipeline(
            new FakeVideoRenderer(),
            audioRenderer,
            videoDecoders: new SyntheticVideoDecoderFactory(),
            sourceFactory: sources,
            audioDecoders: new SyntheticAudioDecoderFactory());

        try
        {
            PlaybackOperationResult opened = Open(pipeline, SyntheticPath(), new FakeRenderTarget());
            Assert.That(opened.IsSuccessful, Is.True, opened.Message);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitFor(
                () => pipeline.Position > TimeSpan.FromSeconds(0.9) && pipeline.StarvedFor is not null,
                "both streams run out at one second, and time has to stop with them",
                4000);

            // The verdict reaches the device a beat later — the device is the audio thread's to stop — and the clock
            // may step back by the sync tolerance on the way, so the hold is read once it has settled.
            Thread.Sleep(150);
            TimeSpan held = pipeline.Position;
            Thread.Sleep(300);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.Position, Is.EqualTo(held), "a held clock does not move");
                Assert.That(pipeline.StarvedFor, Is.Not.Null, "both stages have nothing, and neither has data ahead of the clock");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "waiting is not a state change");
            }
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    private static string SyntheticPath()
    {
        return Path.Combine(Path.GetTempPath(), "sineplayer-synthetic-fixture");
    }

    /// <summary>
    /// The tail is not merely accepted, it is <em>paced</em>: a second of picture after the sound takes a second, so
    /// the last frames are shown at their own moments instead of all at once. That is what tells "time carried on at
    /// its own rate" from "the tail was let through as fast as it could be drawn".
    /// </summary>
    [Test]
    public void PlaysTheTailAtItsOwnRate()
    {
        // 40 frames of sound (1.33 s) and 15 more frames of picture (0.5 s), so the file lasts about 1.83 s and the
        // sound stops at 1.33 s.
        string path = RawAudioVideoFixtureFile.Create(frameCount: 55, videoFramesAfterTheSound: 15);
        try
        {
            FakeAudioRenderer audioRenderer = new() { PlaysWhatItIsGiven = true };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);

            var played = Stopwatch.StartNew();
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForPlaybackToFinish(pipeline);
            played.Stop();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);
                Assert.That(played.ElapsedMilliseconds, Is.GreaterThan(1500),
                    "the picture after the sound is played at its own rate, not flushed through at once");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A file that has played out ends even if the user pauses on the last of it. The ending is the stages
    /// saying they have nothing left, not a decision about the state, so a pause that lands while the last
    /// word is being said does not hold the file open — and nothing has to ask again for the file to be
    /// allowed to finish.
    /// <para>
    /// The pause is taken from inside the sound's own question to the device, which is the moment the word
    /// turns on: that is the only way this case can be *named* instead of raced for, and it is also the
    /// honest description of it — the user paused while the sound was finding out that the device had
    /// played the last of it out.
    /// </para>
    /// </summary>
    [Test]
    public void EndsWhilePausedWhenTheFileHasPlayedOut()
    {
        string path = CreateWaveFile();
        try
        {
            // The device still holds audio — the sound has nothing left to hand over, and the only thing
            // holding the ending up is the device not having played the last of it.
            FakeAudioRenderer audioRenderer = new() { QueuedSampleCount = SampleRate / 10 - 1 };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            List<MediaEngineState> said = [];
            pipeline.StateChanged += (state, _) =>
            {
                lock (said)
                {
                    said.Add(state);
                }
            };

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitFor(() => audioRenderer.SubmittedSamples >= SampleCount, "the reader has to hand the whole sound over");

            // The sound is asking the device whether it has played out, and the user pauses before the answer
            // comes back. The answer is then "yes, it has" — and that is the ending, with the file paused.
            PlaybackOperationResult? paused = null;
            audioRenderer.BeforeReadingQueuedSampleCount = () =>
            {
                paused = Await(pipeline.Pause());
                audioRenderer.QueuedSampleCount = 0;
            };

            WaitFor(() =>
            {
                lock (said)
                {
                    return said.Count > 0 && said[^1] == MediaEngineState.Ended;
                }
            }, "the file has to end even though it was paused");

            MediaEngineState[] changes;
            lock (said)
            {
                changes = [.. said];
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(paused?.IsSuccessful, Is.True, paused?.Message);
                Assert.That(changes, Has.Length.GreaterThanOrEqualTo(2));
                Assert.That(changes[^2], Is.EqualTo(MediaEngineState.Paused), "the file has to end from the pause, not from a resume");
                Assert.That(changes[^1], Is.EqualTo(MediaEngineState.Ended));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void LetsTheDeviceGoOfTheOldPositionWhenPlaybackMoves()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            FakeAudioRenderer audioRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            // The device has to have played something first, or there is nothing for the seek to let go of
            // and the test would pass for the wrong reason.
            WaitFor(() => audioRenderer.SubmittedBuffers > 5, "the device has to have taken some audio first");
            WaitFor(() => pipeline.Position > TimeSpan.FromMilliseconds(200), "the device's travel has to be under way");

            var target = TimeSpan.FromSeconds(2);
            int flushed = audioRenderer.FlushCount;
            Assert.That(Await(pipeline.Seek(target)).IsSuccessful, Is.True);

            WaitFor(() => audioRenderer.FlushCount > flushed, "the device was never let go of the position playback left");

            // And the clock reads as where the seek asked to go, not as that position plus the travel the
            // device had accumulated: the picture follows this, which is why it is worth asserting.
            Assert.That(pipeline.Position, Is.LessThanOrEqualTo(target + TimeSpan.FromMilliseconds(500)),
                "the clock must not carry the old position's device travel into the new one");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void StartsWhereTheFileResumesRatherThanWhereTheSeekAsked()
    {
        // A seek cannot land wherever it likes: a demuxer stops at the keyframe at or before the position
        // it was given. Anchoring the clock to the position that was asked for leaves the picture that far
        // behind the sound, every frame of it late enough to be dropped, and the picture never moves again.
        string path = VideoFixtureFile.CreateAv1(gopSize: VideoFixtureFile.FrameCount);
        try
        {
            FFmpegSourceProviderFactory sourceFactory = new();
            Assert.That(sourceFactory.TryOpen(MediaLocator.Parse(path), out IMediaSource? opened).IsSuccessful, Is.True);

            TimeSpan target;
            if (opened is null)
            {
                Assert.Fail("The fixture has to open.");
                return;
            }

            using (IMediaSource source = opened)
            {
                target = FindASeekThatMisses(source);
            }

            if (target == TimeSpan.Zero)
            {
                Assert.Ignore("The fixture resumes wherever it is asked to, so a seek that misses it cannot be tested.");
            }

            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer());

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            pipeline.Play();
            WaitForPresentedFrames(pipeline, 2);

            long presented = pipeline.PresentedFrameCount;
            SeekAndWait(pipeline, target);

            // The seek left playback paused, so the frames that have to follow from where the file resumed
            // are a decision the host makes: one play, and the picture carries on from there.
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            // The picture has to carry on from where the file resumed, and the frames counted from here are
            // all frames of the new position: if the clock stayed at the position that was asked for, every
            // one of them would be late and none of them would ever be shown.
            WaitForPresentedFrames(pipeline, presented + 3);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.Position, Is.LessThan(target), "the clock has to follow where playback resumed");
                Assert.That(pipeline.PictureLag, Is.LessThan(TimeSpan.FromSeconds(1)), "the picture has to stay in step with the clock");
                Assert.That(pipeline.DroppedFrameCount, Is.LessThan(5), "frames from the resume point are not late");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A seek places the clock at the position that was asked for, and the media replaces it the moment the
    /// first of it plays (see <see cref="StartsWhereTheFileResumesRatherThanWhereTheSeekAsked"/>). Every
    /// decision the clock is made afterwards has to leave it where the media put it: a pause is such a
    /// decision, and placing the request again on the way would move time forward to a position nothing is
    /// playing — every frame of it late from then on, and the host reading a position nobody asked for.
    /// </summary>
    [Test]
    public void KeepsTheClockWhereTheMediaPutItWhenSomethingElseChangesIt()
    {
        string path = VideoFixtureFile.CreateAv1(gopSize: VideoFixtureFile.FrameCount);
        try
        {
            FFmpegSourceProviderFactory sourceFactory = new();
            Assert.That(sourceFactory.TryOpen(MediaLocator.Parse(path), out IMediaSource? opened).IsSuccessful, Is.True);

            TimeSpan target;
            if (opened is null)
            {
                Assert.Fail("The fixture has to open.");
                return;
            }

            using (IMediaSource source = opened)
            {
                target = FindASeekThatMisses(source);
            }

            if (target == TimeSpan.Zero)
            {
                Assert.Ignore("The fixture resumes wherever it is asked to, so a seek that misses it cannot be tested.");
            }

            FakeAudioRenderer audioRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            pipeline.Play();
            WaitForPresentedFrames(pipeline, 2);

            SeekAndWait(pipeline, target);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            // The media takes the clock over where the file actually resumes, which is behind the request:
            // that arriving is what says the claim has been made and what must not be placed over again.
            WaitFor(() => pipeline.Position < target, "the media has to take the clock over where it resumes");

            Assert.That(Await(pipeline.Pause()).IsSuccessful, Is.True);
            WaitFor(() => !audioRenderer.ClockIsRunning, "a paused pipeline has to stop the clock");

            Assert.That(pipeline.Position, Is.LessThan(target), "a decision made after the claim leaves the clock where the media put it");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Looks for a position the file cannot resume at, well past a keyframe but before the next one, and
    /// hands back that position. Reading the packet after the seek is what says where the file went, since
    /// no demuxer promises to land exactly where it was pointed.
    /// </summary>
    private static TimeSpan FindASeekThatMisses(IMediaSource source)
    {
        var margin = TimeSpan.FromMilliseconds(300);
        for (var target = TimeSpan.FromMilliseconds(300); target <= TimeSpan.FromSeconds(2.6); target += TimeSpan.FromMilliseconds(20))
        {
            Assert.That(source.Seek(target).IsSuccessful, Is.True);
            Assert.That(source.TryReadPacket(out MediaPacket? packet).IsSuccessful, Is.True);
            Assert.That(packet, Is.Not.Null, "the fixture has to hold something at every position tried");

            using (packet)
            {
                if (packet is not null && target - packet.Timestamp >= margin)
                {
                    return target;
                }
            }
        }

        return TimeSpan.Zero;
    }

    [Test]
    public void KeepsPlayingVideoWhenAudioCannotBeOpened()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            FakeAudioRenderer audioRenderer = new() { CanConfigure = false, RefusalReason = "no device here" };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);
            WarningLog warnings = new(pipeline);

            PlaybackOperationResult opened = Open(pipeline, path, new FakeRenderTarget());

            using (Assert.EnterMultipleScope())
            {
                // A file whose audio will not play is still worth playing: the reason is reported, but
                // it is a warning rather than a failure.
                Assert.That(opened.IsSuccessful, Is.True, opened.Message);
                Assert.That(warnings.Latest, Does.Contain("no device here"));
                Assert.That(pipeline.State, Is.Not.EqualTo(MediaEngineState.Error));
                Assert.That(pipeline.HasAudio, Is.False);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void RefusesAnAudioFileWhenTheDeviceWillNotTakeIt()
    {
        string path = CreateWaveFile();
        try
        {
            FakeAudioRenderer audioRenderer = new() { CanConfigure = false, RefusalReason = "no device here" };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            PlaybackOperationResult opened = Open(pipeline, path);

            // There is nothing to fall back on: a file with no video and no sound is a file with nothing to
            // play, so the open fails with the reason rather than reporting success over an empty pipeline.
            using (Assert.EnterMultipleScope())
            {
                Assert.That(opened.IsSuccessful, Is.False);
                Assert.That(opened.Message, Does.Contain("no device here"));
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.None));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void RefusesVideoWithoutAPresentationSurface()
    {
        string path = CreateBitmapFile();
        try
        {
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), new FakeAudioRenderer());

            PlaybackOperationResult opened = Open(pipeline, path);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(opened.IsSuccessful, Is.False);
                Assert.That(opened.Message, Does.Contain("presentation surface"));

                // A file that will not open leaves nothing open rather than an error state: the caller that
                // asked is the one holding the reason.
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.None));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A playback that has failed stops reading. The reader has nothing to read *for* — nothing will consume
    /// what it produces, and the position it would fill is never played — so it parks in the same idle wait
    /// the picture parks in, instead of filling a read-ahead and polling the queues every 15 ms while the host
    /// decides what to do about the failure. What it holds (the file, the reason, the media already queued,
    /// and the counters describing where playback stopped) is exactly what is left to explain the failure,
    /// which is why nothing is released or discarded here: stopping or opening something else is what does
    /// that, and both are the host's decisions.
    /// <para>
    /// The sound's decoder is paced and counted, and both are what make this observable: paced, because the
    /// video decoder has to fail while the reader is still in the middle of its read-ahead (otherwise "still
    /// reading" and "parked" are the same number), and counted, because the reader decodes the sound inline —
    /// a call to it is the reader having read another packet.
    /// </para>
    /// </summary>
    [Test]
    public void StopsReadingOncePlaybackHasFailed()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            PacedAudioDecoderFactory audioDecoders = new();
            using PlaybackPipeline pipeline = CreatePipeline(
                new FakeVideoRenderer(),
                new FakeAudioRenderer(),
                videoDecoders: new FailingVideoDecoderFactory { PacketsBeforeFailure = 1 },
                audioDecoders: audioDecoders);

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            Assert.That(pipeline.HasAudio, Is.True, "the reader is paced by the sound it decodes inline");

            // Nothing is played: the reader reads and the sound is decoded while the open's state is still
            // paused, which is what paces the test — and a play is not asked for because the failure may
            // already have been heard by the time one could be, which would refuse it.
            WaitForState(pipeline, MediaEngineState.Error);
            int decodedWhenItFailed = audioDecoders.Decodes;

            // A reader that kept going would finish its read-ahead in here — the sound is slowed on purpose,
            // so that is many packets of work rather than one. What is allowed is the read that was already
            // in flight when the failure landed.
            Thread.Sleep(300);

            Assert.That(audioDecoders.Decodes - decodedWhenItFailed, Is.LessThanOrEqualTo(1),
                "a failed playback has nothing to read for: the reader parks instead of filling a read-ahead");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ReportsAFailureToOpen()
    {
        using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), new FakeAudioRenderer());

        PlaybackOperationResult opened = Open(pipeline, Path.Combine(Path.GetTempPath(), "sineplayer-missing-file.mp4"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(opened.IsSuccessful, Is.False);
            Assert.That(opened.Message, Is.Not.Null.And.Not.Empty);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.None));
            Assert.That(pipeline.Error, Is.Null);
        }
    }

    [Test]
    public void RefusesCommandsWhenNothingIsOpen()
    {
        using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), new FakeAudioRenderer());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.False);
            Assert.That(Await(pipeline.Pause()).IsSuccessful, Is.False);
            Assert.That(Await(pipeline.Seek(TimeSpan.FromSeconds(1))).IsSuccessful, Is.False);

            // A refused command is the caller's business and nobody else's: it says nothing about playback,
            // so nothing about playback changes.
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.None));

            // Stopping is the one request that is always satisfiable, because there is nothing it can fail at.
            Assert.That(Await(pipeline.Stop()).IsSuccessful, Is.True);
        }
    }

    [Test]
    public void ReportsAFailedPlayOnceTheFileHasEnded()
    {
        string path = CreateWaveFile();
        try
        {
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), new FakeAudioRenderer());
            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForPlaybackToFinish(pipeline);

            PlaybackOperationResult played = Await(pipeline.Play());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(played.IsSuccessful, Is.False);
                Assert.That(played.Message, Is.Not.Null.And.Not.Empty);

                // There is nothing left to play, which is not the same as playback having failed: the pipeline
                // stays where it is, and a seek from the end is still the way to play it again.
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended));
                Assert.That(pipeline.Error, Is.Null);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A source that refuses to be moved — saying so in advance, the way a live stream does, or by failing the
    /// seek itself. Neither is a failure of playback, and what each one leaves behind is what the two answers
    /// differ in: a refusal in advance is a command that could not be carried out, so nothing at all changes;
    /// a refusal when asked was accepted first, so the move is what paused playback, and the reader can only
    /// say so afterwards — a line, because the caller's answer has already been given.
    /// <para>
    /// Whether the source can be moved at all is fixed when it is opened, so it is known without the reader and
    /// comes back in the result; whether it <em>will</em> move this time is only known by trying, which the
    /// reader does on the thread that owns the cursor.
    /// </para>
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public void RefusesASeekTheSourceWillNotCarryOut(bool refusesInAdvance)
    {
        string path = CreateY4mFile();
        try
        {
            using PlaybackPipeline pipeline = CreatePipeline(
                new FakeVideoRenderer(),
                new FakeAudioRenderer(),
                videoDecoders: new SilentVideoDecoderFactory(),
                sourceFactory: new LiveRefusingSourceFactory(refusesInAdvance));
            WarningLog warnings = new(pipeline);

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForState(pipeline, MediaEngineState.Playing);

            PlaybackOperationResult seeked = Await(pipeline.Seek(TimeSpan.FromSeconds(5)));

            MediaEngineState expected = refusesInAdvance ? MediaEngineState.Playing : MediaEngineState.Paused;
            using (Assert.EnterMultipleScope())
            {
                // A source that refuses in advance is refused without the reader ever being involved.
                Assert.That(seeked.IsSuccessful, Is.EqualTo(!refusesInAdvance), seeked.Message);

                // A source that will not be moved is not playback failing, either way:
                Assert.That(pipeline.State, Is.EqualTo(expected),
                    refusesInAdvance
                        ? "a command that could not be carried out changes nothing"
                        : "the move is what paused playback; the source's refusal only means the position did not change");
                Assert.That(pipeline.Error, Is.Null);
            }

            if (!refusesInAdvance)
            {
                // The reader found out by trying, and a line is what it has to say it with.
                WaitForWarning(warnings, "refused to seek");
            }

            // A moment later the state is the one the case implies, rather than half way through a seek that
            // never happened — and the second case is the one the host can put back on the road with a play.
            Thread.Sleep(100);
            Assert.That(pipeline.State, Is.EqualTo(expected), pipeline.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A decoder that throws is the same failure as one that returns a failure, and it has to arrive the same
    /// way — the catch on the decode thread is the only thing between an interop fault and a thread that dies
    /// in silence, and it was once emptied by an edit without any test noticing. Playback stays `Playing` with
    /// a picture that never moves when that happens, so this test is what says the line is still there.
    /// </summary>
    [Test]
    public void ReportsADecoderThatThrowsAsPlaybackFailing()
    {
        string path = CreateY4mFile();
        try
        {
            using PlaybackPipeline pipeline = CreatePipeline(
                new FakeVideoRenderer(),
                new FakeAudioRenderer(),
                videoDecoders: new ThrowingVideoDecoderFactory { PacketsBeforeThrow = 1 });

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);

            // The decoder fails on its own, without anything being played: the reader reads and the decode
            // thread decodes while the open's state is still paused, so there is nothing for a play to add —
            // and asking for one would be asking for a command the failure may already have refused. That the
            // reader still does its work while paused is exactly what this test leans on.
            WaitForState(pipeline, MediaEngineState.Error);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.Error, Is.EqualTo("The decoder failed on this stream."),
                    "the exception's message is what the failure says, which is what pins the catch");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A file whose decoder holds its frames back until it is drained — which is what a codec with output
    /// delay does, and why an end of stream has to produce a tail at all — runs to its end and shows the frame
    /// that only the drain could release.
    /// <para>
    /// The drain is asked for when the packet stream ends and not when a poll happens to find the queue empty:
    /// the end is an entry the decoder is handed, after the last packet of it. The test is here because that
    /// is the one thing a pipeline test can see of the difference — with the entry, "nothing more is coming"
    /// and "there is nothing right now" are different facts, and only the first one drains a decoder.
    /// </para>
    /// </summary>
    [Test]
    public void ProducesTheTailOfAFileWhenItsPacketStreamEnds()
    {
        string path = CreateBitmapFile();
        try
        {
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(
                videoRenderer,
                new FakeAudioRenderer { CanConfigure = false },
                videoDecoders: new DelayedVideoDecoderFactory());

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            pipeline.Play();
            WaitForPlaybackToFinish(pipeline);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);
                Assert.That(videoRenderer.RenderedFrames, Is.GreaterThan(0),
                    "the frame the decoder held until the stream ended has to be shown");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ReportsADecoderThatDiesAsPlaybackFailing()
    {
        string path = CreateY4mFile();
        try
        {
            using PlaybackPipeline pipeline = CreatePipeline(
                new FakeVideoRenderer(),
                new FakeAudioRenderer(),
                videoDecoders: new FailingVideoDecoderFactory { PacketsBeforeFailure = 1 });

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);

            // Whether this play is accepted is a coin toss (see below), so what is asserted about it is nothing:
            // the test is about the failure being reported, not about the command that raced it.
            Await(pipeline.Play());

            // The failure is reported by the decoding thread and the end of the file by the reader, and the two
            // are items of the same queue in whatever order they were posted: the file can be read out before
            // the decoder's report is heard, so the state to wait for is the failure and not "playback stopped".
            // Waiting for the first of the two to arrive is waiting for a coin toss.
            WaitForState(pipeline, MediaEngineState.Error);

            // Nothing about this was asked for and nothing can go on: a decoder that dies is playback failing,
            // which is what the error state is for. And the play above is a third loser of the same toss: the
            // decoder is fed while the open's state is still paused — the decode thread decodes ahead without
            // asking whether anything is playing — so the failure can be heard first, and then the play is
            // refused *because* playback has already failed.
            Assert.That(pipeline.Error, Is.Not.Null.And.Not.Empty);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static PlaybackPipeline CreatePipeline(
        FakeVideoRenderer videoRenderer,
        FakeAudioRenderer audioRenderer,
        IVideoDecoderFactory? videoDecoders = null,
        ISourceProviderFactory? sourceFactory = null,
        IAudioDecoderFactory? audioDecoders = null)
    {
        return new PlaybackPipeline(
            sourceFactory ?? new FFmpegSourceProviderFactory(),
            videoDecoders ?? new FFmpegVideoDecoderFactory(),
            new FakeVideoRendererFactory(videoRenderer),
            audioDecoders ?? new FFmpegAudioDecoderFactory(),
            new FixedAudioRendererFactory(audioRenderer),
            new DefaultSynchronizer());
    }

    /// <summary>
    /// A pipeline whose sound devices are made by the factory under test, with a stand-in picture so the file
    /// only has to contain audio.
    /// </summary>
    private static PlaybackPipeline CreatePipeline(IComponentFactory<IAudioRenderer> audioRendererFactory)
    {
        return new PlaybackPipeline(
            new FFmpegSourceProviderFactory(),
            new FFmpegVideoDecoderFactory(),
            new FakeVideoRendererFactory(new FakeVideoRenderer()),
            new FFmpegAudioDecoderFactory(),
            audioRendererFactory,
            new DefaultSynchronizer());
    }

    /// <summary>
    /// Hands out the one renderer the test is going to inspect. The pipeline creates its own renderer per
    /// open and disposes it on close, so this is only sound because the stand-in's dispose does nothing and
    /// it can be configured again — which is exactly what the tests need to read its counters afterwards.
    /// </summary>
    private sealed class FixedAudioRendererFactory : IComponentFactory<IAudioRenderer>
    {
        private readonly IAudioRenderer _renderer;

        internal FixedAudioRendererFactory(IAudioRenderer renderer)
        {
            _renderer = renderer;
        }

        public ComponentCapabilities Capabilities { get; } = new()
        {
            Name = "fake",
            Description = "The renderer the test reads.",
        };

        public PlaybackOperationResult TryCreate(out IAudioRenderer? component)
        {
            component = _renderer;
            return PlaybackOperationResult.Success;
        }
    }

    /// <summary>
    /// Two players at once: two pipelines, two devices, two clocks. Nothing in the engine is shared between them —
    /// this is the test that would fail if a static (a dispatcher, a graphics device, a queue) came back, which is
    /// exactly the kind of sharing that is invisible until two files are opened.
    /// </summary>
    [Test]
    public void PlaysTwoFilesAtOnceWithoutEitherTouchingTheOther()
    {
        string first = CreateWaveFile();
        string second = CreateWaveFile();
        try
        {
            FakeAudioRenderer firstAudio = new();
            FakeAudioRenderer secondAudio = new();
            using PlaybackPipeline firstPipeline = CreatePipeline(new FakeVideoRenderer(), firstAudio);
            using PlaybackPipeline secondPipeline = CreatePipeline(new FakeVideoRenderer(), secondAudio);

            Assert.That(Open(firstPipeline, first).IsSuccessful, Is.True);
            Assert.That(Open(secondPipeline, second).IsSuccessful, Is.True);
            Assert.That(Await(firstPipeline.Play()).IsSuccessful, Is.True);
            Assert.That(Await(secondPipeline.Play()).IsSuccessful, Is.True);

            // Both are started before either is waited on, so they run at the same time; what is asserted is the
            // outcome for each rather than a transient state, because a device that accepts everything can let a
            // short file finish before the other command has even been answered.
            WaitForPlaybackToFinish(firstPipeline);
            WaitForPlaybackToFinish(secondPipeline);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(firstPipeline.State, Is.EqualTo(MediaEngineState.Ended), firstPipeline.Error);
                Assert.That(secondPipeline.State, Is.EqualTo(MediaEngineState.Ended), secondPipeline.Error);
                Assert.That(firstAudio.SubmittedSamples, Is.EqualTo(SampleCount), "each player feeds its own device, with all of its own sound");
                Assert.That(secondAudio.SubmittedSamples, Is.EqualTo(SampleCount));
                Assert.That(firstAudio.SubmittedBuffers, Is.GreaterThan(0));
                Assert.That(secondAudio.SubmittedBuffers, Is.GreaterThan(0));
            }
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    /// <summary>
    /// A renderer that cannot hand a block to its device fails the hand-off, and the pipeline is where that
    /// becomes an error — not where the thread dies. The message the state carries is the one the failing layer
    /// produced, context included: the pipeline adds a state, not a diagnosis. (The exception itself is pinned
    /// to be preserved by <c>WasapiAudioClockTests</c>, which is where it is caught.)
    /// </summary>
    [Test]
    public void ReportsARenderersFailureAsPlaybackFailing()
    {
        string path = CreateWaveFile();
        try
        {
            InvalidOperationException failure = new("The device cannot be written to.");
            FakeAudioRenderer audioRenderer = new() { SubmitFailure = failure };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitForPlaybackToFinish(pipeline);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Error));
                Assert.That(pipeline.Error, Is.EqualTo($"Unable to queue audio: {failure.Message}"),
                    "the message the user sees is the one the failing layer produced, with its context");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The sound's device belongs to one pipeline for one open. A renderer that outlived the open would carry
    /// its clock, its buffer and its flush into the next file — and a renderer shared between two pipelines
    /// would give them one clock, so the second file would play at the first one's position.
    /// </summary>
    [Test]
    public void CreatesItsOwnAudioRendererForEachOpen()
    {
        string path = CreateWaveFile();
        try
        {
            CreatingAudioRendererFactory factory = new();

            using (PlaybackPipeline first = CreatePipeline(factory))
            {
                Assert.That(Open(first, path).IsSuccessful, Is.True);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(factory.Created, Has.Count.EqualTo(1), "opening a file asks the factory for a device");
                    Assert.That(factory.Created[0].DisposeCount, Is.Zero, "the device is in use, so it is not let go of");
                }
            }

            FakeAudioRenderer letGo = factory.Created[0];
            Assert.That(letGo.DisposeCount, Is.EqualTo(1), "closing lets go of the device it was playing on");

            // A second pipeline from the same factory gets a device of its own rather than the one the first
            // pipeline disposed: that is what makes two pipelines two streams the audio engine mixes.
            using PlaybackPipeline second = CreatePipeline(factory);
            Assert.That(Open(second, path).IsSuccessful, Is.True);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(factory.Created, Has.Count.EqualTo(2), "a second pipeline gets a second device");
                Assert.That(factory.Created[1], Is.Not.SameAs(letGo));
                Assert.That(factory.Created[1].SubmittedSamples, Is.Zero);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The host may set the volume before there is a device to set it on. The value is the pipeline's, so it
    /// survives the renderer being created per open and reaches the device the next open makes.
    /// </summary>
    [Test]
    public void AppliesTheVolumeSetBeforeAnythingIsOpen()
    {
        string path = CreateWaveFile();
        try
        {
            CreatingAudioRendererFactory factory = new();
            using PlaybackPipeline pipeline = CreatePipeline(factory);

            pipeline.Volume = 0.25;
            pipeline.IsMuted = true;

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(factory.Created, Has.Count.EqualTo(1));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(factory.Created[0].Volume, Is.EqualTo(0.25), "what the host asked for is what the device gets");
                Assert.That(factory.Created[0].IsMuted, Is.True);

                // And after the device exists, the same property reaches it.
                pipeline.Volume = 0.5;
                Assert.That(factory.Created[0].Volume, Is.EqualTo(0.5));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Hands out a fresh stand-in device per call, which is what the real factory does, and keeps every one it
    /// made so a test can see both how many opens asked for a device and what became of each.
    /// </summary>
    private sealed class CreatingAudioRendererFactory : IComponentFactory<IAudioRenderer>
    {
        private readonly object _lock = new();

        public List<FakeAudioRenderer> Created { get; } = [];

        public ComponentCapabilities Capabilities { get; } = new()
        {
            Name = "fake-audio",
            Description = "A new stand-in device per creation.",
        };

        public PlaybackOperationResult TryCreate(out IAudioRenderer? component)
        {
            FakeAudioRenderer renderer = new();
            lock (_lock)
            {
                Created.Add(renderer);
            }

            component = renderer;
            return PlaybackOperationResult.Success;
        }
    }

    /// <summary>
    /// The real decoder wrapped so that it keeps everything it decodes until it is drained, which is how a
    /// codec with output delay behaves: the frames of the last packets are only released when the decoder is
    /// told the stream has ended. It is also the strictest form of the question the pipeline answers at the end
    /// of a file — the tail is not "the frames that came out of the last packet", it is "whatever the decoder
    /// is still holding", and nothing but the drain can produce it.
    /// </summary>
    private sealed class DelayedVideoDecoderFactory : IVideoDecoderFactory
    {
        private readonly FFmpegVideoDecoderFactory _inner = new();

        public ComponentCapabilities Capabilities => _inner.Capabilities;

        public PlaybackOperationResult TryCreate(IMediaSource source, VideoStreamInfo stream, DecoderOptions options, out IVideoDecoder? decoder)
        {
            PlaybackOperationResult created = _inner.TryCreate(source, stream, options, out IVideoDecoder? inner);
            if (!created.IsSuccessful || inner is null)
            {
                decoder = null;
                return created;
            }

            decoder = new DelayedVideoDecoder(inner);
            return PlaybackOperationResult.Success;
        }

        private sealed class DelayedVideoDecoder : IVideoDecoder
        {
            private readonly IVideoDecoder _inner;
            private readonly List<RawVideoFrame> _held = [];

            internal DelayedVideoDecoder(IVideoDecoder inner)
            {
                _inner = inner;
            }

            public MediaStreamInfo Stream => _inner.Stream;

            public bool IsHardwareAccelerated => _inner.IsHardwareAccelerated;

            public PixelFormat OutputFormat => _inner.OutputFormat;

            public PlaybackOperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
            {
                PlaybackOperationResult decoded = _inner.TryDecode(packet, out IReadOnlyList<RawVideoFrame> produced);
                if (decoded.IsSuccessful)
                {
                    _held.AddRange(produced);
                }

                frames = [];
                return decoded;
            }

            public PlaybackOperationResult Drain(out IReadOnlyList<RawVideoFrame> frames)
            {
                PlaybackOperationResult drained = _inner.Drain(out IReadOnlyList<RawVideoFrame> tail);
                if (!drained.IsSuccessful)
                {
                    frames = tail;
                    return drained;
                }

                _held.AddRange(tail);
                frames = _held.ToArray();
                _held.Clear();
                return PlaybackOperationResult.Success;
            }

            public PlaybackOperationResult Flush()
            {
                ReleaseHeld();
                return _inner.Flush();
            }

            public void Dispose()
            {
                ReleaseHeld();
                _inner.Dispose();
            }

            private void ReleaseHeld()
            {
                foreach (RawVideoFrame frame in _held)
                {
                    frame.Dispose();
                }

                _held.Clear();
            }
        }
    }

    /// <summary>
    /// The real packets of a file, wrapped so that the stream never ends and never moves: a source that
    /// refuses to be seeked is a live one, and a live one has no end to reach. No file on disk refuses, so
    /// this is the only way to reach either refusal path with a real file behind it.
    /// </summary>
    private sealed class LiveRefusingSourceFactory(bool refusesInAdvance) : ISourceProviderFactory
    {
        private readonly FFmpegSourceProviderFactory _inner = new();

        public ComponentCapabilities Capabilities => _inner.Capabilities;

        public PlaybackOperationResult TryOpen(MediaLocator locator, out IMediaSource? source)
        {
            PlaybackOperationResult opened = _inner.TryOpen(locator, out IMediaSource? inner);
            source = opened.IsSuccessful && inner is not null ? new LiveRefusingSource(inner, refusesInAdvance) : inner;
            return opened;
        }

        private sealed class LiveRefusingSource(IMediaSource inner, bool refusesInAdvance) : IMediaSource
        {
            public MediaInfo Info => inner.Info;

            public bool CanSeek => !refusesInAdvance;

            public PlaybackOperationResult Seek(TimeSpan position)
            {
                return PlaybackOperationResult.Failure("This source refuses to move.");
            }

            public PlaybackOperationResult TryReadPacket(out MediaPacket? packet)
            {
                PlaybackOperationResult read = inner.TryReadPacket(out packet);
                if (read.IsSuccessful && packet is null)
                {
                    // The stream loops rather than ends, which is what a live one does.
                    inner.Seek(TimeSpan.Zero);
                    read = inner.TryReadPacket(out packet);
                }

                // Paced a little, so a stream without an end does not spin the reader as fast as it can read.
                Thread.Sleep(1);
                return read;
            }

            public void Dispose()
            {
                inner.Dispose();
            }
        }
    }

    /// <summary>
    /// The real audio decoder, slowed down and counted. Slowed so that a failure elsewhere lands while the
    /// reader is still in the middle of reading the file — the reader decodes the sound inline, so this is
    /// what paces it — and counted because a call to this decoder is the reader having read another audio
    /// packet, which is the only way a test can see that a failed playback stopped reading.
    /// </summary>
    private sealed class PacedAudioDecoderFactory : IAudioDecoderFactory
    {
        /// <summary>How long one decode takes: the default keeps the reader just slow enough that a failure lands inside its read-ahead.</summary>
        private const int DecodePaceMilliseconds = 10;

        private readonly FFmpegAudioDecoderFactory _inner = new();
        private int _decodes;

        /// <summary>How long one decode takes, when a test needs the pipe to be slower than the device.</summary>
        internal int DecodeMilliseconds { get; init; } = DecodePaceMilliseconds;

        public int Decodes => Volatile.Read(ref _decodes);

        public ComponentCapabilities Capabilities => _inner.Capabilities;

        public PlaybackOperationResult TryCreate(IMediaSource source, AudioStreamInfo stream, DecoderOptions options, out IAudioDecoder? decoder)
        {
            PlaybackOperationResult created = _inner.TryCreate(source, stream, options, out IAudioDecoder? inner);
            if (!created.IsSuccessful || inner is null)
            {
                decoder = null;
                return created;
            }

            decoder = new PacedAudioDecoder(inner, this, DecodeMilliseconds);
            return PlaybackOperationResult.Success;
        }

        private sealed class PacedAudioDecoder(IAudioDecoder inner, PacedAudioDecoderFactory owner, int decodeMilliseconds) : IAudioDecoder
        {
            public MediaStreamInfo Stream => inner.Stream;

            public bool IsHardwareAccelerated => inner.IsHardwareAccelerated;

            public AudioSampleFormat OutputFormat => inner.OutputFormat;

            public PlaybackOperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawAudioBuffer> buffers)
            {
                Interlocked.Increment(ref owner._decodes);
                Thread.Sleep(decodeMilliseconds);
                return inner.TryDecode(packet, out buffers);
            }

            public PlaybackOperationResult Drain(out IReadOnlyList<RawAudioBuffer> buffers)
            {
                return inner.Drain(out buffers);
            }

            public PlaybackOperationResult Flush()
            {
                return inner.Flush();
            }

            public void Dispose()
            {
                inner.Dispose();
            }
        }
    }

    /// <summary>
    /// The real decoder wrapped so that it throws instead of returning a failure, which is how a decoder
    /// reaches it when the fault comes out of interop rather than out of a return code. The pipeline catches
    /// on the decode thread, and this is what keeps that catch from being emptied: without it, a throwing
    /// decoder kills the thread silently — playback stays `Playing` with a picture that never moves and
    /// nothing anywhere says why, which is the worst failure this design can have.
    /// </summary>
    private sealed class ThrowingVideoDecoderFactory : IVideoDecoderFactory
    {
        private readonly FFmpegVideoDecoderFactory _inner = new();

        /// <summary>How many packets decode cleanly before the decoder throws.</summary>
        internal int PacketsBeforeThrow { get; init; } = 1;

        public ComponentCapabilities Capabilities => _inner.Capabilities;

        public PlaybackOperationResult TryCreate(IMediaSource source, VideoStreamInfo stream, DecoderOptions options, out IVideoDecoder? decoder)
        {
            PlaybackOperationResult created = _inner.TryCreate(source, stream, options, out IVideoDecoder? inner);
            if (!created.IsSuccessful || inner is null)
            {
                decoder = null;
                return created;
            }

            decoder = new ThrowingVideoDecoder(inner, PacketsBeforeThrow);
            return PlaybackOperationResult.Success;
        }

        private sealed class ThrowingVideoDecoder : IVideoDecoder
        {
            private readonly IVideoDecoder _inner;
            private readonly int _packetsBeforeThrow;
            private int _decoded;

            internal ThrowingVideoDecoder(IVideoDecoder inner, int packetsBeforeThrow)
            {
                _inner = inner;
                _packetsBeforeThrow = packetsBeforeThrow;
            }

            public MediaStreamInfo Stream => _inner.Stream;

            public bool IsHardwareAccelerated => _inner.IsHardwareAccelerated;

            public PixelFormat OutputFormat => _inner.OutputFormat;

            public PlaybackOperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
            {
                if (_decoded++ >= _packetsBeforeThrow)
                {
                    throw new InvalidOperationException("The decoder failed on this stream.");
                }

                return _inner.TryDecode(packet, out frames);
            }

            public PlaybackOperationResult Drain(out IReadOnlyList<RawVideoFrame> frames)
            {
                return _inner.Drain(out frames);
            }

            public PlaybackOperationResult Flush()
            {
                return _inner.Flush();
            }

            public void Dispose()
            {
                _inner.Dispose();
            }
        }
    }

    /// <summary>
    /// A video decoder that never produces a frame. It exists so a source other than the engine's own can be
    /// played at all — the real decoder insists on an FFmpeg source — which is what makes the refusal paths
    /// above reachable with a real file behind them.
    /// </summary>
    private sealed class SilentVideoDecoderFactory : IVideoDecoderFactory
    {
        public ComponentCapabilities Capabilities { get; } = new()
        {
            Name = "silent-video",
            Description = "A decoder that never produces a frame.",
        };

        public PlaybackOperationResult TryCreate(IMediaSource source, VideoStreamInfo stream, DecoderOptions options, out IVideoDecoder? decoder)
        {
            decoder = new SilentVideoDecoder(stream);
            return PlaybackOperationResult.Success;
        }

        private sealed class SilentVideoDecoder(VideoStreamInfo stream) : IVideoDecoder
        {
            public MediaStreamInfo Stream => stream;

            public bool IsHardwareAccelerated => false;

            public PixelFormat OutputFormat => PixelFormat.Yuv420P;

            public PlaybackOperationResult Flush()
            {
                return PlaybackOperationResult.Success;
            }

            public PlaybackOperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
            {
                frames = [];
                return PlaybackOperationResult.Success;
            }

            public PlaybackOperationResult Drain(out IReadOnlyList<RawVideoFrame> frames)
            {
                frames = [];
                return PlaybackOperationResult.Success;
            }

            public void Dispose()
            {
            }
        }
    }

    /// <summary>
    /// The real decoder wrapped so that it gives up after a few packets, which is how a codec that comes
    /// across something it cannot handle behaves. It pins the other half of the rule the refusal tests pin: a
    /// command that cannot be carried out is the caller's business, but a decoder that dies is playback's.
    /// </summary>
    private sealed class FailingVideoDecoderFactory : IVideoDecoderFactory
    {
        private readonly FFmpegVideoDecoderFactory _inner = new();

        /// <summary>How many packets decode cleanly before the decoder gives up.</summary>
        internal int PacketsBeforeFailure { get; init; } = 1;

        public ComponentCapabilities Capabilities => _inner.Capabilities;

        public PlaybackOperationResult TryCreate(IMediaSource source, VideoStreamInfo stream, DecoderOptions options, out IVideoDecoder? decoder)
        {
            PlaybackOperationResult created = _inner.TryCreate(source, stream, options, out IVideoDecoder? inner);
            if (!created.IsSuccessful || inner is null)
            {
                decoder = null;
                return created;
            }

            decoder = new FailingVideoDecoder(inner, PacketsBeforeFailure);
            return PlaybackOperationResult.Success;
        }

        private sealed class FailingVideoDecoder : IVideoDecoder
        {
            private readonly IVideoDecoder _inner;
            private readonly int _packetsBeforeFailure;
            private int _decoded;

            internal FailingVideoDecoder(IVideoDecoder inner, int packetsBeforeFailure)
            {
                _inner = inner;
                _packetsBeforeFailure = packetsBeforeFailure;
            }

            public MediaStreamInfo Stream => _inner.Stream;

            public bool IsHardwareAccelerated => _inner.IsHardwareAccelerated;

            public PixelFormat OutputFormat => _inner.OutputFormat;

            public PlaybackOperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
            {
                if (_decoded++ >= _packetsBeforeFailure)
                {
                    frames = [];
                    return PlaybackOperationResult.Failure("The decoder cannot decode this stream.");
                }

                return _inner.TryDecode(packet, out frames);
            }

            public PlaybackOperationResult Drain(out IReadOnlyList<RawVideoFrame> frames)
            {
                return _inner.Drain(out frames);
            }

            public PlaybackOperationResult Flush()
            {
                return _inner.Flush();
            }

            public void Dispose()
            {
                _inner.Dispose();
            }
        }
    }

    /// <summary>
    /// The real decoder with a delay added to every packet it is given, which is the one thing a decoder that
    /// keeps up never does and a decoder that cannot the only thing it does.
    /// </summary>
    private sealed class ThrottledVideoDecoderFactory : IVideoDecoderFactory
    {
        private readonly FFmpegVideoDecoderFactory _inner = new();

        /// <summary>How long one packet takes to decode, which is what makes the decoder too slow.</summary>
        internal int DecodeMilliseconds { get; init; }

        /// <summary>
        /// How many packets are decoded without the delay, before it starts. A decoder that is slow from the
        /// very first packet leaves the picture behind the clock before there is anything to judge; one that
        /// decodes a frame and then goes quiet for seconds is what a picture *starting* to fall behind looks
        /// like, and it is the arrangement in which the picture's verdict has nothing to wake it.
        /// </summary>
        internal int FastPackets { get; init; }

        public ComponentCapabilities Capabilities => _inner.Capabilities;

        public PlaybackOperationResult TryCreate(IMediaSource source, VideoStreamInfo stream, DecoderOptions options, out IVideoDecoder? decoder)
        {
            PlaybackOperationResult created = _inner.TryCreate(source, stream, options, out IVideoDecoder? inner);
            if (!created.IsSuccessful || inner is null)
            {
                decoder = null;
                return created;
            }

            decoder = new ThrottledVideoDecoder(inner, DecodeMilliseconds, FastPackets);
            return PlaybackOperationResult.Success;
        }

        private sealed class ThrottledVideoDecoder : IVideoDecoder
        {
            private readonly IVideoDecoder _inner;
            private readonly int _decodeMilliseconds;
            private readonly int _fastPackets;
            private int _decoded;

            internal ThrottledVideoDecoder(IVideoDecoder inner, int decodeMilliseconds, int fastPackets)
            {
                _inner = inner;
                _decodeMilliseconds = decodeMilliseconds;
                _fastPackets = fastPackets;
            }

            public MediaStreamInfo Stream => _inner.Stream;

            public bool IsHardwareAccelerated => _inner.IsHardwareAccelerated;

            public PixelFormat OutputFormat => _inner.OutputFormat;

            public PlaybackOperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
            {
                if (Interlocked.Increment(ref _decoded) > _fastPackets)
                {
                    Thread.Sleep(_decodeMilliseconds);
                }

                return _inner.TryDecode(packet, out frames);
            }

            public PlaybackOperationResult Drain(out IReadOnlyList<RawVideoFrame> frames)
            {
                return _inner.Drain(out frames);
            }

            public PlaybackOperationResult Flush()
            {
                return _inner.Flush();
            }

            public void Dispose()
            {
                _inner.Dispose();
            }
        }
    }

    /// <summary>
    /// Hands the same stand-in renderer to the pipeline. The pipeline creates its renderer itself, on
    /// the thread that will use it, so that the graphics device belongs to that apartment.
    /// </summary>
    private sealed class FakeVideoRendererFactory : IComponentFactory<IVideoRenderer>
    {
        private readonly IVideoRenderer _renderer;

        internal FakeVideoRendererFactory(IVideoRenderer renderer)
        {
            _renderer = renderer;
        }

        public ComponentCapabilities Capabilities { get; } = new()
        {
            Name = "fake-video",
            Description = "Stand-in video renderer.",
        };

        public PlaybackOperationResult TryCreate(out IVideoRenderer? component)
        {
            component = _renderer;
            return PlaybackOperationResult.Success;
        }
    }

    /// <summary>
    /// Opens a file and waits for the answer, because whoever opens a file has to know whether it opened.
    /// </summary>
    private static PlaybackOperationResult Open(PlaybackPipeline pipeline, string path, IVideoRenderTarget? target = null)
    {
        return pipeline.Open(MediaLocator.Parse(path), target).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Waits for a command's own answer, which every call into the pipeline hands back: the job is queued, so
    /// waiting for the task is waiting for the command to have happened.
    /// </summary>
    private static PlaybackOperationResult Await(Task<PlaybackOperationResult> command)
    {
        Assert.That(command.Wait(5000), Is.True, "the command was never carried out");
        return command.Result;
    }

    /// <summary>
    /// Waits for something about playback to become true. A call is posted, so what a test is usually about is
    /// what a call led to — a frame shown, the clock moving, a state reached before anything else changes it.
    /// </summary>
    private static void WaitFor(Func<bool> condition, string what)
    {
        WaitFor(condition, what, 2000);
    }

    /// <summary>
    /// Waits for a condition with a budget of its own, for a test whose subject is *when* something happened:
    /// what the ordinary budget would let pass is exactly what such a test is about.
    /// </summary>
    private static void WaitFor(Func<bool> condition, string what, int milliseconds)
    {
        for (int attempt = 0; attempt < milliseconds / 10; attempt++)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.Fail(what);
    }

    private static void WaitForState(PlaybackPipeline pipeline, MediaEngineState state)
    {
        WaitFor(() => pipeline.State == state, $"The pipeline is {pipeline.State} where {state} was expected. {pipeline.Error}");
    }

    private static void WaitForPlaybackToFinish(PlaybackPipeline pipeline)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            MediaEngineState state = pipeline.State;
            if (state is MediaEngineState.Ended or MediaEngineState.Error)
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.Fail($"Playback did not finish; the pipeline is {pipeline.State}.");
    }

    private static void WaitForPresentedFrames(PlaybackPipeline pipeline, long count)
    {
        // Five seconds rather than two: the video fixtures run at a frame every two seconds, so a test asking for
        // the second frame needs more than the frame interval and nothing else.
        for (int attempt = 0; attempt < 500; attempt++)
        {
            if (pipeline.PresentedFrameCount >= count)
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.Fail($"The pipeline showed {pipeline.PresentedFrameCount} frame(s) where {count} were expected; it is {pipeline.State} at {pipeline.Position:mm\\:ss\\.fff} with the picture at {pipeline.LastPresentedTimestamp:mm\\:ss\\.fff}, starved for {pipeline.StarvedFor ?? "nothing"}, {pipeline.DroppedFrameCount} dropped and a lag of {pipeline.PictureLag.TotalMilliseconds:0} ms. {pipeline.Error}");
    }

    /// <summary>
    /// Seeks and waits for the answer: what a test is about — where the clock ended up, whether playback went
    /// on — is only settled once the queue item has run, and a seek the source refuses says so in the result.
    /// </summary>
    private static void SeekAndWait(PlaybackPipeline pipeline, TimeSpan position)
    {
        PlaybackOperationResult seeked = Await(pipeline.Seek(position));
        Assert.That(seeked.IsSuccessful, Is.True, seeked.Message);
    }

    /// <summary>
    /// The same seek from a thread of its own: a caller is free to be any thread, and the task is what says
    /// when the seek happened, wherever it was asked for.
    /// </summary>
    private static void SeekFromAnotherThread(PlaybackPipeline pipeline, TimeSpan position)
    {
        Task<PlaybackOperationResult>? seek = null;
        var seeker = new Thread(() => seek = pipeline.Seek(position))
        {
            IsBackground = true,
        };

        seeker.Start();
        Assert.That(seeker.Join(10000), Is.True, "the seek was never asked for");

        if (seek is null)
        {
            Assert.Fail("The seek was never carried out.");
            return;
        }

        Assert.That(seek.Wait(10000), Is.True, "the seek was never carried out");
        Assert.That(seek.Result.IsSuccessful, Is.True, seek.Result.Message);
    }

    private static byte[] CreateWaveSamples(int sampleCount)
    {
        byte[] samples = new byte[sampleCount * ChannelCount * sizeof(short)];
        for (int index = 0; index < samples.Length; index++)
        {
            samples[index] = (byte)((index / 4) % 32);
        }

        return samples;
    }

    private static string CreateWaveFile(int sampleCount = SampleCount)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sineplayer-pipeline-{Guid.NewGuid():N}.wav");
        byte[] samples = CreateWaveSamples(sampleCount);

        using (BinaryWriter writer = new(File.Create(path)))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + samples.Length);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)ChannelCount);
            writer.Write(SampleRate);
            writer.Write(SampleRate * ChannelCount * sizeof(short));
            writer.Write((short)(ChannelCount * sizeof(short)));
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(samples.Length);
            writer.Write(samples);
        }

        return path;
    }

    /// <summary>
    /// Writes a video file by hand as YUV4MPEG, which FFmpeg reads as raw frames. Half a frame per
    /// second is what makes it worth having: frame zero is due at once and the next one is two seconds
    /// away, long enough for the thread that shows frames to be certainly still waiting for it.
    /// </summary>
    private static string CreateY4mFile(int frameCount = 20, string frameRate = "1:2")
    {
        string path = Path.Combine(Path.GetTempPath(), $"sineplayer-pipeline-{Guid.NewGuid():N}.y4m");
        byte[] luma = new byte[Width * Height];
        byte[] chroma = new byte[Width / 2 * (Height / 2)];

        using (FileStream stream = File.Create(path))
        {
            WriteText(stream, $"YUV4MPEG2 W{Width} H{Height} F{frameRate} Ip A1:1 C420\n");
            for (int index = 0; index < frameCount; index++)
            {
                WriteText(stream, "FRAME\n");
                stream.Write(luma);
                stream.Write(chroma);
                stream.Write(chroma);
            }
        }

        return path;
    }

    private static void WriteText(Stream stream, string text)
    {
        stream.Write(Encoding.ASCII.GetBytes(text));
    }

    private static string CreateBitmapFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sineplayer-pipeline-{Guid.NewGuid():N}.bmp");
        int pixelBytes = Width * Height * sizeof(int);
        int dataOffset = 14 + 40;

        using (BinaryWriter writer = new(File.Create(path)))
        {
            writer.Write("BM"u8);
            writer.Write(dataOffset + pixelBytes);
            writer.Write(0);
            writer.Write(dataOffset);

            writer.Write(40);
            writer.Write(Width);
            writer.Write(Height);
            writer.Write((short)1);
            writer.Write((short)32);
            writer.Write(0);
            writer.Write(pixelBytes);
            writer.Write(2835);
            writer.Write(2835);
            writer.Write(0);
            writer.Write(0);

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    writer.Write((byte)(x * 8));
                    writer.Write((byte)(y * 10));
                    writer.Write((byte)200);
                    writer.Write((byte)255);
                }
            }
        }

        return path;
    }

    /// <summary>
    /// Waits for the pipeline to say something about something, since when it says it is the pipeline's
    /// decision — a stall is named once it has lasted, not the moment it starts.
    /// </summary>
    private static void WaitForWarning(WarningLog warnings, string text)
    {
        var waitedFor = Stopwatch.StartNew();
        while (waitedFor.ElapsedMilliseconds < 5000)
        {
            foreach (string message in warnings.Messages)
            {
                if (message.Contains(text, StringComparison.Ordinal))
                {
                    return;
                }
            }

            Thread.Sleep(10);
        }

        Assert.Fail($"The pipeline never said anything about \"{text}\"; it said: {string.Join(" | ", warnings.Messages)}");
    }

    /// <summary>
    /// Records what the pipeline says about playback without stopping it, in the order it said it. A warning
    /// is a line rather than a state, so it is heard as it happens and what this keeps is the sequence.
    /// </summary>
    private sealed class WarningLog
    {
        private readonly object _lock = new();
        private readonly List<string> _messages = [];

        internal WarningLog(PlaybackPipeline pipeline)
        {
            pipeline.WarningRaised += Record;
        }

        internal IReadOnlyList<string> Messages
        {
            get
            {
                lock (_lock)
                {
                    return [.. _messages];
                }
            }
        }

        internal string? Latest
        {
            get
            {
                lock (_lock)
                {
                    return _messages.Count == 0 ? null : _messages[^1];
                }
            }
        }

        private void Record(string warning)
        {
            lock (_lock)
            {
                _messages.Add(warning);
            }
        }
    }

    private sealed class FakeRenderTarget : IVideoRenderTarget
    {
        public int SurfaceWidth => 320;

        public int SurfaceHeight => 240;

        public double RasterizationScale => 1.0;

        public event Action? SurfaceSizeChanged;

        public void AttachPresentationSurface(nint swapChain)
        {
            SurfaceSizeChanged?.Invoke();
        }

        /// <summary>Stands in for the host resizing its surface or moving the window to another display.</summary>
        public void RaiseSizeChanged()
        {
            SurfaceSizeChanged?.Invoke();
        }
    }

    private sealed class FakeVideoRenderer : IVideoRenderer
    {
        public string Name => "fake";

        public VideoStreamInfo? ConfiguredStream { get; private set; }

        public IVideoRenderTarget? Target { get; private set; }

        public int RenderedFrames { get; private set; }

        /// <summary>Stands in for a renderer that needs longer to show a frame than the file gives it.</summary>
        public int RenderDelayMilliseconds { get; init; }

        public PlaybackOperationResult Configure(VideoStreamInfo stream, IVideoRenderTarget target)
        {
            ConfiguredStream = stream;
            Target = target;
            return PlaybackOperationResult.Success;
        }

        public PlaybackOperationResult Render(RawVideoFrame frame)
        {
            if (RenderDelayMilliseconds > 0)
            {
                Thread.Sleep(RenderDelayMilliseconds);
            }

            RenderedFrames++;
            return PlaybackOperationResult.Success;
        }

        /// <summary>
        /// Counts the times the pipeline offered the renderer a surface change while nothing was being
        /// presented; the stand-in does not keep a surface of its own, so taking it up is nothing.
        /// </summary>
        public int RefreshedSurfaces { get; private set; }

        public PlaybackOperationResult RefreshSurface()
        {
            RefreshedSurfaces++;
            return PlaybackOperationResult.Success;
        }

        /// <summary>
        /// What the display took, for the test that asks: a stand-in has no display, so it says nothing until
        /// a test tells it what to answer.
        /// </summary>
        public long? DisplayedFrameCount { get; set; }

        public bool TryGetDisplayedFrameCount(out long count)
        {
            count = DisplayedFrameCount ?? 0;
            return DisplayedFrameCount.HasValue;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeAudioRenderer : IAudioRenderer
    {
        private readonly DeviceClock _clock = new();
        private long _queuedSampleCount;
        private volatile bool _noRoom;

        public string Name => "fake";

        public double Volume { get; set; } = 1.0;

        public bool IsMuted { get; set; }

        public bool CanConfigure { get; init; } = true;

        public string RefusalReason { get; init; } = string.Empty;

        public int SubmitDelayMilliseconds { get; init; }

        /// <summary>
        /// Stands in for a device that cannot be handed audio: the hand-off reports this failure, which is what
        /// the pipeline is expected to turn into its own error rather than into a dead thread.
        /// </summary>
        public Exception? SubmitFailure { get; init; }

        /// <summary>
        /// Stands in for a device that has no room: a hand-off says so instead of completing, until it is
        /// given room again. The real renderer asks the device; this one is told, because "a full device" has
        /// to be arranged rather than waited for.
        /// </summary>
        public bool NoRoom
        {
            get => _noRoom;
            set => _noRoom = value;
        }

        public AudioStreamInfo? ConfiguredStream { get; private set; }

        public int SubmittedBuffers { get; private set; }

        public int SubmittedSamples { get; private set; }

        /// <summary>How many hand-offs the pipeline has started, whether or not they completed.</summary>
        public int SubmitsStarted { get; private set; }

        /// <summary>How many hand-offs the pipeline let go of before the device had taken the whole block.</summary>
        public int AbandonedSubmits { get; private set; }

        /// <summary>How many times the device has been let go of what it holds.</summary>
        public int FlushCount { get; private set; }

        public bool ClockIsRunning => Clock.IsRunning;

        /// <summary>How many times the pipeline has told the device's clock to start, changed or not.</summary>
        public int ClockStarts => _clock.Starts;

        /// <summary>How many times the pipeline has told the device's clock to pause, changed or not.</summary>
        public int ClockPauses => _clock.Pauses;

        public IPlaybackClock Clock => PlaysWhatItIsGiven ? PlayingDevice : _clock;

        /// <summary>
        /// Stands in for a device that plays what it is given and then falls silent: its position counts what has
        /// been *played*, so it stops when it runs dry even though the stream is still marked running. Every other
        /// stand-in here advances with the wall clock, which is why none of them could show a picture waiting for a
        /// moment that has already gone by.
        /// </summary>
        public bool PlaysWhatItIsGiven { get; init; }

        private DrainingDeviceClock? _playingDevice;

        private DrainingDeviceClock PlayingDevice => _playingDevice ??= new DrainingDeviceClock();

        /// <summary>Takes a block into the device, which the draining clock then plays at real time.</summary>
        internal void OnBlockTaken(RawAudioBuffer buffer)
        {
            if (PlaysWhatItIsGiven)
            {
                PlayingDevice.Submit(buffer.Duration);
            }
        }

        /// <summary>
        /// Run on whichever thread is asking the device how much it holds, just before the answer is given.
        /// The question is asked in one place — whether the device has played the last of it out — so a test
        /// that arms this once there is nothing left to hand over is inside that question, which is the only
        /// moment "the file ended while the user paused" can be arranged rather than raced for. One-shot,
        /// because what the test wants is the next answer, not every one.
        /// </summary>
        public Action? BeforeReadingQueuedSampleCount { get; set; }

        public long QueuedSampleCount
        {
            get
            {
                Action? beforeReading = BeforeReadingQueuedSampleCount;
                if (beforeReading is not null)
                {
                    BeforeReadingQueuedSampleCount = null;
                    beforeReading();
                }

                if (PlaysWhatItIsGiven)
                {
                    int sampleRate = ConfiguredStream?.SampleRate ?? 0;
                    return (long)Math.Round(PlayingDevice.QueuedSeconds * sampleRate);
                }

                return _queuedSampleCount;
            }

            set => _queuedSampleCount = value;
        }
        public PlaybackOperationResult Configure(AudioStreamInfo stream)
        {
            if (!CanConfigure)
            {
                return PlaybackOperationResult.Failure(RefusalReason);
            }

            ConfiguredStream = stream;
            return PlaybackOperationResult.Success;
        }

        /// <summary>
        /// A submit leaves the clock alone: whether the device runs is the pipeline's decision, and a stand-in
        /// that started it would release a hold the picture depends on — which is what the real renderer used
        /// to do, and what no test could see while this double was silent about it.
        /// </summary>
        public IAudioSubmit Submit(RawAudioBuffer buffer)
        {
            SubmitsStarted++;
            return new FakeAudioSubmit(this, buffer);
        }

        public PlaybackOperationResult Flush()
        {
            FlushCount++;

            // What the renderer's own flush does, in the same order: the device's travel starts over, and the
            // clock is re-anchored at the position it was showing, so a flush does not move playback. For the
            // draining device that also means the buffer is let go of, which is what a reset does.
            if (PlaysWhatItIsGiven)
            {
                TimeSpan playing = PlayingDevice.Position;
                PlayingDevice.Reset();
                PlayingDevice.SetPosition(playing);
            }
            else
            {
                TimeSpan position = _clock.Position;
                _clock.ResetPoint();
                _clock.SetPosition(position);
            }

            return PlaybackOperationResult.Success;
        }

        /// <summary>How many times the pipeline has let go of this device — once per open it was made for.</summary>
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }

        /// <summary>
        /// One block on its way to the device: it is taken in one go unless the device has no room, in which
        /// case it says so and keeps its place — which is what the pipeline is expected to wait on and resume,
        /// and what a double that completed unconditionally could never show.
        /// </summary>
        private sealed class FakeAudioSubmit : IAudioSubmit
        {
            private readonly FakeAudioRenderer _renderer;
            private readonly RawAudioBuffer _buffer;
            private bool _delayed;
            private bool _finished;

            internal FakeAudioSubmit(FakeAudioRenderer renderer, RawAudioBuffer buffer)
            {
                _renderer = renderer;
                _buffer = buffer;
            }

            public AudioSubmitYield Current { get; private set; }

            public PlaybackOperationResult Result { get; private set; } = PlaybackOperationResult.Success;

            public bool MoveNext()
            {
                if (_finished)
                {
                    return false;
                }

                if (!_delayed)
                {
                    _delayed = true;
                    if (_renderer.SubmitDelayMilliseconds > 0)
                    {
                        Thread.Sleep(_renderer.SubmitDelayMilliseconds);
                    }
                }

                if (_renderer.NoRoom)
                {
                    Current = AudioSubmitYield.NoRoom;
                    return true;
                }

                _finished = true;

                if (_renderer.SubmitFailure is Exception failure)
                {
                    Result = PlaybackOperationResult.Failure($"Unable to queue audio: {failure.Message}", failure);
                    return false;
                }

                _renderer.SubmittedBuffers++;
                _renderer.SubmittedSamples += _buffer.SampleCount;
                _renderer.OnBlockTaken(_buffer);
                return false;
            }

            public void Dispose()
            {
                if (!_finished)
                {
                    _renderer.AbandonedSubmits++;
                }

                _finished = true;
            }
        }
    }

    /// <summary>
    /// The device as a clock, which is what the renderer's own clock is: a base the pipeline sets, plus how
    /// far the device has travelled since it was last reset.
    /// <para>
    /// A wall clock stands in for this badly, and the difference is the reason this class exists: a device
    /// keeps its travel across a seek. A stand-in that always reported wall time could not see a pipeline
    /// that moved the position without letting go of what the device held — so a seek could play the old
    /// position's sound under the new position's picture, and take the clock with it, with every test green.
    /// Here <see cref="ResetPoint"/> is the device being reset, and the position after a seek says which of
    /// the two happened.
    /// </para>
    /// </summary>
    private sealed class DeviceClock : IPlaybackClock
    {
        private readonly StopwatchClock _device = new();

        private TimeSpan _zero;
        private TimeSpan _base;
        private int _starts;
        private int _pauses;

        /// <summary>How many times the pipeline has told this clock to start — changed or not.</summary>
        public int Starts => Volatile.Read(ref _starts);

        /// <summary>How many times the pipeline has told this clock to pause — changed or not.</summary>
        public int Pauses => Volatile.Read(ref _pauses);

        public bool IsRunning => _device.IsRunning;

        public double Rate => 1.0;

        public TimeSpan Position => _base + (_device.Position - _zero);

        public void Start()
        {
            Interlocked.Increment(ref _starts);
            _device.Start();
        }

        public void Pause()
        {
            Interlocked.Increment(ref _pauses);
            _device.Pause();
        }

        public void SetPosition(TimeSpan position)
        {
            // The device's travel is taken as the zero point, so time reads as the position that was asked
            // for from wherever the device is — reset or not, which is what makes the clock's answer honest
            // in the window before the device's own thread gets round to the reset.
            _base = position;
            _zero = _device.Position;
        }

        public void SetRate(double rate)
        {
        }

        /// <summary>The device being reset: its travel starts over. What it holds is gone with it.</summary>
        public void ResetPoint()
        {
            _device.SetPosition(TimeSpan.Zero);
        }
    }

    /// <summary>
    /// The device as a WASAPI stream actually behaves: its position counts what has been **played**, so when it has
    /// played everything it was given, the position stops — while the stream is still marked running. That is the
    /// difference between this and <see cref="DeviceClock"/>, and it is the whole reason a picture whose last frames
    /// are due after the sound can be left waiting for a moment that never comes.
    /// </summary>
    private sealed class DrainingDeviceClock : IPlaybackClock
    {
        private readonly object _lock = new();
        private readonly Stopwatch _since = Stopwatch.StartNew();

        private TimeSpan _base;
        private double _playedSeconds;
        private double _queuedSeconds;
        private bool _running;

        public TimeSpan Position
        {
            get
            {
                lock (_lock)
                {
                    Drain();
                    return _base + TimeSpan.FromSeconds(_playedSeconds);
                }
            }
        }

        public bool IsRunning
        {
            get
            {
                lock (_lock)
                {
                    return _running;
                }
            }
        }

        public double Rate => 1.0;

        public void Start()
        {
            lock (_lock)
            {
                Drain();
                _running = true;
                _since.Restart();
            }
        }

        public void Pause()
        {
            lock (_lock)
            {
                Drain();
                _running = false;
            }
        }

        public void SetPosition(TimeSpan position)
        {
            lock (_lock)
            {
                Drain();
                _base = position;
                _playedSeconds = 0;
            }
        }

        public void SetRate(double rate)
        {
        }

        /// <summary>How much of what it was given is still unplayed, which is what the device reports as queued.</summary>
        internal double QueuedSeconds
        {
            get
            {
                lock (_lock)
                {
                    Drain();
                    return _queuedSeconds;
                }
            }
        }

        /// <summary>The device takes a block and will play it at real time.</summary>
        internal void Submit(TimeSpan duration)
        {
            lock (_lock)
            {
                Drain();
                _queuedSeconds += duration.TotalSeconds;
                _since.Restart();
            }
        }

        /// <summary>What a reset does: the device lets go of everything it was holding.</summary>
        internal void Reset()
        {
            lock (_lock)
            {
                Drain();
                _queuedSeconds = 0;
            }
        }

        /// <summary>
        /// Plays whatever time has passed since the last look, but never more than it was given: this is the device
        /// running dry, and it is what makes the clock stop where the sound ends.
        /// </summary>
        private void Drain()
        {
            if (!_running)
            {
                _since.Restart();
                return;
            }

            double elapsed = _since.Elapsed.TotalSeconds;
            _since.Restart();

            double played = Math.Min(elapsed, _queuedSeconds);
            _queuedSeconds -= played;
            _playedSeconds += played;
        }
    }
}

