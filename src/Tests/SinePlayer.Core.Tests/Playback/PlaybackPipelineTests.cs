// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

using SinePlayer.Core.Playback;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Abstractions.Timing;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Internal.Timing;

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

            PlaybackResult opened = pipeline.Open(MediaLocator.Parse(path), new FakeRenderTarget());

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
    public void PlaysAnAudioFileThroughTheRenderer()
    {
        string path = CreateWaveFile();
        try
        {
            FakeVideoRenderer videoRenderer = new();
            FakeAudioRenderer audioRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, audioRenderer);

            PlaybackResult opened = pipeline.Open(MediaLocator.Parse(path));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(opened.IsSuccessful, Is.True, opened.Message);
                Assert.That(pipeline.HasAudio, Is.True);
                Assert.That(pipeline.HasVideo, Is.False);
                Assert.That(pipeline.Warning, Is.Null);
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
            FakeAudioRenderer audioRenderer = new() { QueuedSampleCount = SampleRate };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(pipeline.Open(MediaLocator.Parse(path)).IsSuccessful, Is.True);

            pipeline.Play();
            Thread.Sleep(500);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "a device that takes nothing is a wait, not a failure");
                Assert.That(pipeline.Error, Is.Null);
                Assert.That(audioRenderer.SubmittedBuffers, Is.Zero, "the device never made room, so nothing was handed over");
            }

            // Letting the device take audio again ends the wait, and what was carried through it is played
            // rather than dropped.
            audioRenderer.QueuedSampleCount = 0;
            WaitForPlaybackToFinish(pipeline);
            Assert.That(audioRenderer.SubmittedSamples, Is.EqualTo(SampleCount), "every sample reaches the device once it opens up");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A device that stops playing in the middle of a file is the one stall nothing inside the pipeline can
    /// explain, and the design's answer is to do nothing about it: the device is the clock, so a device that
    /// has stopped is time that has stopped, and the picture waits where it is, losing nothing while it
    /// waits. Doing nothing is not the same as saying nothing, though — a picture that stopped with no reason
    /// given is exactly what the user is left looking at — so the device is named for as long as it lasts,
    /// and the picture carries on by itself when the device does.
    /// </summary>
    [Test]
    public void WaitsForADeviceThatIsNotMovingAndThenCarriesOn()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            FakeAudioRenderer audioRenderer = new() { HoldTheClock = true, QueuedSampleCount = SampleRate };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            PlaybackResult opened = pipeline.Open(MediaLocator.Parse(path), new FakeRenderTarget());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(opened.IsSuccessful, Is.True, opened.Message);
                Assert.That(audioRenderer.ConfiguredStream, Is.Not.Null, "the fixture has to have sound in it");
            }

            pipeline.Play();
            Thread.Sleep(1000);
            long presentedWhileStuck = pipeline.PresentedFrameCount;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "a device that is not moving is a wait, not a failure");
                Assert.That(pipeline.Error, Is.Null);
                Assert.That(audioRenderer.SubmittedBuffers, Is.Zero, "the device never made room, so nothing was handed over");
                Assert.That(presentedWhileStuck, Is.GreaterThan(0), "the first frame is due at once, so the picture is not the thing that is stuck");
                Assert.That(pipeline.Warning, Does.Contain("not moving"), "a picture that stopped has to say why");
            }

            Thread.Sleep(300);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.DroppedFrameCount, Is.Zero, "waiting for the clock is not a reason to lose a frame");
                Assert.That(pipeline.PresentedFrameCount, Is.EqualTo(presentedWhileStuck), "nothing can be shown while the clock is not moving");
            }

            // The device plays again, so the clock moves and the picture picks up where it left off.
            audioRenderer.QueuedSampleCount = 0;
            audioRenderer.ReleaseTheClock();
            WaitForPresentedFrames(pipeline, presentedWhileStuck + 2);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.Error, Is.Null);
                Assert.That(pipeline.DroppedFrameCount, Is.Zero, "and none of the frames it waited for were lost");
            }

            // And the warning goes away on its own, because it is only ever about the position moving.
            Thread.Sleep(400);
            Assert.That(pipeline.Warning, Is.Null.Or.Not.Contain("not moving"));
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

            PlaybackResult opened = pipeline.Open(MediaLocator.Parse(path), new FakeRenderTarget());
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

            PlaybackResult opened = pipeline.Open(MediaLocator.Parse(path), new FakeRenderTarget());
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

    [Test]
    public void PausesAndResumes()
    {
        string path = CreateWaveFile(SampleRate);
        try
        {
            FakeAudioRenderer audioRenderer = new() { SubmitDelayMilliseconds = 10 };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(pipeline.Open(MediaLocator.Parse(path)).IsSuccessful, Is.True);

            pipeline.Play();
            Thread.Sleep(50);
            pipeline.Pause();

            TimeSpan paused = pipeline.Position;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused));
                Assert.That(audioRenderer.ClockIsRunning, Is.False);
            }

            Thread.Sleep(50);
            Assert.That(pipeline.Position, Is.EqualTo(paused), "a paused pipeline must not move");

            pipeline.Play();
            WaitForPlaybackToFinish(pipeline);

            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void SeeksWhilePlayingAndKeepsGoing()
    {
        string path = CreateWaveFile(SampleRate);
        try
        {
            FakeAudioRenderer audioRenderer = new() { SubmitDelayMilliseconds = 5 };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(pipeline.Open(MediaLocator.Parse(path)).IsSuccessful, Is.True);
            pipeline.Play();
            Thread.Sleep(40);

            var target = TimeSpan.FromSeconds(0.5);
            PlaybackResult seeked = pipeline.Seek(target);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(seeked.IsSuccessful, Is.True, seeked.Message);
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing));
                Assert.That(pipeline.Position, Is.GreaterThanOrEqualTo(target - TimeSpan.FromMilliseconds(50)));
            }

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

            Assert.That(pipeline.Open(MediaLocator.Parse(path)).IsSuccessful, Is.True);

            var target = TimeSpan.FromSeconds(0.25);
            PlaybackResult seeked = pipeline.Seek(target);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(seeked.IsSuccessful, Is.True, seeked.Message);
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused), "seeking must not start playback");
                Assert.That(pipeline.Position, Is.GreaterThanOrEqualTo(target - TimeSpan.FromMilliseconds(50)));
            }

            pipeline.Play();
            WaitForPlaybackToFinish(pipeline);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);
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

            Assert.That(pipeline.Open(MediaLocator.Parse(path), new FakeRenderTarget()).IsSuccessful, Is.True);
            pipeline.Play();
            WaitForPlaybackToFinish(pipeline);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);

            long once = pipeline.PresentedFrameCount;
            Assert.That(once, Is.GreaterThan(0), "the frame should have been shown once");

            Assert.That(pipeline.Seek(TimeSpan.Zero).IsSuccessful, Is.True);
            pipeline.Play();

            WaitForPresentedFrames(pipeline, once + 1);
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

            Assert.That(pipeline.Open(MediaLocator.Parse(path), new FakeRenderTarget()).IsSuccessful, Is.True);
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
            PlaybackResult seeked = SeekFromAnotherThread(pipeline, TimeSpan.FromSeconds(20));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(seeked.IsSuccessful, Is.True, seeked.Message);
                Assert.That(pipeline.Position, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(19)));
            }
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
        string path = Av1FixtureFile.Create(gopSize: Av1FixtureFile.FrameCount);
        try
        {
            FFmpegSourceProviderFactory sourceFactory = new();
            Assert.That(sourceFactory.TryOpen(MediaLocator.Parse(path), out IMediaSource? opened).IsSuccessful, Is.True);

            TimeSpan target;
            using (IMediaSource source = opened!)
            {
                target = FindASeekThatMisses(source);
            }

            if (target == TimeSpan.Zero)
            {
                Assert.Ignore("The fixture resumes wherever it is asked to, so a seek that misses it cannot be tested.");
            }

            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer());

            Assert.That(pipeline.Open(MediaLocator.Parse(path), new FakeRenderTarget()).IsSuccessful, Is.True);
            pipeline.Play();
            WaitForPresentedFrames(pipeline, 2);

            long presented = pipeline.PresentedFrameCount;
            PlaybackResult seeked = pipeline.Seek(target);
            Assert.That(seeked.IsSuccessful, Is.True, seeked.Message);

            // The picture has to carry on from where the file resumed. If the clock stayed at the position
            // that was asked for, every frame after it is late and none of them is ever shown again.
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
                if (target - packet!.Timestamp >= margin)
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
        string path = CreateWaveFile();
        try
        {
            FakeAudioRenderer audioRenderer = new() { CanConfigure = false, RefusalReason = "no device here" };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            PlaybackResult opened = pipeline.Open(MediaLocator.Parse(path));

            using (Assert.EnterMultipleScope())
            {
                // A file whose audio will not play is still worth playing: the reason is reported, but
                // it is a warning rather than a failure.
                Assert.That(opened.IsSuccessful, Is.True, opened.Message);
                Assert.That(pipeline.Warning, Does.Contain("no device here"));
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
    public void RefusesVideoWithoutAPresentationSurface()
    {
        string path = CreateBitmapFile();
        try
        {
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), new FakeAudioRenderer());

            PlaybackResult opened = pipeline.Open(MediaLocator.Parse(path));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(opened.IsSuccessful, Is.False);
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Error));
                Assert.That(pipeline.Error, Does.Contain("presentation surface"));
            }
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

        PlaybackResult opened = pipeline.Open(MediaLocator.Parse(Path.Combine(Path.GetTempPath(), "sineplayer-missing-file.mp4")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(opened.IsSuccessful, Is.False);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Error));
            Assert.That(pipeline.Error, Is.Not.Null.And.Not.Empty);
        }
    }

    private static PlaybackPipeline CreatePipeline(
        FakeVideoRenderer videoRenderer,
        FakeAudioRenderer audioRenderer,
        IVideoDecoderFactory? videoDecoders = null)
    {
        return new PlaybackPipeline(
            new FFmpegSourceProviderFactory(),
            videoDecoders ?? new FFmpegVideoDecoderFactory(),
            new FakeVideoRendererFactory(videoRenderer),
            new FFmpegAudioDecoderFactory(),
            audioRenderer,
            new DefaultSynchronizer());
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

        public ComponentCapabilities Capabilities => _inner.Capabilities;

        public PlaybackResult TryCreate(IMediaSource source, VideoStreamInfo stream, DecoderOptions options, out IVideoDecoder? decoder)
        {
            PlaybackResult created = _inner.TryCreate(source, stream, options, out IVideoDecoder? inner);
            if (!created.IsSuccessful || inner is null)
            {
                decoder = null;
                return created;
            }

            decoder = new ThrottledVideoDecoder(inner, DecodeMilliseconds);
            return PlaybackResult.Success;
        }

        private sealed class ThrottledVideoDecoder : IVideoDecoder
        {
            private readonly IVideoDecoder _inner;
            private readonly int _decodeMilliseconds;

            internal ThrottledVideoDecoder(IVideoDecoder inner, int decodeMilliseconds)
            {
                _inner = inner;
                _decodeMilliseconds = decodeMilliseconds;
            }

            public MediaStreamInfo Stream => _inner.Stream;

            public bool IsHardwareAccelerated => _inner.IsHardwareAccelerated;

            public PixelFormat OutputFormat => _inner.OutputFormat;

            public PlaybackResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
            {
                Thread.Sleep(_decodeMilliseconds);
                return _inner.TryDecode(packet, out frames);
            }

            public PlaybackResult Drain(out IReadOnlyList<RawVideoFrame> frames)
            {
                return _inner.Drain(out frames);
            }

            public PlaybackResult Flush()
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

        public PlaybackResult TryCreate(out IVideoRenderer? component)
        {
            component = _renderer;
            return PlaybackResult.Success;
        }
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
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (pipeline.PresentedFrameCount >= count)
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.Fail($"The pipeline showed {pipeline.PresentedFrameCount} frame(s) where {count} were expected; it is {pipeline.State} at {pipeline.Position:mm\\:ss\\.fff} with the decoder at {pipeline.LastDecodedTimestamp:mm\\:ss\\.fff}, {pipeline.DroppedFrameCount} dropped and a lag of {pipeline.PictureLag.TotalMilliseconds:0} ms. {pipeline.Error}");
    }

    /// <summary>
    /// Seeks on a thread of its own, so that a seek which never returns fails the test rather than
    /// hanging it.
    /// </summary>
    private static PlaybackResult SeekFromAnotherThread(PlaybackPipeline pipeline, TimeSpan position)
    {
        var result = PlaybackResult.Failure("The seek did not return.");
        var seeker = new Thread(() => result = pipeline.Seek(position))
        {
            IsBackground = true,
        };

        seeker.Start();
        Assert.That(seeker.Join(5000), Is.True, "the seek never returned");
        return result;
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
    private static string CreateY4mFile(int frameCount = 20)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sineplayer-pipeline-{Guid.NewGuid():N}.y4m");
        byte[] luma = new byte[Width * Height];
        byte[] chroma = new byte[Width / 2 * (Height / 2)];

        using (FileStream stream = File.Create(path))
        {
            WriteText(stream, $"YUV4MPEG2 W{Width} H{Height} F1:2 Ip A1:1 C420\n");
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
    }

    private sealed class FakeVideoRenderer : IVideoRenderer
    {
        public string Name => "fake";

        public VideoStreamInfo? ConfiguredStream { get; private set; }

        public IVideoRenderTarget? Target { get; private set; }

        public int RenderedFrames { get; private set; }

        /// <summary>Stands in for a renderer that needs longer to show a frame than the file gives it.</summary>
        public int RenderDelayMilliseconds { get; init; }

        public PlaybackResult Configure(VideoStreamInfo stream, IVideoRenderTarget target)
        {
            ConfiguredStream = stream;
            Target = target;
            return PlaybackResult.Success;
        }

        public PlaybackResult Render(RawVideoFrame frame)
        {
            if (RenderDelayMilliseconds > 0)
            {
                Thread.Sleep(RenderDelayMilliseconds);
            }

            RenderedFrames++;
            return PlaybackResult.Success;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeAudioRenderer : IAudioRenderer
    {
        private readonly StopwatchClock _clock = new();
        private FrozenClock? _frozenClock;

        public string Name => "fake";

        public double Volume { get; set; } = 1.0;

        public bool IsMuted { get; set; }

        public bool CanConfigure { get; init; } = true;

        public string RefusalReason { get; init; } = string.Empty;

        public int SubmitDelayMilliseconds { get; init; }

        /// <summary>Stands in for a device that is playing but whose position never moves.</summary>
        public bool HoldTheClock { get; init; }

        public AudioStreamInfo? ConfiguredStream { get; private set; }

        public int SubmittedBuffers { get; private set; }

        public int SubmittedSamples { get; private set; }

        public bool ClockIsRunning => Clock.IsRunning;

        public IPlaybackClock Clock => HoldTheClock ? _frozenClock ??= new FrozenClock() : _clock;

        /// <summary>Lets the stand-in clock start reporting progress.</summary>
        public void ReleaseTheClock()
        {
            _frozenClock?.Release();
        }

        public long QueuedSampleCount { get; set; }

        public PlaybackResult Configure(AudioStreamInfo stream)
        {
            if (!CanConfigure)
            {
                return PlaybackResult.Failure(RefusalReason);
            }

            ConfiguredStream = stream;
            return PlaybackResult.Success;
        }

        public PlaybackResult Submit(RawAudioBuffer buffer)
        {
            if (SubmitDelayMilliseconds > 0)
            {
                Thread.Sleep(SubmitDelayMilliseconds);
            }

            SubmittedBuffers++;
            SubmittedSamples += buffer.SampleCount;
            return PlaybackResult.Success;
        }

        public PlaybackResult Flush()
        {
            return PlaybackResult.Success;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A clock that is running and reports no progress, which is what a starved device looks like, and which
    /// can be let go again to stand for a device that was late rather than broken.
    /// </summary>
    private sealed class FrozenClock : IPlaybackClock
    {
        private readonly object _lock = new();
        private readonly Stopwatch _watch = new();

        private TimeSpan _base;
        private bool _held = true;

        public bool IsRunning { get; private set; }

        public double Rate => 1.0;

        public TimeSpan Position
        {
            get
            {
                lock (_lock)
                {
                    return _base + (_held ? TimeSpan.Zero : _watch.Elapsed);
                }
            }
        }

        public void Start()
        {
            IsRunning = true;
            _watch.Restart();
        }

        public void Pause()
        {
            IsRunning = false;
            _watch.Stop();
        }

        public void SetPosition(TimeSpan position)
        {
            lock (_lock)
            {
                _base = position;
                _watch.Restart();
            }
        }

        public void SetRate(double rate)
        {
        }

        /// <summary>Reports progress from here on.</summary>
        public void Release()
        {
            lock (_lock)
            {
                _held = false;
                _watch.Restart();
            }
        }
    }
}

