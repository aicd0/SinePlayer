// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Hosting;

namespace SinePlayer.Core.Tests.Playback;

public partial class PlaybackPipelineTests
{
    [Test]
    public void EndsWhenTheDeviceHasPlayedOutTheSound()
    {
        string path = RawAudioVideoFixtureFile.Create(frameCount: 60);
        try
        {
            FakeAudioRenderer audioRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            const int totalSamples = SampleRate * 60 / 30;
            WaitFor(() => audioRenderer.SubmittedSamples >= totalSamples, "the reader has to reach the end of the file");

            // Now that the whole sound has been handed over, the device holds audio and takes none of it,
            // and the seek puts playback near the end so the picture has almost nothing left to show: what
            // holds the ending up is the sound and nothing else.
            audioRenderer.QueuedSampleCount = SampleRate;
            audioRenderer.NoRoom = true;
            Assert.That(Await(pipeline.Seek(TimeSpan.FromSeconds(1.8))).IsSuccessful, Is.True);
            Thread.Sleep(200);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "a sound that has not played out is not an ending");

            // The seek did not pause, so playback is already on the road to the end: the ending is earned once the
            // device has played the last of the sound out.

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
    /// A file whose picture outlasts its sound is the ordinary shape â€” the container's duration is the longest
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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitForPlaybackToFinish(pipeline);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);
                Assert.That(pipeline.VideoChains[0].PresentedFrameCount, Is.GreaterThan(0), "the picture is shown, including its tail");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The same shape the other way round: a sound that runs on after the picture has ended. The picture has nothing
    /// left here, and the hold it would otherwise take â€” "the picture is behind the clock" â€” is a hold on the clock
    /// the rest of the sound still needs, so playback would stop dead at the moment the picture ran out. What tells
    /// the two apart is what the reader has already taken in: nothing more of the picture at this position, and
    /// plenty of the sound (Â§8).
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

            OperationResult opened = Open(pipeline, path);
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
    /// behind (Â§8). A hold taken for the picture here stops the clock, and the sound with it, for good.
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
            OperationResult opened = Open(pipeline, SyntheticPath());
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
    /// A file whose sound has no chain at all â€” no decoder offered, so no device either â€” still ends. The ending
    /// asks the file whether it has sound, and then waits for the sound's *stage* to say it is over: the stage that
    /// would have played it exists regardless (the audio render loop runs for every open), and it walks its queue to
    /// the end marker even with nothing to play through, so "the file has sound" and "something will report the
    /// sound's end" are the same answer here. A predicate taken from the components instead would have hung: there
    /// is no audio component to ask.
    /// </summary>
    [Test]
    public void EndsWhenTheSoundHasNoChainToPlayItThrough()
    {
        string path = RawAudioVideoFixtureFile.Create(frameCount: 30);
        try
        {
            using PlaybackPipeline pipeline = CreatePipeline(p =>
            {
                p.VideoChains[0].Components.Register(new FFmpegVideoDecoderFactory());
                p.VideoChains[0].Components.Register(new FakeVideoRendererFactory(new FakeVideoRenderer()));

                // The file has sound, and nothing registered here can decode or play it.
            });

            // The picture is what this host can play, so it is what it chooses; the file still has sound, and that is
            // what the ending has to account for.
            Assert.That(OpenWithoutSound(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitForState(pipeline, MediaEngineState.Ended);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A file that has played out ends even if the user pauses on the last of it. The ending is the stages
    /// saying they have nothing left, not a decision about the state, so a pause that lands while the last
    /// word is being said does not hold the file open â€” and nothing has to ask again for the file to be
    /// allowed to finish.
    /// <para>
    /// The pause is taken from inside the sound's own question to the device, which is the moment the word
    /// turns on: that is the only way this case can be *named* instead of raced for, and it is also the
    /// honest description of it â€” the user paused while the sound was finding out that the device had
    /// played the last of it out.
    /// </para>
    /// </summary>
    [Test]
    public void EndsWhilePausedWhenTheFileHasPlayedOut()
    {
        string path = CreateWaveFile();
        try
        {
            // The device still holds audio â€” the sound has nothing left to hand over, and the only thing
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
            // comes back. The answer is then "yes, it has" â€” and that is the ending, with the file paused.
            OperationResult? paused = null;
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

    /// <summary>
    /// A file whose decoder holds its frames back until it is drained â€” which is what a codec with output
    /// delay does, and why an end of stream has to produce a tail at all â€” runs to its end and shows the frame
    /// that only the drain could release.
    /// <para>
    /// The drain is asked for when the packet stream ends and not when a poll happens to find the queue empty:
    /// the end is an entry the decoder is handed, after the last packet of it. The test is here because that
    /// is the one thing a pipeline test can see of the difference â€” with the entry, "nothing more is coming"
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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
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

    /// <summary>
    /// The other half of the same rule: a picture device that has failed must not hold the end of the file back.
    /// The frames it can no longer take are spent, so the end marker still reaches the render thread and the
    /// playback still ends â€” a picture that has nothing left to show is not a picture that never ends.
    /// </summary>
    [Test]
    public void EndsWhenThePictureDeviceNeverComesBack()
    {
        string path = CreateY4mFile(frameCount: 15, frameRate: "30:1");
        try
        {
            FakeVideoRenderer failed = new() { Name = "failed", RenderFailure = "The device was removed." };
            using PlaybackPipeline pipeline = CreatePipeline(failed, new FakeAudioRenderer());
            WarningLog warnings = new(pipeline);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitForPlaybackToFinish(pipeline);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(warnings.Messages, Is.Not.Empty, "the failure is still reported");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), "the frames were spent, so the file still ended");
                Assert.That(pipeline.Error, Is.Null, "a failed device is not a failed playback");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The end of a file is not the place to find out that playback failed. A decoder that cannot give up its tail
    /// has lost the tail, and that is all: the stream is already over, so the end still goes out and the failure is
    /// reported rather than becoming the state of a playback that played. A hardware decoder whose device is gone by
    /// the time the file ends is exactly this.
    /// </summary>
    [Test]
    public void EndsWhenTheDecoderCannotGiveUpItsTail()
    {
        string path = CreateY4mFile(frameCount: 15, frameRate: "30:1");
        try
        {
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer(), videoDecoders: new BrokenDrainVideoDecoderFactory(returnsNothing: false));
            WarningLog warnings = new(pipeline);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitForPlaybackToFinish(pipeline);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(warnings.Messages, Is.Not.Empty);
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), "the stream was over; losing its tail does not unend it");
                Assert.That(pipeline.Error, Is.Null, "a failed tail is not a failed playback");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The same moment, with the other kind of broken drain: a decoder that says it succeeded and hands back nothing
    /// at all. That used to be walked as a list, which is an exception on a thread with no one to catch it, so this
    /// test failing as a crash is the point of it.
    /// </summary>
    [Test]
    public void SurvivesADrainThatHandsBackNoTailAtAll()
    {
        string path = CreateY4mFile(frameCount: 15, frameRate: "30:1");
        try
        {
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer(), videoDecoders: new BrokenDrainVideoDecoderFactory(returnsNothing: true));
            WarningLog warnings = new(pipeline);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitForPlaybackToFinish(pipeline);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(warnings.Messages, Is.Not.Empty);
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
    /// The sound's half of the same moment: an audio decoder that cannot give up its tail must not take the end of
    /// the file with it. The reader publishes both end markers right after this drain, so failing there would leave
    /// the picture and the sound waiting for a stream that is already over.
    /// </summary>
    [Test]
    public void EndsWhenTheSoundDecoderCannotGiveUpItsTail()
    {
        string path = RawAudioVideoFixtureFile.Create(frameCount: 12);
        try
        {
            FakeAudioRenderer audioRenderer = new() { PlaysWhatItIsGiven = true };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer, audioDecoders: new BrokenDrainAudioDecoderFactory());
            WarningLog warnings = new(pipeline);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitForPlaybackToFinish(pipeline);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(warnings.Messages, Is.Not.Empty);
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), "the stream was over; losing its tail does not unend it");
                Assert.That(pipeline.Error, Is.Null);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Playback ends when what is selected has nothing left, not when a particular file does: the shorter file added
    /// second is the one playing here, and it is its end that ends playback. The pipeline has no opinion about which
    /// file is "the media" â€” that is the host's, and it does not appear in what the pipeline reports.
    /// </summary>
    [Test]
    public void EndsWhenTheSelectedTracksHaveNothingLeft()
    {
        string longFile = SyntheticPath("long");
        string shortFile = SyntheticPath("short");

        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(longFile, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(3) });
        sources.Add(shortFile, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(1) });

        FakeVideoRenderer videoRenderer = new();
        FakeSubtitleRenderer subtitleRenderer = new();
        PlaybackPipeline pipeline = CreateMultiSourcePipeline(sources, subtitleRenderer, videoRenderer);
        try
        {
            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(longFile))).IsSuccessful, Is.True);

            OperationResult<MediaSource> added = Await(pipeline.AddSource(MediaLocator.Parse(shortFile)));
            Assert.That(added.IsSuccessful, Is.True, added.Message);

            // The picture of the shorter file, and nothing of the longer one.
            Assert.That(Select(pipeline, MediaStreamKind.Video, added.Result.Info.VideoStreams[0]).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitForPlaybackToFinish(pipeline);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended));
                Assert.That(pipeline.Position, Is.LessThanOrEqualTo(TimeSpan.FromSeconds(1.2)), "playback stopped where the file that is playing ends, not where the other one does");
            }
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

}
