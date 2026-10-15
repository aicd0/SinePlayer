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
        string path = CreateBitmapFile();
        try
        {
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer());

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            Assert.That(pipeline.DisplayedFrameCount, Is.Null, "nothing has been presented, so the renderer has nothing to report");

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
            FakeAudioRenderer audioRenderer = new() { QueuedSampleCount = SampleRate };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);

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
            WarningLog warnings = new(pipeline);

            PlaybackOperationResult opened = Open(pipeline, path, new FakeRenderTarget());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(opened.IsSuccessful, Is.True, opened.Message);
                Assert.That(audioRenderer.ConfiguredStream, Is.Not.Null, "the fixture has to have sound in it");
            }

            pipeline.Play();

            // The device is given a second of standing still before it is called stuck, so the line arrives
            // when the pipeline decides and not when the test does.
            WaitForWarning(warnings, "not moving");
            long presentedWhileStuck = pipeline.PresentedFrameCount;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "a device that is not moving is a wait, not a failure");
                Assert.That(pipeline.Error, Is.Null);
                Assert.That(audioRenderer.SubmittedBuffers, Is.Zero, "the device never made room, so nothing was handed over");
                Assert.That(presentedWhileStuck, Is.GreaterThan(0), "the first frame is due at once, so the picture is not the thing that is stuck");
                Assert.That(warnings.Latest, Does.Contain("not moving"), "a picture that stopped has to say why");
                Assert.That(warnings.Messages, Has.Count.EqualTo(1), "one stall is one line, not one per check");
            }

            Thread.Sleep(300);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.DroppedFrameCount, Is.Zero, "waiting for the clock is not a reason to lose a frame");
                Assert.That(pipeline.PresentedFrameCount, Is.EqualTo(presentedWhileStuck), "nothing can be shown while the clock is not moving");
            }

            // The device plays again, so the clock moves and the picture picks up where it left off. Movement
            // is what ends the stall, and it is said as one line — the pair to the one that started it.
            audioRenderer.QueuedSampleCount = 0;
            audioRenderer.ReleaseTheClock();
            WaitForWarning(warnings, "moving again");
            WaitForPresentedFrames(pipeline, presentedWhileStuck + 2);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.Error, Is.Null);
                Assert.That(pipeline.DroppedFrameCount, Is.Zero, "and none of the frames it waited for were lost");
                Assert.That(warnings.Latest, Does.Contain("moving again"), "the last line is the recovery, not the stall");
            }
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

            TimeSpan paused = pipeline.Position;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused));
                Assert.That(audioRenderer.ClockIsRunning, Is.False);
            }

            Thread.Sleep(50);
            Assert.That(pipeline.Position, Is.EqualTo(paused), "a paused pipeline must not move");

            // Pausing what is already paused asks for what is already true, which is not a failure.
            Assert.That(Await(pipeline.Pause()).IsSuccessful, Is.True);

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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            pipeline.Play();
            Thread.Sleep(40);

            var target = TimeSpan.FromSeconds(0.5);
            SeekAndWait(pipeline, target);

            // The seek is carried out before the pipeline says so, so everything about it can be read here:
            // the clock is where the seek asked and playback is still playing.
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "a seek while playing has to go on playing");
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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);

            var target = TimeSpan.FromSeconds(0.25);
            SeekAndWait(pipeline, target);

            using (Assert.EnterMultipleScope())
            {
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
            // playback was at.
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "a seek while playing has to go on playing");
                Assert.That(pipeline.Position, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(18)));
            }
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

            var askedAt = Stopwatch.StartNew();
            PlaybackOperationResult seeked = Await(pipeline.Seek(TimeSpan.FromSeconds(1)));
            askedAt.Stop();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(seeked.IsSuccessful, Is.True, seeked.Message);
                Assert.That(askedAt.ElapsedMilliseconds, Is.LessThan(1000),
                    "a command must not wait for the end of the file to play out");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "the seek is where playback goes, not the end of a file");
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

            // Now that the whole sound has been handed over, the device stops taking any more of it, and the
            // seek puts playback near the end so the picture has almost nothing left to show: what holds the
            // ending up is the sound and nothing else.
            audioRenderer.QueuedSampleCount = SampleRate;
            Assert.That(Await(pipeline.Seek(TimeSpan.FromSeconds(6))).IsSuccessful, Is.True);
            Thread.Sleep(200);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "a sound that has not played out is not an ending");

            // The device takes audio again, so the last of the sound is played and the ending is earned.
            audioRenderer.QueuedSampleCount = 0;
            WaitForPlaybackToFinish(pipeline);
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

            Assert.That(Open(pipeline, path, new FakeRenderTarget()).IsSuccessful, Is.True);
            pipeline.Play();
            WaitForPresentedFrames(pipeline, 2);

            long presented = pipeline.PresentedFrameCount;
            SeekAndWait(pipeline, target);

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
    /// seek itself. Neither is a failure of playback, and playback goes on being exactly what it was.
    /// <para>
    /// The two refusals are answered in different places, and deliberately: whether the source can be moved at
    /// all is fixed when it is opened, so it is known without the reader and comes back in the result; whether
    /// it <em>will</em> move this time is only known by trying, which the reader does on the thread that owns
    /// the cursor — so it is reported as a line, and the caller keeps the answer it already has.
    /// </para>
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public void KeepsPlayingWhenTheSourceRefusesToSeek(bool refusesInAdvance)
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

            using (Assert.EnterMultipleScope())
            {
                // A source that refuses in advance is refused without the reader ever being involved.
                Assert.That(seeked.IsSuccessful, Is.EqualTo(!refusesInAdvance), seeked.Message);

                // A source that will not be moved is not playback failing:
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing));
                Assert.That(pipeline.Error, Is.Null);
            }

            if (!refusesInAdvance)
            {
                // The reader found out by trying, and a line is what it has to say it with.
                WaitForWarning(warnings, "refused to seek");
            }

            // Still playing a moment later, rather than left half way through a seek that never happened.
            Thread.Sleep(100);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), pipeline.Error);
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
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            // The failure is reported by the decoding thread and the end of the file by the reader, and the two
            // are items of the same queue in whatever order they were posted: the file can be read out before
            // the decoder's report is heard, so the state to wait for is the failure and not "playback stopped".
            // Waiting for the first of the two to arrive is waiting for a coin toss.
            WaitForState(pipeline, MediaEngineState.Error);

            // Nothing about this was asked for and nothing can go on: a decoder that dies is playback failing,
            // which is what the error state is for.
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
        ISourceProviderFactory? sourceFactory = null)
    {
        return new PlaybackPipeline(
            sourceFactory ?? new FFmpegSourceProviderFactory(),
            videoDecoders ?? new FFmpegVideoDecoderFactory(),
            new FakeVideoRendererFactory(videoRenderer),
            new FFmpegAudioDecoderFactory(),
            audioRenderer,
            new DefaultSynchronizer());
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

        public ComponentCapabilities Capabilities => _inner.Capabilities;

        public PlaybackOperationResult TryCreate(IMediaSource source, VideoStreamInfo stream, DecoderOptions options, out IVideoDecoder? decoder)
        {
            PlaybackOperationResult created = _inner.TryCreate(source, stream, options, out IVideoDecoder? inner);
            if (!created.IsSuccessful || inner is null)
            {
                decoder = null;
                return created;
            }

            decoder = new ThrottledVideoDecoder(inner, DecodeMilliseconds);
            return PlaybackOperationResult.Success;
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

            public PlaybackOperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
            {
                Thread.Sleep(_decodeMilliseconds);
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
        for (int attempt = 0; attempt < 200; attempt++)
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
        Assert.That(seek is not null && seek.Wait(10000), Is.True, "the seek was never carried out");
        Assert.That(seek!.Result.IsSuccessful, Is.True, seek.Result.Message);
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

        /// <summary>How many times the device has been let go of what it holds.</summary>
        public int FlushCount { get; private set; }

        public bool ClockIsRunning => Clock.IsRunning;

        public IPlaybackClock Clock => HoldTheClock ? _frozenClock ??= new FrozenClock() : _clock;

        /// <summary>Lets the stand-in clock start reporting progress.</summary>
        public void ReleaseTheClock()
        {
            _frozenClock?.Release();
        }

        public long QueuedSampleCount { get; set; }

        public PlaybackOperationResult Configure(AudioStreamInfo stream)
        {
            if (!CanConfigure)
            {
                return PlaybackOperationResult.Failure(RefusalReason);
            }

            ConfiguredStream = stream;
            return PlaybackOperationResult.Success;
        }

        public PlaybackOperationResult Submit(RawAudioBuffer buffer)
        {
            if (SubmitDelayMilliseconds > 0)
            {
                Thread.Sleep(SubmitDelayMilliseconds);
            }

            SubmittedBuffers++;
            SubmittedSamples += buffer.SampleCount;
            return PlaybackOperationResult.Success;
        }

        public PlaybackOperationResult Flush()
        {
            FlushCount++;

            // What the renderer's own flush does, in the same order: the device's travel starts over, and the
            // clock is re-anchored at the position it was showing, so a flush does not move playback.
            if (_frozenClock is null)
            {
                TimeSpan position = _clock.Position;
                _clock.ResetPoint();
                _clock.SetPosition(position);
            }

            return PlaybackOperationResult.Success;
        }

        public void Dispose()
        {
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

        public bool IsRunning => _device.IsRunning;

        public double Rate => 1.0;

        public TimeSpan Position => _base + (_device.Position - _zero);

        public void Start()
        {
            _device.Start();
        }

        public void Pause()
        {
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

