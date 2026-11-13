// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Diagnostics;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Hosting;

namespace SinePlayer.Core.Tests.Playback;

public partial class PlaybackPipelineTests
{
    /// <summary>
    /// A surface change has to reach the picture thread even when there is no frame to show: a paused
    /// position whose picture is owed leaves that thread waiting for one, and a window resize is not a frame.
    /// Nothing about the queue changes, so a thread waiting *in* the queue cannot be told â€” which is what
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
                new ThrottledVideoDecoderFactory { DecodeMilliseconds = 1300, FastPackets = 1 },
                videoTarget: target);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
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

            OperationResult opened = Open(pipeline, path);
            Assert.That(opened.IsSuccessful, Is.True, opened.Message);
            pipeline.Play();

            Thread.Sleep(1500);

            long presented = pipeline.VideoChains[0].PresentedFrameCount;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.Error, Is.Null);
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "falling behind is not failing");
                Assert.That(presented, Is.GreaterThan(0), "something has to be shown");
                Assert.That(pipeline.VideoChains[0].DroppedFrameCount, Is.GreaterThan(0), "a picture that cannot keep up has to give up on frames");
                Assert.That(pipeline.VideoChains[0].PictureLag, Is.LessThan(TimeSpan.FromMilliseconds(400)), "and what it gives up on is what keeps it in step");
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
    /// A surface that has not been relieved of the frame it is holding cannot show another one, so drawing the next
    /// frame is work for a picture nobody sees. The renderer is asked before the work is done, and a refusal costs the
    /// frame: it is given up rather than drawn, and the loop carries on instead of waiting for room inside the
    /// presentation call.
    /// <para>
    /// The frame that anchors the clock is the exception and the reason: asking about it would let a display that
    /// refuses everything leave the picture with no moment to start from, which is a freeze with nothing to report.
    /// </para>
    /// </summary>
    [Test]
    public void GivesUpAFrameTheDisplayCannotTakeInsteadOfDrawingIt()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            // A display that never offers a slot: everything after the anchoring frame finds the same answer.
            FakeVideoRenderer videoRenderer = new() { FrameSlotsAvailable = false };
            FakeAudioRenderer audioRenderer = new() { SubmitDelayMilliseconds = 30 };
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, audioRenderer);

            OperationResult opened = Open(pipeline, path);
            Assert.That(opened.IsSuccessful, Is.True, opened.Message);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitFor(() => pipeline.VideoChains[0].DroppedFrameCount > 0, "a frame the display cannot take has to be given up");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(videoRenderer.RenderedFrames, Is.EqualTo(1), "only the frame that anchors the clock is drawn");
                Assert.That(pipeline.VideoChains[0].PresentedFrameCount, Is.EqualTo(1), "and it is the only presentation");
                Assert.That(pipeline.VideoChains[0].DroppedFrameCount, Is.GreaterThan(0), "the frames after it are given up, not drawn");
                Assert.That(pipeline.Error, Is.Null, "a display that is behind is not a failure");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A paused picture still has to follow the window: the host reports a new surface size or scale, and
    /// nothing is being presented, so the renderer is never asked to draw â€” which is how a paused video
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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            pipeline.Play();
            WaitForPresentedFrames(pipeline, 2);
            pipeline.Pause();

            // The refresh is what the paused branch does, and when it arrives depends on when the pause reaches
            // that thread â€” so it is waited for rather than assumed to come after the line below. What must not
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
    /// Turning a picture is not a new frame, so a paused video has to be drawn again for the change to show:
    /// the host sets the transform, the presentation thread takes it up, and nothing is presented on the way.
    /// </summary>
    [Test]
    public void TurnsThePictureWhilePausedWithoutWaitingForAFrame()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer { SubmitDelayMilliseconds = 30 });

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            pipeline.Play();
            WaitForPresentedFrames(pipeline, 2);
            pipeline.Pause();
            WaitFor(() => pipeline.State == MediaEngineState.Paused, "the pipeline is paused");

            VideoTransform turned = VideoTransform.Identity with { Orientation = new VideoOrientation(1, false) };
            long presented = videoRenderer.RenderedFrames;
            pipeline.VideoChains[0].Transform = turned;

            WaitFor(() => videoRenderer.LastRefreshedTransform == turned, "the turned picture is offered to the renderer");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(videoRenderer.RenderedFrames, Is.EqualTo(presented), "turning the picture presents no frame");
                Assert.That(pipeline.VideoChains[0].Transform, Is.EqualTo(turned), "the pipeline keeps what the host set");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A phone records the turn it was held at, and a player that ignores it draws the picture on its side. What the
    /// container says is turned before what the viewer asks for, so a viewer turning a portrait recording further
    /// means further.
    /// </summary>
    [Test]
    public void TurnsThePictureTheContainerSaysIsSideways()
    {
        string path = VideoFixtureFile.CreateH264(rotation: 90);
        try
        {
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer());

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            WaitFor(() => videoRenderer.RenderedFrames > 0, "the paused picture to be drawn once");

            var recorded = new VideoOrientation(3, false);
            Assert.That(videoRenderer.LastRenderedTransform.Orientation, Is.EqualTo(recorded), "the turn the recording was made at is applied");

            var asked = new VideoOrientation(1, false);
            pipeline.VideoChains[0].Transform = VideoTransform.Identity with { Orientation = asked };
            WaitFor(() => videoRenderer.LastRefreshedTransform.Orientation == asked.Combine(recorded), "the viewer's turn is added to the recording's");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(videoRenderer.LastRefreshedTransform.Orientation, Is.EqualTo(VideoOrientation.Identity), "a quarter turn against the recording's three quarters is upright");
                Assert.That(pipeline.VideoChains[0].Transform.Orientation, Is.EqualTo(asked), "the pipeline keeps what the host set");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// While frames are arriving, the transform travels with each one rather than being remembered by the
    /// renderer: a renderer replaced mid-playback must not be able to keep a picture turned the way the old one
    /// had it.
    /// </summary>
    [Test]
    public void RendersEveryFrameThroughTheTransformTheHostSet()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer());

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);

            // The paused picture is drawn by the presentation thread, so the first one is waited for rather than
            // assumed: what is asserted here is how it was drawn, not whether the thread has had its turn yet.
            WaitFor(() => videoRenderer.RenderedFrames > 0, "the paused picture to be drawn once");
            Assert.That(videoRenderer.LastRenderedTransform, Is.EqualTo(VideoTransform.Identity), "nothing is turned until the host says so");

            VideoTransform mirrored = VideoTransform.Identity with { Orientation = new VideoOrientation(0, true) };
            pipeline.VideoChains[0].Transform = mirrored;

            // Frames drawn from here on must carry it: the count is taken so that the picture the paused position
            // already showed cannot answer for the playing one.
            long drawn = videoRenderer.RenderedFrames;
            pipeline.Play();

            WaitForPresentedFrames(pipeline, 2);
            WaitFor(() => videoRenderer.RenderedFrames > drawn, "the playing picture to be drawn");

            Assert.That(videoRenderer.LastRenderedTransform, Is.EqualTo(mirrored));
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
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer { SubmitDelayMilliseconds = 30 }, videoTarget: target);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
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

    /// <summary>
    /// The surface is handed back only when the renderer is: one that is replaced is drawn over by its successor, and
    /// emptying it first would be a black flash between two pictures of the same film. The same goes for choosing
    /// another track, which is a new chain rather than the end of the picture.
    /// </summary>
    [Test]
    public void KeepsTheSurfaceWhenThePictureIsReplacedRatherThanTurnedOff()
    {
        string path = CreateY4mFile(frameCount: 60, frameRate: "30:1");
        try
        {
            FakeVideoRenderer first = new() { Name = "first" };
            FakeVideoRenderer second = new() { Name = "second" };
            using PlaybackPipeline pipeline = CreatePipeline(first, new FakeAudioRenderer());

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Await(pipeline.Play());
            WaitFor(() => first.RenderedFrames > 2, "playback to be under way");

            SwapVideoRenderer(pipeline, new FakeVideoRendererFactory(second));
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);
            WaitFor(() => second.RenderedFrames > 0, "the new renderer to be drawing");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(first.ClearedSurfaces, Is.Zero, "the frame is drawn over rather than emptied away");
                Assert.That(second.ClearedSurfaces, Is.Zero);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

}
