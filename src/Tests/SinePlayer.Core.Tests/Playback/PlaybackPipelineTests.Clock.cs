// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Diagnostics;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Engine;
using SinePlayer.Core.Playback.Hosting;

namespace SinePlayer.Core.Tests.Playback;

public partial class PlaybackPipelineTests
{
    /// <summary>
    /// A file longer than the read-ahead is what makes the reader wait at all: it fills the minimum, parks,
    /// and every block the sound plays brings it back. Nothing in the pipeline tells it that â€” the sound is
    /// the only thread that knows it took something â€” so this is what pins the wake from the consumer to the
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

            OperationResult opened = Open(pipeline, path);
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
    /// it â€” the picture is the only thread that knows it made room â€” so a missing wake here is a picture that
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

            OperationResult opened = Open(pipeline, path);
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
    /// The picture holds the clock until this position has something to show. Without it the sound starts
    /// the clock on its first block and the picture is left behind before it has produced a single frame â€”
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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
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
                Assert.That(pipeline.VideoChains[0].DroppedFrameCount, Is.Zero, "the first frames of a position are not late when the clock waited for them");
                Assert.That(pipeline.Position, Is.LessThan(TimeSpan.FromMilliseconds(300)), "and the picture starts at the start of the file, not past it");
            }

            WaitForPresentedFrames(pipeline, 3);
            Assert.That(pipeline.VideoChains[0].DroppedFrameCount, Is.Zero, "and nothing after it is dropped either");
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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            // Far enough in that the decoded tail is ahead of where the seek will go back to.
            WaitFor(() => pipeline.Position > TimeSpan.FromMilliseconds(500), "playback has to get past the position the seek goes back to");

            SeekAndWait(pipeline, TimeSpan.FromMilliseconds(100));

            // The new position's first decode takes four hundred milliseconds, and nothing may run in the
            // meantime â€” the old tail is not a fact about this position.
            WaitFor(() => pipeline.StarvedFor == "video", "a position that has produced nothing holds the clock", 150);
            WaitFor(() => !audioRenderer.ClockIsRunning, "the clock is held, so the sound is stopped with it");

            long shown = pipeline.VideoChains[0].PresentedFrameCount;
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
    /// the position playback has left â€” a value *ahead* of the new request â€” so a tail-based verdict finds the
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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.Position > TimeSpan.FromMilliseconds(500), "playback has to get past the position the seek goes back to");

            // Hold the decoder inside a decode, so its tail keeps the value of the position playback is about
            // to leave, and move the position underneath it.
            videoDecoders.Close();
            Assert.That(videoDecoders.WaitUntilDecoding(), Is.True, "the decoder has to be inside a decode");

            SeekAndWait(pipeline, TimeSpan.FromMilliseconds(100));

            WaitFor(() => pipeline.StarvedFor == "video", "a position that has produced nothing holds the clock", 300);
            WaitFor(() => !audioRenderer.ClockIsRunning, "the old position's tail is not a fact about this one");

            videoDecoders.Open();
            long shown = pipeline.VideoChains[0].PresentedFrameCount;
            WaitForPresentedFrames(pipeline, shown + 1);
            WaitFor(() => audioRenderer.ClockIsRunning, "the first frame of the position lets time pass");
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

            OperationResult opened = Open(pipeline, path);
            Assert.That(opened.IsSuccessful, Is.True, opened.Message);
            pipeline.Play();

            double worstLag = 0;
            long samplesHalfway = 0;
            bool heldForThePicture = false;
            for (int attempt = 0; attempt < 40; attempt++)
            {
                Thread.Sleep(50);
                worstLag = Math.Max(worstLag, pipeline.VideoChains[0].PictureLag.TotalMilliseconds);
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
                Assert.That(pipeline.VideoChains[0].PresentedFrameCount, Is.GreaterThan(0), "the picture keeps being shown");
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
    /// quiet for. What the hold needs is a wait that ends when the *verdict* can change â€” which, while the
    /// clock is still the sound's to claim, is not even a question about the media yet.
    /// </summary>
    [Test]
    public void HoldsTheClockBackWhileTheDecoderIsQuiet()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            // The sound's own first block is what claims the clock, and it is handed over on another thread â€”
            // so the claim is held back here until after this thread has had its say, which is the arrangement
            // in which nothing the picture is waiting for would ever make the verdict readable. The device
            // holding audio is what keeps the sound itself from being the reason time is stopped.
            FakeAudioRenderer audioRenderer = new() { SubmitDelayMilliseconds = 30, QueuedSampleCount = SampleRate };
            using PlaybackPipeline pipeline = CreatePipeline(
                new FakeVideoRenderer(),
                audioRenderer,
                new ThrottledVideoDecoderFactory { DecodeMilliseconds = 1500, FastPackets = 1 },
                audioDecoders: new PacedAudioDecoderFactory { DecodeMilliseconds = 150 });

            OperationResult opened = Open(pipeline, path);
            Assert.That(opened.IsSuccessful, Is.True, opened.Message);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForPresentedFrames(pipeline, 1);

            // The hold has to arrive while the decoder is still quiet: the threshold is 200 ms and the next
            // frame is a second and a half away, so this waits well inside the quiet and a wait that ends when
            // a frame does fails it â€” which is what the wait this replaces would do.
            WaitFor(() => pipeline.StarvedFor == "video", "the picture has nothing to show, so time has to be held", 800);

            // Held near the threshold rather than at whatever the clock had run to by the time a frame turned
            // up: a picture left behind for the whole of the decoder's quiet shows as a gap of about that
            // length between the clock and the last frame the picture showed.
            TimeSpan held = pipeline.Position;
            Assert.That(pipeline.VideoChains[0].PictureLag, Is.LessThan(TimeSpan.FromMilliseconds(700)),
                "the hold is what keeps the clock from running away from a picture that has nothing to show");

            // A held clock is a clock that does not move: the sound waits for the picture rather than leaving
            // it further behind, which is what reporting the verdict is for. It may still be corrected to the
            // device by the sync tolerance, which is a step of milliseconds rather than the clock running on.
            Thread.Sleep(300);
            Assert.That(pipeline.Position, Is.EqualTo(held).Within(TimeSpan.FromMilliseconds(20)),
                "a held clock does not move");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The cadence itself: the verdict is read at the top of the loop, and the thread is idle between
    /// iterations, so the wait has to end when the *clock* reaches the point at which a picture this far
    /// behind counts as starving â€” a wait that ends only when a frame does reports the starvation as many
    /// seconds late as the decoder was quiet.
    /// <para>
    /// A file with no sound is what makes this the only thing being tested: the clock is claimed by this
    /// thread as it shows the first frame that plays, so by the time the decoder goes quiet the verdict is
    /// judgeable and false â€” the clock is level with the picture â€” and nothing but the clock's own progress
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
                // arrives â€” so the frame is a presentation that claims the clock, not the paused position's
                // still, which claims nothing â€” and then quiet for 1300 ms with the clock level with it.
                new SlowFirstDecodeVideoDecoderFactory { FirstDecodeMilliseconds = 400, SubsequentDecodeMilliseconds = 1300 });

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitForPresentedFrames(pipeline, 1);

            // Without the cadence the verdict is only read when a frame arrives â€” and by then the decoder is
            // ahead again, so the hold never arrives at all.
            WaitFor(() => pipeline.StarvedFor == "video", "the clock has run past a picture that has nothing more to show", 1800);

            TimeSpan held = pipeline.Position;
            Assert.That(pipeline.VideoChains[0].PictureLag, Is.LessThan(TimeSpan.FromMilliseconds(700)),
                "the hold is what keeps the clock from running away from a picture that has nothing to show");

            Thread.Sleep(300);
            Assert.That(pipeline.Position, Is.EqualTo(held).Within(TimeSpan.FromMilliseconds(20)), "a held clock does not move, beyond the correction it is allowed to make to the device");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The clock is told what to do only when the answer has changed. Several things decide it in a row â€” a
    /// state change, then each starvation flag, or the reverse â€” and a clock told the same thing twice is a
    /// clock whose Start and Pause have to tolerate it, which the interface does not promise: a naÃ¯ve Start
    /// re-anchors from now, and a naÃ¯ve Pause records a stop the device never had.
    /// <para>
    /// The starvation hold is what makes the difference visible: with the picture behind, the clock has
    /// already been stopped by the hold, so a pause that arrives while it is held changes nothing about what
    /// the clock should do â€” and a pipeline that tells the clock anyway says "pause" twice for one fact.
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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitFor(() => pipeline.StarvedFor == "video", "the picture has to fall behind for the clock to be held");

            // Held: the clock is stopped because time must not pass while the picture catches up, and the
            // count is taken while that is the state of affairs. The device is the audio thread's to stop, so
            // it follows the verdict a beat later.
            WaitFor(() => !audioRenderer.ClockIsRunning, "a held clock is a stopped clock");
            int pauses = audioRenderer.ClockPauses;
            int starts = audioRenderer.ClockStarts;

            // A pause while the clock is already stopped â€” because it is *held*, not because time had started â€”
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
    /// holding what it was given, and that is being heard â€” so the clock is held only once the device is
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

    /// <summary>
    /// A seek has to let go of what the device still holds, and the device is the one part of the pipeline
    /// whose state no seek can rewrite from outside: it belongs to the audio thread, so that is where the
    /// move is taken up.
    /// <para>
    /// It matters twice over. What the device holds was produced for where playback used to be, so leaving
    /// it there plays a moment of the old position over the start of the new one. And because the clock
    /// counts what the device plays, a device that was never reset keeps counting from the old position's
    /// travel â€” a clock that jumps forward the moment it is re-anchored, and a picture that races to catch
    /// up with it. That is what "the video speeds up after a seek" was.
    /// </para>
    /// </summary>
    /// <summary>
    /// A device that keeps none of what it is given is indistinguishable, from the queue's side, from one that plays
    /// perfectly: it holds nothing and accepts everything, so the queue it is fed from never fills. Only the clock can
    /// tell them apart â€” the sound taken runs away from the sound played â€” and when it does, the pipeline stops handing
    /// over sound until the device has caught up, which is what keeps the lead, and with it the memory, bounded.
    /// </summary>
    [Test]
    public void HoldsSoundBackFromADeviceThatDoesNotPlayIt()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            // The default stand-in is exactly this device: it holds nothing and completes every hand-off at once.
            FakeAudioRenderer audioRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);
            WarningLog warnings = new(pipeline);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitFor(() => warnings.Messages.Length > 0, "the device to be found out");
            TimeSpan positionAtFallback = pipeline.Position;

            // Enough wall time for the sound to have been handed over several times over, if nothing held it back.
            for (int sample = 0; sample < 6; sample++)
            {
                Thread.Sleep(150);

                var submitted = TimeSpan.FromSeconds((double)audioRenderer.SubmittedSamples / SampleRate);
                TimeSpan lead = submitted - pipeline.Position;

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(lead, Is.LessThan(TimeSpan.FromSeconds(4)),
                        $"the sound runs at most the pipeline's patience ahead of the clock (at {pipeline.Position:mm\\:ss\\.fff}, submitted {submitted:mm\\:ss\\.fff})");
                    Assert.That(audioRenderer.SubmitsStarted, Is.GreaterThan(0));
                }
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(warnings.Messages, Has.Length.EqualTo(1), "a device is reported once, not once per iteration");
                Assert.That(pipeline.Position, Is.GreaterThan(positionAtFallback), "the position carries on at the clock's pace");
                Assert.That(pipeline.Error, Is.Null, "a device that does not play is not a failed playback");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "playback itself is still going");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The end of a file is played out by the stages that still hold media, and the queue is told about it
    /// rather than held by it: the queue every command is posted to has to stay free while the tail plays
    /// out, or a seek is answered when the file has finished instead of when it was asked for. The device is
    /// jammed here after it has taken the whole of the sound, which leaves the ending outstanding for as long
    /// as the sound has not been heard.
    /// <para>
    /// The file is two seconds long on purpose: handing over the whole of the sound is only a device keeping up
    /// while what it has been given is not far ahead of the position. A file longer than the pipeline's patience
    /// with a device that keeps nothing is <c>ReportsADeviceThatTakesSoundAndNeverPlaysIt</c>, a different case.
    /// </para>
    /// </summary>
    [Test]
    public void AnswersACommandWhileTheSoundHasNotPlayedOut()
    {
        string path = RawAudioVideoFixtureFile.Create(frameCount: 60);
        try
        {
            FakeAudioRenderer audioRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            // The fixture is 60 frames at 30 fps with 44.1 kHz sound, so a device that has been handed this
            // many samples has been handed the whole of the file's audio â€” and the reader, which is ahead of
            // the device everywhere else, has read the end of the file to do it.
            const int totalSamples = SampleRate * 60 / 30;
            WaitFor(() => audioRenderer.SubmittedSamples >= totalSamples, "the reader has to reach the end of the file");
            audioRenderer.QueuedSampleCount = SampleRate;
            audioRenderer.NoRoom = true;

            var askedAt = Stopwatch.StartNew();
            OperationResult seeked = Await(pipeline.Seek(TimeSpan.FromSeconds(1)));
            askedAt.Stop();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(seeked.IsSuccessful, Is.True, seeked.Message);
                Assert.That(askedAt.ElapsedMilliseconds, Is.LessThan(1000),
                    "a command must not wait for the end of the file to play out");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "the seek is answered at once and leaves playback playing, rather than being the end of a file");
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
    /// for a timeout â€” so the file ends when the device has played the last of it, at the moment it does.
    /// </summary>
    /// <summary>
    /// Playback speed is a property of the pipeline: the clock runs at the rate, and the sound is resampled so that a device
    /// rendering at its own rate keeps up with it. What is already in the device plays at the old speed until it drains, so a
    /// change neither flushes nor takes the clock back.
    /// </summary>
    [Test]
    public void RunsTheClockAtTheRateAndResamplesTheSound()
    {
        string path = RawAudioVideoFixtureFile.Create(frameCount: 60);
        try
        {
            FakeAudioRenderer audioRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            pipeline.AudioChains[0].SpeedAlgorithm = AudioSpeedAlgorithm.None;
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.Position > TimeSpan.FromMilliseconds(200), "playback to be under way", 5000);

            using (Assert.EnterMultipleScope())
            {
                pipeline.Rate = -2.0;
                Assert.That(pipeline.Rate, Is.EqualTo(1.0), "a rate below nothing is not a rate, and changes nothing");

                pipeline.Rate = 0.0;
                Assert.That(pipeline.Rate, Is.EqualTo(1.0), "nor is a rate of nothing");

                pipeline.Rate = double.NaN;
                Assert.That(pipeline.Rate, Is.EqualTo(1.0), "nor is one that means nothing");

                pipeline.Rate = 4.0;
                Assert.That(pipeline.Rate, Is.EqualTo(4.0), "what a host may ask for is the host's business, not the engine's");
            }

            int flushes = audioRenderer.FlushCount;
            pipeline.Rate = 2.0;
            TimeSpan before = pipeline.Position;

            var waited = Stopwatch.StartNew();
            WaitFor(() => pipeline.Position - before > TimeSpan.FromMilliseconds(400), "the clock to advance at the rate", 5000);
            waited.Stop();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(waited.ElapsedMilliseconds, Is.LessThan(400),
                    "400 ms of media has to take well under a second of wall time, or the clock is not running at twice the speed");
                Assert.That(audioRenderer.FlushCount, Is.EqualTo(flushes), "a rate change does not flush the device");
                Assert.That(pipeline.Position, Is.GreaterThanOrEqualTo(before), "and does not take the clock back");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The clock follows what the device is playing, and what the device is playing never goes backwards â€” at any rate. A
    /// rate set mid-play is a different matter: audio handed over before the change plays out at the old rate, and the
    /// clock is stepped back to it until it has, which is a transient this test is not about.
    /// </summary>
    [Test]
    [TestCase(0.5)]
    [TestCase(2.0)]
    public void KeepsTheClockGoingForwardWhileTheRateIsNotOne(double rate)
    {
        string path = CreateWaveFile(SampleRate * 6);
        try
        {
            FakeAudioRenderer audioRenderer = new() { QueuedSampleCount = SampleRate };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            pipeline.Rate = rate;
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.Position > TimeSpan.FromMilliseconds(400), "playback to be under way and settled", 5000);

            TimeSpan previous = pipeline.Position;
            double worst = 0;
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 1200)
            {
                TimeSpan now = pipeline.Position;
                worst = Math.Min(worst, (now - previous).TotalMilliseconds);
                previous = now;
                Thread.SpinWait(2000);
            }

            Assert.That(worst, Is.GreaterThan(-20.0),
                "the sound is the master, so the clock follows it rather than being stepped by how the sync is measured");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void LetsEachAudioChainChooseHowItIsSpeeded()
    {
        // The choice belongs to the chain, not to the pipeline: a second chain may be played at the same rate with its pitch
        // carried along while this one keeps it.
        using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), new FakeAudioRenderer());
        IAudioChain first = pipeline.AudioChains[0];
        IAudioChain second = Await(pipeline.AddAudioChain());

        first.SpeedAlgorithm = AudioSpeedAlgorithm.None;
        second.SpeedAlgorithm = AudioSpeedAlgorithm.WSOLA;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.SpeedAlgorithm, Is.EqualTo(AudioSpeedAlgorithm.None), "one chain's choice is its own");
            Assert.That(second.SpeedAlgorithm, Is.EqualTo(AudioSpeedAlgorithm.WSOLA));
        }
    }

    [Test]
    public void PlaysAtTheRateThroughItsStretchAlgorithm()
    {
        // The choice has to reach the sound the chain plays, not just a field: played at the same rate through each algorithm, the
        // same file reaches the device in the same number of frames, less whatever a window of stretch is still holding.
        string path = RawAudioVideoFixtureFile.Create(frameCount: 60);
        try
        {
            int resampled = SoundAt(path, AudioSpeedAlgorithm.None);
            int stretched = SoundAt(path, AudioSpeedAlgorithm.WSOLA);

            Assert.That(resampled, Is.GreaterThan(0));
            Assert.That(stretched, Is.EqualTo(resampled).Within(2000),
                "the same media in the same frames, times two");
        }
        finally
        {
            File.Delete(path);
        }

        static int SoundAt(string path, AudioSpeedAlgorithm algorithm)
        {
            FakeAudioRenderer audioRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);
            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);

            pipeline.AudioChains[0].SpeedAlgorithm = algorithm;
            pipeline.Rate = 2.0;
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitForPlaybackToFinish(pipeline);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended));
            return audioRenderer.SubmittedSamples;
        }
    }

    /// <summary>
    /// The other side of the same rule, which is what keeps it from being "never hold the clock": when the stream
    /// that went quiet is the only one left â€” nothing has been read past the clock by either stream â€” the picture is
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
            OperationResult opened = Open(pipeline, SyntheticPath());
            Assert.That(opened.IsSuccessful, Is.True, opened.Message);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitFor(
                () => pipeline.Position > TimeSpan.FromSeconds(0.9) && pipeline.StarvedFor is not null,
                "both streams run out at one second, and time has to stop with them",
                4000);

            // The verdict reaches the device a beat later â€” the device is the audio thread's to stop â€” and the clock
            // may step back by the sync tolerance on the way, so the hold is read once it has settled.
            Thread.Sleep(150);
            TimeSpan held = pipeline.Position;
            Thread.Sleep(300);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.Position, Is.EqualTo(held).Within(TimeSpan.FromMilliseconds(20)), "a held clock does not move, beyond the correction it is allowed to make to the device");
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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);

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
    /// A seek places the clock at the position that was asked for, and the media replaces it the moment the
    /// first of it plays (see <see cref="StartsWhereTheFileResumesRatherThanWhereTheSeekAsked"/>). Every
    /// decision the clock is made afterwards has to leave it where the media put it: a pause is such a
    /// decision, and placing the request again on the way would move time forward to a position nothing is
    /// playing â€” every frame of it late from then on, and the host reading a position nobody asked for.
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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            pipeline.Play();
            WaitForPresentedFrames(pipeline, 2);

            SeekAndWait(pipeline, target);

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
    /// The read-ahead is a length of time, so what the reader is held to is the media in the queues rather than a
    /// number of packets or samples. This measures the sound's half of that end to end: at the eight-second bound a
    /// thirty-second file is a quarter read when playback has only just begun, and a count of packets â€” or a bound
    /// that a stream can never reach â€” would have taken it all.
    /// </summary>
    [Test]
    public void DoesNotReadAnAudioOnlyFileAllAtOnce()
    {
        string path = CreateWaveFile(sampleCount: SampleRate * 30);
        try
        {
            // The device is full: it takes one block and then holds it, so the packet queue is the only thing the
            // reader can be paced by â€” and that queue only holds a reader back that reads faster than the sound is
            // decoded, which is why the decoder here is the slow half. A reader that ignored the queue would take the
            // whole file in the first moment.
            PacedAudioDecoderFactory decoders = new() { DecodeMilliseconds = 50 };
            FakeAudioRenderer audioRenderer = new() { NoRoom = true };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer, audioDecoders: decoders);

            OperationResult opened = Open(pipeline, path);
            Assert.That(opened.IsSuccessful, Is.True, opened.Message);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            // Long enough for the pre-buffer to be filled many times over, if it were not a bound at all.
            Thread.Sleep(800);
            int early = decoders.Decodes;

            // What the file is worth read from the file itself: playing a device that takes nothing to the end
            // would take the length of the film, so the total is counted rather than waited for.
            int total = CountPackets(path);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(total, Is.GreaterThan(4), "the file has to be read in several packets, or this measures nothing");
                Assert.That(early, Is.LessThan(total / 2), $"the reader ran ahead: {early} of {total} packets were read while the sound queue should have held it to about eight seconds");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The picture being off leaves one chain reading for, and a chain that has decoded everything it was handed has
    /// nothing to report: the reader's only account of "the read ahead is being played down" is the position moving on,
    /// so it has to look again by itself â€” waiting for a chain to say so froze playback on the last of what was read.
    /// <para>
    /// It takes as long as the read ahead is deep, because the freeze is on the far side of everything that was
    /// buffered when the picture went off: the test asks when the sound has got two seconds past that point.
    /// </para>
    /// </summary>
    [Test]
    public void KeepsReadingWhenTheOnlyChainLeftHasDecodedEverything()
    {
        string path = RawAudioVideoFixtureFile.Create(frameCount: 900);
        try
        {
            FakeAudioRenderer audioRenderer = new() { PlaysWhatItIsGiven = true };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.Position > TimeSpan.FromMilliseconds(300), "playback to be under way");

            var audio = (AudioChain)ChainOf(pipeline, MediaStreamKind.Audio);
            WaitFor(() => audio.PacketQueue.IsEmpty, "the sound to have decoded everything it was given");
            var readAhead = TimeSpan.FromTicks(audio.ReadTail.Ticks);

            Assert.That(Select(pipeline, MediaStreamKind.Video, null).IsSuccessful, Is.True);

            // Nothing but the sound is left to report a read, and it has nothing left to report: from here on, only the
            // reader looking again keeps the sound coming.
            WaitFor(
                () => pipeline.Position > readAhead + TimeSpan.FromSeconds(2),
                "the sound to play on past everything that had been read when the picture went off",
                30000);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A chain can be taken out while the others play. What keeps the clock goes with it, so another sound takes the
    /// clock over, and the one that is left carries on handing sound to its device without a pause.
    /// </summary>
    [Test]
    public void RemovesTheChainThatKeepsTheClockWhileTheOtherPlaysOn()
    {
        int baseline = PipelineThread.LiveCount;

        string firstPosition = SyntheticPath("leaving-sound");
        string secondPosition = SyntheticPath("staying-sound");

        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(firstPosition, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(10) });
        sources.Add(secondPosition, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(10) });

        FakeAudioRenderer leavingDevice = new();
        FakeAudioRenderer stayingDevice = new();
        PlaybackPipeline pipeline = new() { SourceFactory = sources };
        try
        {
            IAudioChain leaving = Await(pipeline.AddAudioChain());
            IAudioChain staying = Await(pipeline.AddAudioChain());

            leaving.Components.Register(new SyntheticAudioDecoderFactory());
            leaving.Components.Register(new FixedAudioRendererFactory(leavingDevice));
            staying.Components.Register(new SyntheticAudioDecoderFactory());
            staying.Components.Register(new FixedAudioRendererFactory(stayingDevice));

            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(firstPosition))).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(secondPosition))).IsSuccessful, Is.True);
            Assert.That(Await(leaving.SelectStream(pipeline.Sources[0].Info.AudioStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(staying.SelectStream(pipeline.Sources[1].Info.AudioStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitFor(() => leavingDevice.SubmittedBuffers > 1 && stayingDevice.SubmittedBuffers > 1, "both chains to be playing");
            Assert.That(leaving.OwnsClock, Is.True, "the chain that arrived first keeps the clock");

            // The reader and one for each sound.
            WaitFor(() => PipelineThread.LiveCount - baseline == 3, $"both chains to be running (was {PipelineThread.LiveCount - baseline})");

            long handedOver = stayingDevice.SubmittedBuffers;
            Assert.That(Await(pipeline.RemoveChain(leaving)).IsSuccessful, Is.True);
            Assert.That(pipeline.AudioChains, Is.EqualTo(new[] { staying }), "the removal is recorded at once");
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "removing a chain is not a stop");
                Assert.That(staying.OwnsClock, Is.True, "the clock falls to the sound that is left");
            }

            WaitFor(() => PipelineThread.LiveCount - baseline == 2, $"the sound that went to cost no thread (was {PipelineThread.LiveCount - baseline})");
            WaitFor(() => stayingDevice.SubmittedBuffers > handedOver, "the remaining chain to keep playing");
            Assert.That(Await(pipeline.Pause()).IsSuccessful, Is.True);
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

}
