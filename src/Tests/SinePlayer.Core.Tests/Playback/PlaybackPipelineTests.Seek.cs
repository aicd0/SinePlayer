// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Hosting;

namespace SinePlayer.Core.Tests.Playback;

public partial class PlaybackPipelineTests
{
    /// <summary>
    /// The same edge from the other side: a second seek has to reach the thread that is still waiting to be
    /// handed the first one's picture. The clock's position is the observable â€” it is the render thread that
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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            pipeline.Play();
            WaitForPresentedFrames(pipeline, 1);

            Assert.That(Await(pipeline.Pause()).IsSuccessful, Is.True);
            SeekAndWait(pipeline, TimeSpan.FromSeconds(20));

            // The first seek's picture is still owed, so the thread is waiting for it â€” and the second seek
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

    [Test]
    public void SeeksWhilePlayingAndGoesOnPlaying()
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

            // The seek is carried out before the pipeline says so, so everything about it can be read here: a file
            // that was playing goes on playing, because a jump is not a pause â€” pausing is its own command.
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "a seek leaves the state as it found it");

            // Where the clock is, though, is not the seek's to say: the clock belongs to the render threads,
            // and the one that wakes first places it at the position that was asked for â€” a moment after the
            // seek has answered, which is why this is waited for rather than read.
            WaitFor(
                () => pipeline.Position >= target - TimeSpan.FromMilliseconds(50),
                $"The clock is at {pipeline.Position:mm\\:ss\\.fff} where the seek asked for {target:mm\\:ss\\.fff}.");

            WaitForPlaybackToFinish(pipeline);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A seek whose landing is not a keyframe â€” which is what a demuxer gives when it lands on the first packet at or after
    /// the request, and what a group of pictures makes possible â€” must not hand that packet to a decoder that has just been
    /// flushed. A real decoder answers "Invalid data found when processing input" and the playback is over; the picture waits
    /// for the next keyframe instead. The synthetic decoder refuses a packet it has no reference for, so this test sees the
    /// ordering the codecs see.
    /// </summary>
    [Test]
    public void StartsAtTheNextKeyFrameWhenTheSeekLandsInsideAGroup()
    {
        string path = SyntheticPath("gop");
        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(path, new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(3),
            KeyFrameInterval = 30,
            LandsOnKeyframes = false,
        });

        PlaybackPipeline pipeline = CreateMultiSourcePipeline(sources, new FakeSubtitleRenderer());
        try
        {
            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(path))).IsSuccessful, Is.True);

            MediaInfo info = pipeline.Sources[0].Info;
            Assert.That(Select(pipeline, MediaStreamKind.Video, info.VideoStreams[0]).IsSuccessful, Is.True);
            Assert.That(Select(pipeline, MediaStreamKind.Audio, info.AudioStreams[0]).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            Thread.Sleep(40);

            // 1.5 s is halfway between the keyframes at 1 s and 2 s.
            SeekAndWait(pipeline, TimeSpan.FromSeconds(1.5));

            Assert.That(pipeline.Error, Is.Null, "a packet with no keyframe before it must never reach the decoder");
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "a seek leaves the state as it found it");

            WaitForPlaybackToFinish(pipeline);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// A forward keyframe seek resumes at the next place a decoder can start rather than at the frame that was asked
    /// for: the source lands on the first keyframe at or after the target, so nothing from before that keyframe is ever
    /// shown, and the picture's own first frame is the keyframe itself rather than a frame of the group behind it. The
    /// synthetic decoder refuses a packet whose keyframe it never saw, so a landing that was not a keyframe would end the
    /// playback instead of merely being inaccurate.
    /// </summary>
    [Test]
    public void NextKeyframeSeekResumesAtTheKeyframeAfterThePosition()
    {
        SyntheticSourceProviderFactory sources = new(new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(3),
            KeyFrameInterval = 30,
            LandsOnKeyframes = true,
        });

        FakeVideoRenderer videoRenderer = new();
        PlaybackPipeline pipeline = CreatePipeline(
            videoRenderer,
            new FakeAudioRenderer(),
            videoDecoders: new SyntheticVideoDecoderFactory(),
            sourceFactory: sources,
            audioDecoders: new SyntheticAudioDecoderFactory());

        try
        {
            Assert.That(Open(pipeline, SyntheticPath()).IsSuccessful, Is.True);
            Assert.That(Select(pipeline, MediaStreamKind.Video, pipeline.Sources[0].Info.VideoStreams[0]).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.Position > TimeSpan.FromSeconds(0.3), "playback to be under way", 5000);

            // 1.5 s is halfway between the keyframes at 1 s and 2 s, so a forward landing is the one at 2 s.
            var target = TimeSpan.FromSeconds(1.5);
            var keyframeAfter = TimeSpan.FromSeconds(2);
            Assert.That(Await(pipeline.Seek(target, SeekMode.NextKeyframe)).IsSuccessful, Is.True);
            WaitFor(() => sources.Source?.LastSeekPosition == target, "the cursor to be asked to move", 5000);

            Assert.That(sources.Source!.LastSeekMode, Is.EqualTo(SeekMode.NextKeyframe), "the landing is looked for forward");

            int marker = videoRenderer.RenderedTimestamps.Count;
            WaitFor(() => pipeline.VideoChains[0].LastPresentedTimestamp >= keyframeAfter,
                $"a forward seek offered the still of {pipeline.VideoChains[0].LastPresentedTimestamp:g}, before the keyframe it lands on ({keyframeAfter:g})");
            WaitForPresentedFrames(pipeline, pipeline.VideoChains[0].PresentedFrameCount + 2);

            IReadOnlyList<TimeSpan> rendered = videoRenderer.RenderedTimestamps;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.Error, Is.Null, "a forward landing starts the decoder at a keyframe, so nothing is undecodable");
                Assert.That(rendered.Count, Is.GreaterThan(marker), "the picture has to carry on after a forward seek");
                Assert.That(rendered.Skip(marker).All(timestamp => timestamp >= keyframeAfter), Is.True,
                    "a forward seek showed a frame from before the keyframe it landed on");
                Assert.That(pipeline.Position, Is.GreaterThanOrEqualTo(keyframeAfter),
                    "the clock follows the picture to the keyframe the seek landed on");
            }
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// Two chains reading one file have keyframes of their own, so a landing has to be one both of them can start from:
    /// the file is asked where each stream would land and the earliest answer wins. Every chain's own keyframe is then at
    /// or after the landing, so each decoder starts from its own. Landing by the first chain's keyframe instead puts the
    /// second chain's keyframe behind the landing, and it has to wait for the group after it â€” up to a whole group of
    /// pictures of picture that no seek asked to lose. Nothing plays: a paused seek moves the reader and both decoders,
    /// and the stills they offer are where each of them started.
    /// </summary>
    [Test]
    public void LandsByTheEarliestKeyframeOfTheChainsReadingOneFile()
    {
        SyntheticSourceProviderFactory sources = new(new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(3),
            KeyFrameInterval = 30,
            SecondVideoKeyFrameOffset = 20,
            LandsOnKeyframes = true,
        });

        FakeVideoRenderer firstRenderer = new();
        FakeVideoRenderer secondRenderer = new();
        PlaybackPipeline pipeline = new() { SourceFactory = sources };
        try
        {
            IVideoChain first = Await(pipeline.AddVideoChain(new FakeRenderTarget()));
            IVideoChain second = Await(pipeline.AddVideoChain(new FakeRenderTarget()));
            first.Components.Register(new SyntheticVideoDecoderFactory());
            first.Components.Register(new FakeVideoRendererFactory(firstRenderer));
            second.Components.Register(new SyntheticVideoDecoderFactory());
            second.Components.Register(new FakeVideoRendererFactory(secondRenderer));

            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(SyntheticPath()))).IsSuccessful, Is.True);

            MediaInfo info = pipeline.Sources[0].Info;
            Assert.That(Await(first.SelectStream(info.VideoStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(second.SelectStream(info.VideoStreams[1])).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);

            // At 1.5 s the first stream's keyframe is the one at 1 s and the second stream's the one at 20/30 s.
            var target = TimeSpan.FromSeconds(1.5);
            var earliest = TimeSpan.FromSeconds(20.0 / 30);
            Assert.That(Await(pipeline.Seek(target, SeekMode.PreviousKeyframe)).IsSuccessful, Is.True);
            WaitFor(() => sources.Source?.LastSeekPosition == target, "the file to be asked to move", 5000);

            Assert.That(sources.Source!.LastSeekStreamIndex, Is.EqualTo(SyntheticMediaSource.SecondVideoStreamIndex),
                "the landing is the earliest keyframe the two chains have, not the one the first chain has");

            // Both pictures start at a keyframe no later than where the seek asked. The first chain finds its own one
            // second in, the second chain the landing itself at 20/30 s: nothing has to wait for the next group.
            WaitFor(
                () => firstRenderer.RenderedTimestamps.Any(timestamp => timestamp >= earliest && timestamp <= target)
                    && secondRenderer.RenderedTimestamps.Any(timestamp => timestamp >= earliest && timestamp <= target),
                "both pictures to start at a keyframe at or before the position the seek asked for",
                4000);

            Assert.That(pipeline.Error, Is.Null, "a landing both streams can start from leaves nothing undecodable");
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// Two picture streams of one file keyframe at the same instants, which is what one encode of one source produces, so
    /// the landing cannot be chosen by position. A seek starts the read at the block of the stream it is measured by, and
    /// the container writes one stream's keyframe before the other's: measuring it by the later block leaves the earlier
    /// stream's keyframe behind, and that picture waits a whole group of pictures. The chains' order is not the answer â€”
    /// the file is asked which of them suits both â€” so the chain added first here is the one that must not land it.
    /// </summary>
    [Test]
    public void LandsByTheStreamWhoseKeyframeTheContainerWritesFirst()
    {
        SyntheticSourceProviderFactory sources = new(new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(3),
            KeyFrameInterval = 30,
            SecondVideoKeyFrameOffset = 0,
            LandsOnKeyframes = true,
        });

        FakeVideoRenderer firstRenderer = new();
        FakeVideoRenderer secondRenderer = new();
        PlaybackPipeline pipeline = new() { SourceFactory = sources };
        try
        {
            IVideoChain first = Await(pipeline.AddVideoChain(new FakeRenderTarget()));
            IVideoChain second = Await(pipeline.AddVideoChain(new FakeRenderTarget()));
            first.Components.Register(new SyntheticVideoDecoderFactory());
            first.Components.Register(new FakeVideoRendererFactory(firstRenderer));
            second.Components.Register(new SyntheticVideoDecoderFactory());
            second.Components.Register(new FakeVideoRendererFactory(secondRenderer));

            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(SyntheticPath()))).IsSuccessful, Is.True);

            MediaInfo info = pipeline.Sources[0].Info;
            Assert.That(Await(first.SelectStream(info.VideoStreams[1])).IsSuccessful, Is.True);
            Assert.That(Await(second.SelectStream(info.VideoStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);

            var target = TimeSpan.FromSeconds(1.5);
            var landing = TimeSpan.FromSeconds(1);
            Assert.That(Await(pipeline.Seek(target, SeekMode.PreviousKeyframe)).IsSuccessful, Is.True);
            WaitFor(() => sources.Source?.LastSeekPosition == target, "the file to be asked to move", 5000);

            Assert.That(sources.Source!.LastSeekStreamIndex, Is.EqualTo(info.VideoStreams[0].Index),
                "the landing is measured by the stream whose keyframe the container wrote first, not by the first chain");

            WaitFor(
                () => firstRenderer.RenderedTimestamps.Any(timestamp => timestamp >= landing && timestamp <= target)
                    && secondRenderer.RenderedTimestamps.Any(timestamp => timestamp >= landing && timestamp <= target),
                "both pictures to start at the keyframe they share",
                4000);

            Assert.That(pipeline.Error, Is.Null, "a landing both streams can start from leaves nothing undecodable");
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// Two picture streams of one file are not always the same length. A seek past the end of the shorter one must not land
    /// where that stream ended: a landing by a stream that is over is its last keyframe, so the rest of the file â€” which
    /// the longer stream still has â€” would be thrown away, and the position would jump back to where the short picture
    /// stopped. Only the streams that still have media at the position may decide it.
    /// </summary>
    [Test]
    public void DoesNotLandWhereAShorterPictureStreamEnded()
    {
        SyntheticSourceProviderFactory sources = new(new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(3),
            KeyFrameInterval = 30,
            SecondVideoKeyFrameOffset = 0,
            LandsOnKeyframes = true,
            VideoStopsAt = TimeSpan.FromSeconds(1),
            VideoDuration = TimeSpan.FromSeconds(1),
            SecondVideoDuration = TimeSpan.FromSeconds(3),
        });

        FakeVideoRenderer shortRenderer = new();
        FakeVideoRenderer longRenderer = new();
        PlaybackPipeline pipeline = new() { SourceFactory = sources };
        try
        {
            IVideoChain shortChain = Await(pipeline.AddVideoChain(new FakeRenderTarget()));
            IVideoChain longChain = Await(pipeline.AddVideoChain(new FakeRenderTarget()));
            shortChain.Components.Register(new SyntheticVideoDecoderFactory());
            shortChain.Components.Register(new FakeVideoRendererFactory(shortRenderer));
            longChain.Components.Register(new SyntheticVideoDecoderFactory());
            longChain.Components.Register(new FakeVideoRendererFactory(longRenderer));

            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(SyntheticPath()))).IsSuccessful, Is.True);

            MediaInfo info = pipeline.Sources[0].Info;
            Assert.That(Await(shortChain.SelectStream(info.VideoStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(longChain.SelectStream(info.VideoStreams[1])).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);

            // The picture of the first stream is over at one second; 2.5 s is well past it and the other stream still has it.
            var target = TimeSpan.FromSeconds(2.5);
            Assert.That(Await(pipeline.Seek(target, SeekMode.PreviousKeyframe)).IsSuccessful, Is.True);
            WaitFor(() => sources.Source?.LastSeekPosition == target, "the file to be asked to move", 5000);

            Assert.That(sources.Source!.LastSeekStreamIndex, Is.EqualTo(info.VideoStreams[1].Index),
                "the landing is measured by the stream that still has the position, not by the one that ended before it");

            WaitFor(() => longRenderer.RenderedTimestamps.Any(timestamp => timestamp >= target - TimeSpan.FromSeconds(0.5)),
                "the picture that still has the position to start at the keyframe it lands on",
                4000);

            Assert.That(longRenderer.RenderedTimestamps.Any(timestamp => timestamp < TimeSpan.FromSeconds(1.5)), Is.False,
                "the seek must not land where the shorter picture ended");
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// A seek past the end of one picture stream leaves that chain with nothing to present for the rest of the file, which
    /// is not the same as being starved: holding the clock for it stops the playback the other streams can still carry, and
    /// the position freezes at the position that was asked for. The stream that ended is over, so its chain waits quietly
    /// while the ones that still have media play on â€” the sound here, which is what keeps the clock when a chain has none.
    /// </summary>
    [Test]
    public void KeepsPlayingAfterASeekPastTheEndOfOnePictureStream()
    {
        SyntheticSourceProviderFactory sources = new(new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(60),
            KeyFrameInterval = 30,
            SecondVideoKeyFrameOffset = 0,
            LandsOnKeyframes = true,
            VideoStopsAt = TimeSpan.FromSeconds(20),
            VideoDuration = TimeSpan.FromSeconds(20),
            SecondVideoDuration = TimeSpan.FromSeconds(60),
        });

        FakeVideoRenderer shortRenderer = new();
        FakeVideoRenderer longRenderer = new();
        PlaybackPipeline pipeline = new() { SourceFactory = sources };
        try
        {
            IVideoChain shortChain = Await(pipeline.AddVideoChain(new FakeRenderTarget()));
            IVideoChain longChain = Await(pipeline.AddVideoChain(new FakeRenderTarget()));
            IAudioChain audioChain = Await(pipeline.AddAudioChain());
            shortChain.Components.Register(new SyntheticVideoDecoderFactory());
            shortChain.Components.Register(new FakeVideoRendererFactory(shortRenderer));
            longChain.Components.Register(new SyntheticVideoDecoderFactory());
            longChain.Components.Register(new FakeVideoRendererFactory(longRenderer));
            audioChain.Components.Register(new SyntheticAudioDecoderFactory());
            audioChain.Components.Register(new FixedAudioRendererFactory(new FakeAudioRenderer()));

            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(SyntheticPath()))).IsSuccessful, Is.True);

            MediaInfo info = pipeline.Sources[0].Info;
            Assert.That(Await(shortChain.SelectStream(info.VideoStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(longChain.SelectStream(info.VideoStreams[1])).IsSuccessful, Is.True);
            Assert.That(Await(audioChain.SelectStream(info.AudioStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForPresentedFrames(pipeline, 2);

            // The picture of the first stream is over at twenty seconds; forty is well past it and everything else has it.
            var target = TimeSpan.FromSeconds(40);
            Assert.That(Await(pipeline.Seek(target, SeekMode.PreviousKeyframe)).IsSuccessful, Is.True);

            WaitFor(() => longChain.PresentedFrameCount > 2 && pipeline.Position > target + TimeSpan.FromSeconds(1),
                $"the clock to keep running past a seek to {target:g}; it stands at {pipeline.Position:g} with {pipeline.StarvedFor ?? "nothing"} starved",
                6000);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "the playback carried on");
                Assert.That(longChain.LastPresentedTimestamp, Is.GreaterThan(target), "the stream that still has the position kept showing it");
                Assert.That(shortChain.LastPresentedTimestamp, Is.LessThan(TimeSpan.FromSeconds(21)), "the stream that ended stayed where it ended");
            }
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
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
    /// picture being paused rather than by a state check â€” the decoder waiting for room in a full frame queue
    /// that only the picture can empty, the reader idling on a packet queue it has filled â€” so a seek that
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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForPresentedFrames(pipeline, 2);
            Assert.That(Await(pipeline.Pause()).IsSuccessful, Is.True);

            // Long enough for the decoder to fill the frame queue and stop, and for the reader to fill the
            // packet queue: both are parked on the old position, which is the state the seek has to break.
            Thread.Sleep(400);

            // The position asked for is far beyond what the old one can reach: the decoder stops with the
            // frame queue full, so the newest frame it has produced is only a few frames past the last one
            // the picture took â€” around 20 s on this fixture â€” whatever the reader has queued behind it.
            SeekAndWait(pipeline, TimeSpan.FromSeconds(60));

            // Still paused, and a picture of the position that was asked for has already been produced: the
            // decoder was released and told to take the position up, and the reader is reading it rather than
            // sitting on the old position's queue. Nothing but the reset can put either of them there, which
            // is what this asserts â€” the old position cannot reach 50 s.
            WaitFor(() => pipeline.VideoChains[0].LastPresentedTimestamp >= TimeSpan.FromSeconds(50),
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
    /// presented, that is the new position's first frame â€” offered by the picture thread from the queue the
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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
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
                // to the frame the still showed (18 s â€” the keyframe a demuxer lands on). Anchoring it would
                // make the next frame, due at 20 s, two seconds early on resume, and the picture would wait
                // that out.
                Assert.That(pipeline.Position, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(19)), "a still must not move the clock");
                Assert.That(pipeline.VideoChains[0].DroppedFrameCount, Is.Zero, "a still is not a presentation, so nothing about it is late");
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
    /// still of a frame that has just been shown would be a frame shown twice â€” worse than that, it would jump
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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
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
    /// picture's word for the ending â€” said where it is learned rather than left for a presentation that is
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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
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
    /// the only thing that can â€” and it only helps if it arrives after the epoch does. Woken in between, the
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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForPlaybackToFinish(pipeline);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);

            long shownBefore = videoRenderer.RenderedFrames;
            SeekAndWait(pipeline, TimeSpan.Zero);

            // The still of the new position can only be a frame the reader read for it, so this is the reader
            // taking the seek up on its own â€” with no play, no pause and no second seek to wake it.
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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            pipeline.Play();

            // The first frame is due straight away, and the second one is two seconds away, so the thread
            // showing frames settles on it and the queue behind it fills up.
            WaitForPresentedFrames(pipeline, 1);
            Thread.Sleep(300);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.VideoChains[0].PresentedFrameCount, Is.EqualTo(1), "the second frame must not be due yet");
                Assert.That(videoRenderer.RenderedFrames, Is.EqualTo(1));
                Assert.That(pipeline.VideoChains[0].DroppedFrameCount, Is.Zero, "waiting two seconds for a frame is not a reason to give up on it");
            }

            // The reader is now waiting for room that only that frame's moment can free, and it holds the
            // lock a seek needs. Seeking has to get past it without waiting for the picture.
            SeekFromAnotherThread(pipeline, TimeSpan.FromSeconds(20));

            // Where the clock ends up is where the file resumed rather than where the seek asked to go: this
            // fixture holds a frame every two seconds, so a seek to 20 s resumes at 18 s and the clock follows
            // it once the first frame of the new position arrives. Both are far ahead of the two seconds
            // playback was at. The seek does not change the state â€” a file that was playing is still playing.
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "a seek leaves the state as it found it");

            // Where the clock is, though, is not the seek's to say: the clock belongs to the render threads,
            // and the one that wakes first places it at the position that was asked for â€” a moment after the
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

    [Test]
    public void LetsTheDeviceGoOfTheOldPositionWhenPlaybackMoves()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            FakeAudioRenderer audioRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            pipeline.Play();
            WaitForPresentedFrames(pipeline, 2);

            long presented = pipeline.VideoChains[0].PresentedFrameCount;
            SeekAndWait(pipeline, target);

            // The seek did not pause, so the picture carries on from where the file resumed by itself.

            // The picture has to carry on from where the file resumed, and the frames counted from here are
            // all frames of the new position: if the clock stayed at the position that was asked for, every
            // one of them would be late and none of them would ever be shown.
            WaitForPresentedFrames(pipeline, presented + 3);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.Position, Is.LessThan(target), "the clock has to follow where playback resumed");
                Assert.That(pipeline.VideoChains[0].PictureLag, Is.LessThan(TimeSpan.FromSeconds(1)), "the picture has to stay in step with the clock");
                Assert.That(pipeline.VideoChains[0].DroppedFrameCount, Is.LessThan(5), "frames from the resume point are not late");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A precise seek is a floor rather than a wish. The media between the keyframe the source lands on and the
    /// position asked for is still decoded â€” the frames after it refer to it â€” but it may not be shown: the
    /// still of the new position, the frames that follow and the clock all start at or after the position.
    /// </summary>
    [Test]
    public void PreciseSeekResumesAtThePositionRatherThanAtTheKeyframeBeforeIt()
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

            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer());

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            pipeline.Play();
            WaitForPresentedFrames(pipeline, 2);

            SeekAndWait(pipeline, target, SeekMode.Precise);

            // The still of the new position is the first frame at or after it, not the keyframe behind it.
            WaitFor(() => pipeline.VideoChains[0].LastPresentedTimestamp >= target,
                $"a precise seek offered the still of {pipeline.VideoChains[0].LastPresentedTimestamp:g}, before the position it asked for ({target:g})");

            int marker = videoRenderer.RenderedTimestamps.Count;

            WaitForPresentedFrames(pipeline, pipeline.VideoChains[0].PresentedFrameCount + 2);

            IReadOnlyList<TimeSpan> rendered = videoRenderer.RenderedTimestamps;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(rendered.Count, Is.GreaterThan(marker), "the picture has to carry on after a precise seek");
                Assert.That(rendered.Skip(marker).All(timestamp => timestamp >= target), Is.True,
                    "a precise seek showed a frame from before the position it asked for");
                Assert.That(pipeline.Position, Is.GreaterThanOrEqualTo(target),
                    "the clock may not stand at a position before the one a precise seek asked for");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The sound has no still and no drop rule of its own: the sink is where a block of a position playback has
    /// left gets given up on, and it has to be given up on before it can anchor the clock.
    /// </summary>
    [Test]
    public void PreciseSeekPlaysNoSoundFromBeforeThePosition()
    {
        string path = RawAudioVideoFixtureFile.Create(frameCount: 90);
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
                target = FindASeekThatLandsJustBefore(source);
            }

            if (target == TimeSpan.Zero)
            {
                Assert.Ignore("Every position the fixture is asked for is one it can land on exactly.");
            }

            FakeAudioRenderer audioRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            pipeline.Play();
            WaitFor(() => audioRenderer.SubmittedBuffers > 5, "the device has to have taken some audio first");

            int flushed = audioRenderer.FlushCount;
            SeekAndWait(pipeline, target, SeekMode.Precise);

            // The device is let go of what the old position gave it before anything of the new one is offered,
            // so from here on the list holds the new position's sound alone.
            WaitFor(() => audioRenderer.FlushCount > flushed, "the device was never let go of the position playback left");

            int marker = audioRenderer.SubmittedTimestamps.Count;

            WaitFor(() => audioRenderer.SubmittedTimestamps.Count > marker + 2, "the sound has to carry on after a precise seek");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(audioRenderer.SubmittedTimestamps.Skip(marker).All(timestamp => timestamp >= target), Is.True,
                    "a precise seek played sound from before the position it asked for");
                Assert.That(pipeline.Position, Is.GreaterThanOrEqualTo(target),
                    "the clock may not stand at a position before the one a precise seek asked for");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A source that refuses to be moved â€” saying so in advance, the way a live stream does, or by failing the
    /// seek itself. Neither is a failure of playback, and what each one leaves behind is what the two answers
    /// differ in: a refusal in advance is a command that could not be carried out, so nothing at all changes;
    /// a refusal when asked was accepted first, so the move is what paused playback, and the reader can only
    /// say so afterwards â€” a line, because the caller's answer has already been given.
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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForState(pipeline, MediaEngineState.Playing);

            OperationResult seeked = Await(pipeline.Seek(TimeSpan.FromSeconds(5)));

            using (Assert.EnterMultipleScope())
            {
                // A source that refuses in advance is refused without the reader ever being involved.
                Assert.That(seeked.IsSuccessful, Is.EqualTo(!refusesInAdvance), seeked.Message);

                // A source that will not be moved is not playback failing, either way â€” and a seek changes no state,
                // so what a refusal leaves behind is a line rather than a paused file:
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "a seek leaves the state as it found it");
                Assert.That(pipeline.Error, Is.Null);
            }

            if (!refusesInAdvance)
            {
                // The reader found out by trying, and a line is what it has to say it with.
                WaitForWarning(warnings);
            }

            // A moment later the state is still the one playback was in, rather than half way through a seek that
            // never happened.
            Thread.Sleep(100);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), pipeline.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A source that will not be moved is not a reason to leave the rest behind: every file is asked, so a refusal in
    /// one does not decide where another lands. A source left where it was may still be at its end, and the packet it
    /// answers with then reads as the end of the media rather than as the tail of a seek that never reached it â€” the
    /// file that could not be moved is a line, and the media goes where it was asked to.
    /// </summary>
    [Test]
    public void MovesEverySourceEvenWhenOneOfThemRefuses()
    {
        string refusing = SyntheticPath("refusing");
        string subtitles = SyntheticPath("subtitles");

        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(refusing, new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(3),
            RefusesToSeek = true,
        });
        sources.Add(subtitles, new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(3))],
        });

        FakeSubtitleRenderer subtitleRenderer = new();
        PlaybackPipeline pipeline = CreateMultiSourcePipeline(sources, subtitleRenderer);
        try
        {
            // The file that refuses is the one the picture comes from, so it is the first the reader reaches and the
            // file behind it is the one a refusal must not take with it.
            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(refusing))).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(subtitles))).IsSuccessful, Is.True);

            MediaInfo played = pipeline.Sources[0].Info;
            Assert.That(Select(pipeline, MediaStreamKind.Video, played.VideoStreams[0]).IsSuccessful, Is.True);
            SubtitleStreamInfo subtitle = pipeline.Sources[1].Info.Streams.OfType<SubtitleStreamInfo>().First();
            Assert.That(Select(pipeline, MediaStreamKind.Subtitle, subtitle).IsSuccessful, Is.True);

            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForState(pipeline, MediaEngineState.Playing);

            var target = TimeSpan.FromSeconds(1);
            Assert.That(Await(pipeline.Seek(target)).IsSuccessful, Is.True);

            WaitFor(() => sources.Opened[1].LastSeekPosition == target, "the file behind the one that refused to be moved");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(sources.Opened[0].LastSeekPosition, Is.EqualTo(target), "the source that refuses was asked");
                Assert.That(sources.Opened[1].LastSeekPosition, Is.EqualTo(target), "and the one behind it moved anyway");
            }
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// A precise seek whose first picture is *ahead* of the position it asked for â€” the ordinary case, since frames are
    /// not at every millisecond â€” has to start the clock. The frame that claims a position is not early: its moment is
    /// when the position becomes a fact. Without that rule the frame waits for a moment only the clock could bring and
    /// the clock waits for a claim only that frame could make, which is playback standing still with nothing to report
    /// (state `Playing`, no picture, no starvation). The sound here has nothing left after one second, so nothing else
    /// can claim the position.
    /// </summary>
    [Test]
    public void StartsPlaybackWhenTheFirstPictureOfAPositionIsAheadOfIt()
    {
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(3),
            AudioStopsAt = TimeSpan.FromSeconds(1),
        };

        FakeVideoRenderer videoRenderer = new();
        SyntheticSourceProviderFactory sources = new(script);
        PlaybackPipeline pipeline = CreatePipeline(
            videoRenderer,
            new FakeAudioRenderer { PlaysWhatItIsGiven = true },
            videoDecoders: new SyntheticVideoDecoderFactory(),
            sourceFactory: sources,
            audioDecoders: new SyntheticAudioDecoderFactory());

        try
        {
            Assert.That(Open(pipeline, SyntheticPath()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.Position > TimeSpan.FromSeconds(0.5), "playback to be under way");

            // Between two frames at thirty a second: the next one is twenty-three milliseconds past the request.
            var target = TimeSpan.FromMilliseconds(1510);
            Assert.That(Await(pipeline.Seek(target, SeekMode.Precise)).IsSuccessful, Is.True);

            long shown = pipeline.VideoChains[0].PresentedFrameCount;
            WaitFor(() => pipeline.Position > target, "a position whose first frame is ahead of it still has to run", 4000);
            WaitFor(() => pipeline.VideoChains[0].PresentedFrameCount > shown, "the picture carries on from where it was moved to");
            Assert.That(pipeline.StarvedFor, Is.Null, "nothing is behind: the picture simply had to claim the position");
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// A request past the end of the file as well: neither stream has anything at it, and the landing goes to the one
    /// that ends later â€” the sound here â€” because that is the closest anything can get to where the seek asked. The
    /// picture's last keyframe would be its end, sixteen seconds further back in a file whose picture stops early.
    /// </summary>
    [Test]
    public void LandsByWhicheverStreamEndsLaterWhenNeitherReachesThePosition()
    {
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(3),
            VideoStopsAt = TimeSpan.FromSeconds(1),
            VideoDuration = TimeSpan.FromSeconds(1),
            LandsOnKeyframes = true,
        };

        FakeVideoRenderer videoRenderer = new();
        SyntheticSourceProviderFactory sources = new(script);
        PlaybackPipeline pipeline = CreatePipeline(
            videoRenderer,
            new FakeAudioRenderer { PlaysWhatItIsGiven = true },
            videoDecoders: new SyntheticVideoDecoderFactory(),
            sourceFactory: sources,
            audioDecoders: new SyntheticAudioDecoderFactory());

        try
        {
            Assert.That(Open(pipeline, SyntheticPath()).IsSuccessful, Is.True);
            Assert.That(Select(pipeline, MediaStreamKind.Video, pipeline.Sources[0].Info.VideoStreams[0]).IsSuccessful, Is.True);
            Assert.That(Select(pipeline, MediaStreamKind.Audio, pipeline.Sources[0].Info.AudioStreams[0]).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.Position > TimeSpan.FromSeconds(0.3), "playback to be under way", 5000);

            var target = TimeSpan.FromSeconds(3.5);
            Assert.That(Await(pipeline.Seek(target, SeekMode.PreviousKeyframe)).IsSuccessful, Is.True);
            WaitFor(() => sources.Source?.LastSeekPosition == target, "the cursor to be asked to move", 5000);

            Assert.That(sources.Source!.LastSeekStreamIndex, Is.EqualTo(pipeline.Sources[0].Info.AudioStreams[0].Index),
                "the sound ends later, so it is what the landing is measured by");
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// A container that says nothing about the picture's length is not a container that says the picture has ended.
    /// The picture keeps the landing then, because landing by the sound when the picture still has keyframes would
    /// cost the decoder its lead-in and leave the picture waiting for the next keyframe â€” a gap on every seek, for a
    /// length nobody claimed.
    /// </summary>
    [Test]
    public void KeepsThePictureLandingWhenItsLengthIsNotDeclared()
    {
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(3),
            VideoDuration = TimeSpan.Zero,
            LandsOnKeyframes = true,
        };

        FakeVideoRenderer videoRenderer = new();
        SyntheticSourceProviderFactory sources = new(script);
        PlaybackPipeline pipeline = CreatePipeline(
            videoRenderer,
            new FakeAudioRenderer { PlaysWhatItIsGiven = true },
            videoDecoders: new SyntheticVideoDecoderFactory(),
            sourceFactory: sources,
            audioDecoders: new SyntheticAudioDecoderFactory());

        try
        {
            Assert.That(Open(pipeline, SyntheticPath()).IsSuccessful, Is.True);
            Assert.That(Select(pipeline, MediaStreamKind.Video, pipeline.Sources[0].Info.VideoStreams[0]).IsSuccessful, Is.True);
            Assert.That(Select(pipeline, MediaStreamKind.Audio, pipeline.Sources[0].Info.AudioStreams[0]).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.Position > TimeSpan.FromSeconds(0.3), "playback to be under way", 5000);

            var target = TimeSpan.FromSeconds(2);
            Assert.That(Await(pipeline.Seek(target, SeekMode.PreviousKeyframe)).IsSuccessful, Is.True);
            WaitFor(() => sources.Source?.LastSeekPosition == target, "the cursor to be asked to move", 5000);

            Assert.That(sources.Source!.LastSeekStreamIndex, Is.EqualTo(pipeline.Sources[0].Info.VideoStreams[0].Index),
                "an undeclared length is not an end, so the picture still lands the seek");
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// The other half of the rule above: while the picture still has media at the request it is what the landing is
    /// measured by, because its keyframes are the only ones there are and the media a fast seek lands on is the
    /// lead-in the decoder needs. Nothing about a seek changes for the ordinary case.
    /// </summary>
    [Test]
    public void LandsByThePictureWhileItStillHasSomethingThere()
    {
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(3),
            LandsOnKeyframes = true,
        };

        FakeVideoRenderer videoRenderer = new();
        SyntheticSourceProviderFactory sources = new(script);
        PlaybackPipeline pipeline = CreatePipeline(
            videoRenderer,
            new FakeAudioRenderer { PlaysWhatItIsGiven = true },
            videoDecoders: new SyntheticVideoDecoderFactory(),
            sourceFactory: sources,
            audioDecoders: new SyntheticAudioDecoderFactory());

        try
        {
            Assert.That(Open(pipeline, SyntheticPath()).IsSuccessful, Is.True);
            Assert.That(Select(pipeline, MediaStreamKind.Video, pipeline.Sources[0].Info.VideoStreams[0]).IsSuccessful, Is.True);
            Assert.That(Select(pipeline, MediaStreamKind.Audio, pipeline.Sources[0].Info.AudioStreams[0]).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.Position > TimeSpan.FromSeconds(0.3), "playback to be under way", 5000);

            var target = TimeSpan.FromSeconds(1.5);
            Assert.That(Await(pipeline.Seek(target, SeekMode.PreviousKeyframe)).IsSuccessful, Is.True);
            WaitFor(() => sources.Source?.LastSeekPosition == target, "the cursor to be asked to move", 5000);

            Assert.That(sources.Source!.LastSeekStreamIndex, Is.EqualTo(pipeline.Sources[0].Info.VideoStreams[0].Index));
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// A seek to a position only the sound reaches â€” the picture ended first â€” has to arrive there, and has to do it
    /// without being read from the picture's last frame. A keyframe belongs to the picture, so a landing measured by
    /// it falls back to its last one when the request is past it â€” one second in here â€” and everything after that
    /// would be read and thrown away to arrive at the request, with the picture's last frame shown as the position.
    /// The landing is measured by the sound instead, which has no keyframes and lands within a packet of the request,
    /// and a fast seek is still a fast seek: it resumes at that landing.
    /// </summary>
    [Test]
    public void SeeksToAPositionOnlyTheSoundReaches()
    {
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(3),
            VideoStopsAt = TimeSpan.FromSeconds(1),
            VideoDuration = TimeSpan.FromSeconds(1),
            LandsOnKeyframes = true,
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
            Assert.That(Open(pipeline, SyntheticPath()).IsSuccessful, Is.True);
            Assert.That(Select(pipeline, MediaStreamKind.Video, pipeline.Sources[0].Info.VideoStreams[0]).IsSuccessful, Is.True);
            Assert.That(Select(pipeline, MediaStreamKind.Audio, pipeline.Sources[0].Info.AudioStreams[0]).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.Position > TimeSpan.FromSeconds(0.3), "playback to be under way", 5000);

            // Paused, so that what the picture does about the new position is the only thing that moves: a still is
            // offered there, and it is the still of the landing â€” the picture's last frame â€” rather than of nothing.
            Assert.That(Await(pipeline.Pause()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.State == MediaEngineState.Paused, "the pipeline to stop");
            int pictures = videoRenderer.RenderedTimestamps.Count;

            var target = TimeSpan.FromSeconds(2);
            Assert.That(Await(pipeline.Seek(target, SeekMode.PreviousKeyframe)).IsSuccessful, Is.True);

            // Nothing to wait for when the answer is right, so the answer is given a moment to be wrong in.
            Thread.Sleep(300);

            int soundIndex = pipeline.Sources[0].Info.AudioStreams[0].Index;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(videoRenderer.RenderedTimestamps.Count, Is.EqualTo(pictures),
                    "the picture has nothing at the position, so there is no still of it to show");
                Assert.That(sources.Source?.LastSeekStreamIndex, Is.EqualTo(soundIndex),
                    "the landing is measured by the sound: by the picture it would fall back to its last keyframe, one second in");
            }

            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.Position >= target, "playback to stand where the seek asked, not where the picture ended", 3000);
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

}
