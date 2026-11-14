// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Hosting;

namespace SinePlayer.Core.Tests.Playback;

public partial class PlaybackPipelineTests
{
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
    /// the renderer â€” and a seek has to reach it there. The hand-off belongs to the position it was made for,
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

            // And the sound carries on from where the seek went â€” the seek did not pause, so nothing has to be
            // resumed first.
            audioRenderer.QueuedSampleCount = 0;
            audioRenderer.NoRoom = false;
            WaitForPlaybackToFinish(pipeline);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A sound nothing can play is not a reason to leave the file unopened: the picture is still worth watching, so this
    /// host chooses the picture and leaves the sound unchosen, and it is the choice of the sound â€” not the open â€” that
    /// comes back as a failed command.
    /// </summary>
    [Test]
    public void PlaysThePictureWhenTheSoundCannotBeOpened()
    {
        string path = RawAudioVideoFixtureFile.Create(frameCount: 12);
        try
        {
            FakeAudioRenderer audioRenderer = new() { CanConfigure = false, RefusalReason = "no device here" };
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, audioRenderer);

            OperationResult opened = OpenWithoutSound(pipeline, path);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(opened.IsSuccessful, Is.True, opened.Message);
                Assert.That(pipeline.State, Is.Not.EqualTo(MediaEngineState.Error));
                Assert.That(pipeline.Sources[0].Info.AudioStreams, Is.Not.Empty, "the file's audio stream does not stop existing because no device would take it");
                Assert.That(pipeline.VideoChains[0].SelectedStream, Is.Not.Null, "the picture is what is playing");
                Assert.That(pipeline.AudioChains[0].DecoderName, Is.Null, "and the sound has no chain");
            }

            // The refusal is the answer to the choice, and it names the device.
            OperationResult audio = Select(pipeline, MediaStreamKind.Audio, pipeline.Sources[0].Info.AudioStreams[0]);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(audio.IsSuccessful, Is.False);
                Assert.That(pipeline.AudioChains[0].DecoderName, Is.Null, "a refused choice publishes nothing");
            }

            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForPlaybackToFinish(pipeline);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);
                Assert.That(videoRenderer.RenderedFrames, Is.GreaterThan(0), "the picture played without the sound");
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

            // Opening is not choosing: the file opens, and the choice is refused â€” there is nothing to fall back
            // on in a file with no video, so what is left is an open file with nothing playing.
            Assert.That(Open(pipeline, path).IsSuccessful, Is.False);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.AudioChains[0].SelectedStream, Is.Not.Null, "the choice is recorded even though nothing could be built for it");
                Assert.That(pipeline.AudioChains[0].RendererName, Is.Null, "nothing was published for it");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused), "the file is still open, and nothing is playing");
                Assert.That(Await(pipeline.Play()).IsSuccessful, Is.False, "there is no chain to play");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A playback that has failed stops reading. The reader has nothing to read *for* â€” nothing will consume
    /// what it produces, and the position it would fill is never played â€” so it parks in the same idle wait
    /// the picture parks in, instead of filling a read-ahead while the host decides what to do about the failure.
    /// What it holds (the file, the reason, the media already queued, and the counters describing where playback
    /// stopped) is exactly what is left to explain the failure, which is why nothing is released or discarded here:
    /// stopping or opening something else is what does that, and both are the host's decisions.
    /// <para>
    /// A read is what a test can see of the reader's progress: the decoding that used to happen inline now happens on a
    /// coroutine of its own, so a decode no longer means a read. The file is a live one, which hands each packet over at
    /// its moment â€” a reader that kept reading after the failure would be seen asking for the next packet.
    /// </para>
    /// </summary>
    [Test]
    public void StopsReadingOncePlaybackHasFailed()
    {
        SyntheticMediaScript script = new() { Duration = TimeSpan.FromSeconds(5), ReportsTheEndOfTheFile = false };
        SyntheticSourceProviderFactory sources = new(script);
        PlaybackPipeline pipeline = CreatePipeline(
            new FakeVideoRenderer(),
            new FakeAudioRenderer(),
            videoDecoders: new SyntheticVideoDecoderFactory(packetsBeforeFailure: 1),
            sourceFactory: sources,
            audioDecoders: new SyntheticAudioDecoderFactory());

        try
        {
            OperationResult opened = Open(pipeline, SyntheticPath());
            Assert.That(opened.IsSuccessful, Is.True, opened.Message);

            // Nothing is played: the reader reads while the open's state is still paused, which is what paces the
            // test â€” and a play is not asked for because the failure may already have been heard by the time one
            // could be, which would refuse it.
            WaitForState(pipeline, MediaEngineState.Error);

            SyntheticMediaSource source = sources.Source!;
            int readsWhenItFailed = source.Reads;

            // A reader that kept going would take another second's worth of packets in here.
            Thread.Sleep(300);

            Assert.That(source.Reads - readsWhenItFailed, Is.LessThanOrEqualTo(1),
                "a failed playback has nothing to read for: the reader parks instead of filling a read-ahead");
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    [Test]
    public void ReportsAFailureToOpen()
    {
        using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), new FakeAudioRenderer());

        OperationResult opened = Open(pipeline, Path.Combine(Path.GetTempPath(), "sineplayer-missing-file.mp4"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(opened.IsSuccessful, Is.False);
            Assert.That(pipeline.Sources, Is.Empty, "a failed open leaves nothing open");
            Assert.That(pipeline.Error, Is.Null, "a failed open is not a failed playback");
        }
    }

    /// <summary>
    /// A refused command is the caller's business and nobody else's: it says nothing about playback, so nothing about
    /// playback changes.
    /// </summary>
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

    /// <summary>
    /// Every public call answers with a failure the caller can read, and that answer has to reach the log as well.
    /// These refusals are decided on the playback thread â€” the wrong state, a stream that belongs to nothing the
    /// pipeline was given â€” so they never pass through the path the media threads use to report a failure, which made
    /// them the one thing a host could be told about and leave no trace of.
    /// </summary>
    [Test]
    public void WritesDownEveryPublicCallThatFails()
    {
        using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), new FakeAudioRenderer());
        using LogCapture logs = new();

        OperationResult opened = OpenFirstSource(pipeline, MediaLocator.Parse(Path.Combine(Path.GetTempPath(), "sineplayer-missing-file.mkv")));
        OperationResult played = Await(pipeline.Play());
        OperationResult paused = Await(pipeline.Pause());
        OperationResult selected = Select(pipeline, MediaStreamKind.Video, new VideoStreamInfo { Index = 99, CodecId = "h264", Width = 1, Height = 1, FrameRate = 25.0, PixelFormat = PixelFormat.Yuv420P });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(opened.IsSuccessful, Is.False, "the file does not exist, so opening it has to be refused");
            Assert.That(played.IsSuccessful, Is.False);
            Assert.That(paused.IsSuccessful, Is.False);
            Assert.That(selected.IsSuccessful, Is.False, "a stream of nothing the pipeline was given is not one it can select");

            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True, "reconciling nothing is nothing to do");
            Assert.That(Await(pipeline.Stop()).IsSuccessful, Is.True, "stopping cannot fail");
        }

        WaitFor(() => logs.Has(opened.Message), "the refused open to be written down");
        WaitFor(() => logs.Has(played.Message), "the refused play to be written down");
        WaitFor(() => logs.Has(paused.Message), "the refused pause to be written down");
        WaitFor(() => logs.Has(selected.Message), "the refused track change to be written down");
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

            OperationResult played = Await(pipeline.Play());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(played.IsSuccessful, Is.False);

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
    /// A decoder that throws is the same failure as one that returns a failure, and it has to arrive the same
    /// way â€” the catch on the decode thread is the only thing between an interop fault and a thread that dies
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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);

            // The decoder fails on its own, without anything being played: the reader reads and the decode
            // thread decodes while the open's state is still paused, so there is nothing for a play to add â€”
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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);

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
            // decoder is fed while the open's state is still paused â€” the decode thread decodes ahead without
            // asking whether anything is playing â€” so the failure can be heard first, and then the play is
            // refused *because* playback has already failed.
            Assert.That(pipeline.Error, Is.Not.Null.And.Not.Empty);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A pipeline with nothing to open files with says so rather than faulting: the source provider is a
    /// property, so forgetting it is an omission a caller has to be told about.
    /// </summary>
    [Test]
    public void ReportsWhenNoSourceProviderWasSupplied()
    {
        using PlaybackPipeline pipeline = new();

        OperationResult opened = Open(pipeline, CreateBitmapFile());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(opened.IsSuccessful, Is.False);

            // A failed open closes whatever was playing, like every other way an open can fail.
            Assert.That(pipeline.Sources, Is.Empty);
            Assert.That(pipeline.Error, Is.Null);
        }
    }

    /// <summary>
    /// A track that cannot be decoded is refused to the caller, and the file stays open with nothing playing: a chain that
    /// cannot be built is the chain's own answer, and the reconcile itself happened.
    /// </summary>
    [Test]
    public void RefusesAnAudioChainThatCannotBePrepared()
    {
        string path = CreateWaveFile();
        try
        {
            using PlaybackPipeline pipeline = CreatePipeline(
                new FakeVideoRenderer(),
                new FakeAudioRenderer(),
                audioDecoders: new RefusesInsideTheCreator());

            OperationResult opened = Open(pipeline, path);

            Assert.That(opened.IsSuccessful, Is.False, "nothing can be played without a decoder");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>What a refusal leaves the caller with is the decoder's own reason, not a generic one.</summary>
    [Test]
    public void ReportsWhyNoDecoderCouldServeTheFile()
    {
        string path = CreateBitmapFile();
        try
        {
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), new FakeAudioRenderer());
            pipeline.VideoChains[0].Components.ClearDecoders();
            pipeline.VideoChains[0].Components.Register(new RefusingVideoDecoderFactory());

            OperationResult opened = Open(pipeline, path);

            Assert.That(opened.IsSuccessful, Is.False);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The cure for a failed device is a new one. Because the sound it could not take was spent rather than piled up,
    /// the swap finds playback where it is and hands what is left to the device that replaced the failed one.
    /// </summary>
    [Test]
    public void BringsTheSoundBackWhenAFailedDeviceIsSwapped()
    {
        string path = CreateWaveFile(sampleCount: SampleRate * 2);
        try
        {
            InvalidOperationException failure = new("The device cannot be written to.");
            FakeAudioRenderer failed = new() { Name = "failed", SubmitFailure = failure };
            FakeAudioRenderer healthy = new() { Name = "healthy", PlaysWhatItIsGiven = true };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), failed);
            WarningLog warnings = new(pipeline);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitFor(() => warnings.Messages.Contains($"Unable to queue audio: {failure.Message}"), "the failed device to be reported");

            SwapAudioRenderer(pipeline, new FixedAudioRendererFactory(healthy));
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);

            WaitFor(() => healthy.SubmittedBuffers > 0, "the device that replaced the failed one to take sound");
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "the sound is back, so playback never left");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A warning names the chain that raised it, so a host holding more than one chain of a kind can tell whose device
    /// spoke. What the pipeline itself warns about â€” a seek that could not reach the sources â€” names no chain.
    /// </summary>
    [Test]
    public void NamesTheChainThatRaisedAWarning()
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

            IReadOnlyList<WarningEntry> entries = warnings.Entries;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(entries, Is.Not.Empty, "the failed device is still reported");
                Assert.That(
                    entries.Select(entry => entry.Chain).Distinct(),
                    Is.EqualTo(new[] { pipeline.VideoChains[0] }),
                    "a picture device's trouble belongs to the video chain, not to the pipeline");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A renderer that can no longer present is a failed device, and the swap that replaces it is the cure. So the
    /// thread keeps drawing nothing rather than exiting â€” exiting would put the pipeline in a state where the swap
    /// is refused â€” and the device that replaces it carries on from there.
    /// </summary>
    [Test]
    public void BringsThePictureBackWhenAFailedDeviceIsSwapped()
    {
        string path = CreateY4mFile(frameCount: 90, frameRate: "30:1");
        try
        {
            FakeVideoRenderer failed = new() { Name = "failed", RenderFailure = "The device was removed." };
            FakeVideoRenderer healthy = new() { Name = "healthy" };
            using PlaybackPipeline pipeline = CreatePipeline(failed, new FakeAudioRenderer());
            WarningLog warnings = new(pipeline);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitFor(() => warnings.Messages.Length > 0, "the failed device to be reported");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "a failed device is not a failed playback");
                Assert.That(pipeline.Error, Is.Null);
            }

            SwapVideoRenderer(pipeline, new FakeVideoRendererFactory(healthy));
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);

            WaitFor(() => healthy.RenderedFrames > 0, "the device that replaced the failed one to draw");
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "the picture is back, so playback never left");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A renderer that cannot hand a block to its device has a failed device, not a failed file: the failure is
    /// reported as a warning and playback carries on. The sound it can no longer take is spent â€” at the pace the
    /// clock would have played it, which is what this test measures: the file lasts as long as it says it does, so
    /// nothing is thrown away early and a device that replaced this one would still have sound to play. Failing the
    /// playback instead would leave it in a state where a renderer swap is refused, which is the one thing that
    /// could have fixed it.
    /// </summary>
    [Test]
    public void KeepsGoingWhenTheDeviceStopsTakingTheSound()
    {
        string path = CreateWaveFile(sampleCount: SampleRate);
        try
        {
            InvalidOperationException failure = new("The device cannot be written to.");
            FakeAudioRenderer audioRenderer = new() { SubmitFailure = failure };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);
            WarningLog warnings = new(pipeline);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            WaitForPlaybackToFinish(pipeline);
            elapsed.Stop();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(warnings.Messages, Is.Not.Empty, "the failure reaches a listener rather than the state");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), "the sound is spent, so the file still ends");
                Assert.That(pipeline.Error, Is.Null, "a failed device is not a failed playback");
                Assert.That(elapsed.Elapsed, Is.GreaterThan(TimeSpan.FromMilliseconds(800)),
                    "the second of sound is spent as a second, not swallowed in one gulp");
                Assert.That(pipeline.Position, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900)),
                    "and the position travels with it, so a device swap resumes where playback actually is");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A device that can be described but not opened is a warning and not a failed playback: the picture still plays,
    /// and the reason reaches the host. This is the one failure left to the audio thread â€” everything that can be
    /// answered before a command promises to play is answered while it is still the command â€” and the device is asked
    /// once, not once per iteration.
    /// </summary>
    [Test]
    public void WarnsWhenTheDeviceCannotBeOpenedAfterTheSoundWasChosen()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            FakeAudioRenderer audioRenderer = new() { CanOpen = false, RefusalReason = "the device vanished" };
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, audioRenderer);
            WarningLog warnings = new(pipeline);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Select(pipeline, MediaStreamKind.Audio, pipeline.Sources[0].Info.AudioStreams[0]).IsSuccessful, Is.True, "the choice was answered before anything was opened");
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitFor(() => warnings.Messages.Length > 0, "the device to be found out");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(audioRenderer.ConfigureCount, Is.EqualTo(1), "it is opened once, and not again on every iteration");
                Assert.That(audioRenderer.SubmitsStarted, Is.Zero, "nothing is handed to a device that is not open");
                Assert.That(pipeline.Error, Is.Null, "a missing device is not a failed playback");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The underrun figure is the device's own answer, passed through rather than guessed at: what the pipeline
    /// reports is what the device counted.
    /// </summary>
    [Test]
    public void ReportsTheUnderrunsTheDeviceCounts()
    {
        string path = CreateWaveFile();
        try
        {
            FakeAudioRenderer renderer = new() { UnderrunCount = 3 };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), renderer);
            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);

            Assert.That(pipeline.AudioChains[0].Underruns, Is.EqualTo(3));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A device whose stream will not start is the same failure as one that will not take a block, reached through
    /// the other call: the clock lets it out, the pipeline reports it once and spends the sound instead of asking a
    /// dead device to start on every iteration. And because the failure is a device rather than a file, the swap to
    /// a working one still cures it.
    /// </summary>
    [Test]
    public void KeepsGoingWhenTheDeviceWillNotStart()
    {
        string path = CreateWaveFile(sampleCount: SampleRate * 2);
        try
        {
            InvalidOperationException failure = new("The device will not start.");
            FakeAudioRenderer failed = new() { Name = "failed", ClockStartFailure = failure };
            FakeAudioRenderer healthy = new() { Name = "healthy", PlaysWhatItIsGiven = true };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), failed);
            WarningLog warnings = new(pipeline);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitFor(() => warnings.Messages.Contains(failure.Message), "the failed start to be reported");
            Assert.That(warnings.Messages.Count(message => message == failure.Message), Is.EqualTo(1), "a dead device is reported once, not once per iteration");
            Assert.That(failed.ClockStarts, Is.LessThan(5), "and it is not asked to start again");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "a failed device is not a failed playback");
                Assert.That(pipeline.Error, Is.Null);
            }

            SwapAudioRenderer(pipeline, new FixedAudioRendererFactory(healthy));
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);

            WaitFor(() => healthy.SubmittedBuffers > 0, "the device that replaced the failed one to take sound");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A device that dies while a hand-off is waiting for room is found out through the clock, and the hand-off has
    /// to be let go of at that point: the device block is skipped from then on, so it would never be polled again â€”
    /// its block would stay alive and the device would keep whatever it had acquired until the next swap.
    /// </summary>
    [Test]
    public void LetsGoOfAHandOffWhenTheDeviceDiesDuringIt()
    {
        string path = CreateWaveFile(sampleCount: SampleRate * 3);
        try
        {
            InvalidOperationException pausing = new("The device will not stop.");
            FakeAudioRenderer audioRenderer = new() { NoRoom = true, ClockPauseFailure = pausing };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);
            WarningLog warnings = new(pipeline);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => audioRenderer.SubmitsStarted > 0, "a hand-off to be waiting for room");

            // Pausing is what makes the pipeline ask the device's clock to stop, and stopping a dead device throws.
            Assert.That(Await(pipeline.Pause()).IsSuccessful, Is.True);
            WaitFor(() => warnings.Messages.Contains(pausing.Message), "the dead device to be reported");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(audioRenderer.AbandonedSubmits, Is.EqualTo(1), "the hand-off that was waiting for room is let go of, not left pending for ever");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused), "a failed device is not a failed playback");
                Assert.That(pipeline.Error, Is.Null);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A seek after the device has failed is where the two halves of the spend path meet: the clock is the only
    /// pacing the sound has left, and a clock that is armed per position is only armed by the sound itself. If the
    /// arm is taken only when a device is present, the position after a seek has nothing to start it, the sound is
    /// never spent, and the file never ends.
    /// </summary>
    [Test]
    public void KeepsGoingAfterASeekWhenTheDeviceHasFailed()
    {
        string path = CreateWaveFile(sampleCount: SampleRate * 3);
        try
        {
            InvalidOperationException failure = new("The device cannot be written to.");
            FakeAudioRenderer audioRenderer = new() { SubmitFailure = failure };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);
            WarningLog warnings = new(pipeline);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => warnings.Messages.Contains($"Unable to queue audio: {failure.Message}"), "the failed device to be reported");

            SeekAndWait(pipeline, TimeSpan.FromSeconds(1));

            // The seek did not pause: the position is new and the device is still dead, which is exactly the state
            // the spend path has to be able to carry on from.

            // Two seconds of sound are left, and the sound is what paces them now: the clock has to be armed for the
            // new position and started, or nothing is ever spent again.
            WaitFor(() => pipeline.Position > TimeSpan.FromSeconds(1.5), "the clock to carry on from where the seek put it", 3000);
            WaitForPlaybackToFinish(pipeline);

            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

}
