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
    /// A track switch is not a pause and not a seek to anywhere else: the pipeline keeps playing from where it is,
    /// and only the chain of the kind that was switched is rebuilt â€” the picture is not restarted for a change of
    /// sound.
    /// </summary>
    [Test]
    public void SelectsTheSecondAudioTrackWithoutLeavingPlayback()
    {
        string path = RawAudioVideoFixtureFile.CreateMultiTrack();
        try
        {
            FakeVideoRenderer videoRenderer = new();
            FakeAudioRenderer audioRenderer = new() { PlaysWhatItIsGiven = true };
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, audioRenderer);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => audioRenderer.SubmittedBuffers > 2, "playback to be under way");

            AudioStreamInfo second = pipeline.Sources[0].Info.AudioStreams[1];
            TimeSpan position = pipeline.Position;
            int submitted = audioRenderer.SubmittedBuffers;
            int flushed = audioRenderer.FlushCount;
            int frames = videoRenderer.RenderedFrames;

            OperationResult selected = Select(pipeline, MediaStreamKind.Audio, second);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(selected.IsSuccessful, Is.True, selected.Message);
                Assert.That(pipeline.AudioChains[0].SelectedStream?.Index, Is.EqualTo(second.Index));
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "changing a track is not a pause");
                Assert.That(pipeline.Position, Is.GreaterThanOrEqualTo(position - TrackSwitchSlack), "the switch resumes where playback stands, not at the start of the file");
            }

            // The device is configured by the audio thread, which adopts the published chain when it gets to it: a switch
            // reports that the new chain was published, not that a device has already been built from it.
            WaitFor(() => audioRenderer.ConfiguredStream?.SampleRate == second.SampleRate, "the device to be configured for the stream that was asked for");
            WaitFor(() => audioRenderer.SubmittedBuffers > submitted, "the new track to be handed sound");
            WaitFor(() => audioRenderer.FlushCount > flushed, "the device to be restarted for the new track");

            // The picture is another chain: it is neither rebuilt nor restarted. Its frames arrive on their own
            // cadence, so this is waited for rather than asked once â€” the claim is that the picture keeps moving,
            // not that it has already moved by the time the sound did.
            int rendered = videoRenderer.RenderedFrames;
            WaitFor(() => videoRenderer.RenderedFrames > rendered, "the picture to keep moving while the sound changes track");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The same for the picture: the sound is not touched, and the frames that follow are the new stream's.</summary>
    [Test]
    public void SelectsTheSecondVideoTrackAndKeepsThePictureMoving()
    {
        string path = RawAudioVideoFixtureFile.CreateMultiTrack();
        try
        {
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer { PlaysWhatItIsGiven = true });

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForPresentedFrames(pipeline, 2);

            VideoStreamInfo second = pipeline.Sources[0].Info.VideoStreams[1];
            TimeSpan position = pipeline.Position;
            Assert.That(second.Width, Is.EqualTo(RawAudioVideoFixtureFile.SecondWidth), "the fixture's second picture is the smaller one");

            OperationResult selected = Select(pipeline, MediaStreamKind.Video, second);
            Assert.That(selected.IsSuccessful, Is.True, selected.Message);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.VideoChains[0].SelectedStream?.Index, Is.EqualTo(second.Index));
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing));
                Assert.That(pipeline.Position, Is.GreaterThanOrEqualTo(position - TrackSwitchSlack), "the switch resumes where playback stands, not at the start of the file");
            }

            // As with the sound: the renderer is configured for the new chain by the thread that adopts it.
            WaitFor(() => videoRenderer.ConfiguredStream?.Width == second.Width, "the renderer to be configured for the stream that was asked for");

            int marker = videoRenderer.RenderedWidths.Count;
            WaitForPresentedFrames(pipeline, pipeline.VideoChains[0].PresentedFrameCount + 2);

            IReadOnlyList<int> widths = videoRenderer.RenderedWidths;
            Assert.That(widths.Skip(marker), Is.Not.Empty.And.All.EqualTo(second.Width), "the frames after the switch are the new stream's, and the old stream's are not shown");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Asking for the track that is already playing is not a change: nothing is rebuilt, nothing restarts.</summary>
    [Test]
    public void SelectingTheTrackThatIsPlayingChangesNothing()
    {
        string path = RawAudioVideoFixtureFile.CreateMultiTrack();
        try
        {
            FakeAudioRenderer audioRenderer = new() { PlaysWhatItIsGiven = true };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => audioRenderer.SubmittedBuffers > 2, "playback to be under way");

            int flushed = audioRenderer.FlushCount;
            AudioStreamInfo playing = pipeline.AudioChains[0].SelectedStream!;

            OperationResult selected = Select(pipeline, MediaStreamKind.Audio, playing);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(selected.IsSuccessful, Is.True, selected.Message);
                Assert.That(audioRenderer.FlushCount, Is.EqualTo(flushed), "nothing is restarted for the track that is already playing");
                Assert.That(pipeline.AudioChains[0].SelectedStream?.Index, Is.EqualTo(playing.Index));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>What cannot be selected is refused before anything moves, and playback keeps the track it had.</summary>
    [Test]
    public void RefusesATrackItCannotPlay()
    {
        string path = RawAudioVideoFixtureFile.CreateMultiTrack();
        try
        {
            FakeAudioRenderer audioRenderer = new() { PlaysWhatItIsGiven = true };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => audioRenderer.SubmittedBuffers > 2, "playback to be under way");

            int flushed = audioRenderer.FlushCount;
            AudioStreamInfo playing = pipeline.AudioChains[0].SelectedStream!;

            using (Assert.EnterMultipleScope())
            {
                // A track is known by the instance the pipeline handed out: a copy of one, or a track from a file it was
                // never given, belongs to nothing it can select.
                Assert.That(Select(pipeline, MediaStreamKind.Subtitle, new SubtitleStreamInfo { Index = 0, CodecId = "subrip" }).IsSuccessful, Is.False, "a track of a file it was never given belongs to nothing it can select");
                Assert.That(Select(pipeline, MediaStreamKind.Video, new VideoStreamInfo { Index = 99, CodecId = "h264", Width = 1, Height = 1, FrameRate = 25.0, PixelFormat = PixelFormat.Yuv420P }).IsSuccessful, Is.False, "and neither does a copy of one");
                Assert.That(Select(pipeline, MediaStreamKind.Subtitle, playing).IsSuccessful, Is.False, "a stream of the other kind is not this kind's");
                Assert.That(Select(pipeline, MediaStreamKind.Video, playing).IsSuccessful, Is.False, "a stream of the other kind is not this kind's");

                Assert.That(pipeline.AudioChains[0].SelectedStream?.Index, Is.EqualTo(playing.Index), "a refusal leaves the track that was playing");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing));
                Assert.That(audioRenderer.FlushCount, Is.EqualTo(flushed), "a refusal restarts nothing");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A track no decoder can take is refused, and the one playing keeps playing â€” including when another chain is rebuilt in
    /// the same reconcile. The source holder describes every selection at once, so a chain that publishes it while another
    /// kind's selection could not be built replaces the picture that is playing with one nothing can decode.
    /// </summary>
    [Test]
    public void KeepsThePlayingTrackWhenTheNewOneCannotBeDecoded()
    {
        string path = RawAudioVideoFixtureFile.CreateMultiTrack();
        try
        {
            RefusingIndexVideoDecoderFactory refusing = new();
            using PlaybackPipeline pipeline = CreatePipeline(p =>
            {
                p.VideoChains[0].Components.Register(refusing);
                p.VideoChains[0].Components.Register(new FakeVideoRendererFactory(new FakeVideoRenderer()));
                p.AudioChains[0].Components.Register(new FFmpegAudioDecoderFactory());
                p.AudioChains[0].Components.Register(new FixedAudioRendererFactory(new FakeAudioRenderer { Name = "first", PlaysWhatItIsGiven = true }));
            });

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForPresentedFrames(pipeline, 2);

            VideoStreamInfo second = pipeline.Sources[0].Info.VideoStreams[1];
            refusing.RefusedIndex = second.Index;   // the index the container gave it, not the position in the list
            long presented = pipeline.VideoChains[0].PresentedFrameCount;

            // The sound is given a new device in the same reconcile as the picture's new track, so the audio chain has
            // something of its own to build while the video chain cannot build what was chosen.
            Assert.That(Await(pipeline.VideoChains[0].SelectStream(second)).IsSuccessful, Is.True);
            SwapAudioRenderer(pipeline, new FixedAudioRendererFactory(new FakeAudioRenderer { Name = "second", PlaysWhatItIsGiven = true }));
            OperationResult<ReconcileResult> reconciled = Await(pipeline.Reconcile());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(reconciled.Result[pipeline.VideoChains[0]].IsSuccessful, Is.False, "a stream no decoder can take is the caller's failure to see");
                Assert.That(reconciled.Result[pipeline.AudioChains[0]].IsSuccessful, Is.True, "the sound has a device of its own to build");
                Assert.That(pipeline.VideoChains[0].SelectedStream?.Index, Is.EqualTo(second.Index), "the choice is recorded, refused or not");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "the picture that is playing is untouched");
            }

            WaitForPresentedFrames(pipeline, presented + 2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Opening a file builds nothing and reads nothing: no decoder, no device, no pre-roll. Which tracks to play is the
    /// host's choice, and it is made from what the file holds â€” so the work that used to happen on open waits for the
    /// choice, and a file opened onto a track menu costs nothing but the container.
    /// </summary>
    [Test]
    public void OpeningAFileBuildsNothingUntilATrackIsChosen()
    {
        string path = RawAudioVideoFixtureFile.Create(frameCount: 30);
        try
        {
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer { PlaysWhatItIsGiven = true });

            Assert.That(OpenFirstSource(pipeline, MediaLocator.Parse(path)).IsSuccessful, Is.True);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.Sources[0].Info.VideoStreams, Is.Not.Empty, "the file can be read, which is how a host chooses");
                Assert.That(pipeline.VideoChains[0].SelectedStream, Is.Null, "nothing is chosen for the host");
                Assert.That(pipeline.AudioChains[0].SelectedStream, Is.Null);
                Assert.That(pipeline.VideoChains[0].DecoderName, Is.Null, "no decoder was created");
                Assert.That(pipeline.AudioChains[0].DecoderName, Is.Null);
                Assert.That(pipeline.VideoChains[0].RendererName, Is.Null, "no device was opened");
                Assert.That(pipeline.AudioChains[0].RendererName, Is.Null);
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused));
            }

            Thread.Sleep(150);
            Assert.That(pipeline.VideoChains[0].PresentedFrameCount, Is.Zero, "nothing was decoded, so nothing was shown");
            Assert.That(videoRenderer.RenderedFrames, Is.Zero, "and no thread was reading for a picture nobody asked for");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Play before a choice is refused rather than played into an empty pipeline: with nothing chosen the clock would
    /// run, the position would advance and no stage would ever say a word.
    /// </summary>
    [Test]
    public void PlaysTheFileOnlyOnceATrackIsChosen()
    {
        string path = RawAudioVideoFixtureFile.Create(frameCount: 30);
        try
        {
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), new FakeAudioRenderer { PlaysWhatItIsGiven = true });
            Assert.That(OpenFirstSource(pipeline, MediaLocator.Parse(path)).IsSuccessful, Is.True);

            OperationResult refused = Await(pipeline.Play());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(refused.IsSuccessful, Is.False);
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused), "a refused play is not a state change");
                Assert.That(pipeline.Position, Is.EqualTo(TimeSpan.Zero));
            }

            Assert.That(SelectDefaultStreams(pipeline).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.Position > TimeSpan.FromMilliseconds(200), "playback to be under way");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The point of choosing before playing: a file whose second track is the one wanted is never read or decoded for
    /// its first. The picture that reaches the renderer is the chosen stream's, and the shape of that stream says so.
    /// </summary>
    [Test]
    public void PlaysTheTrackThatWasChosenRatherThanTheFirstOne()
    {
        string path = RawAudioVideoFixtureFile.CreateMultiTrack();
        try
        {
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer { PlaysWhatItIsGiven = true });

            Assert.That(OpenFirstSource(pipeline, MediaLocator.Parse(path)).IsSuccessful, Is.True);

            VideoStreamInfo second = pipeline.Sources[0].Info.VideoStreams[1];
            AudioStreamInfo audio = pipeline.Sources[0].Info.AudioStreams[0];
            Assert.That(second.Width, Is.EqualTo(RawAudioVideoFixtureFile.SecondWidth), "the second picture is the smaller one");

            Assert.That(Select(pipeline, MediaStreamKind.Video, second).IsSuccessful, Is.True);
            OperationResult chosen = Select(pipeline, MediaStreamKind.Audio, audio);
            Assert.That(chosen.IsSuccessful, Is.True, chosen.Message);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitForPresentedFrames(pipeline, 2);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.VideoChains[0].SelectedStream?.Index, Is.EqualTo(second.Index));
                Assert.That(videoRenderer.ConfiguredStream?.Width, Is.EqualTo(second.Width), "the renderer was configured for the chosen stream");
                Assert.That(videoRenderer.RenderedWidths, Is.Not.Empty.And.All.EqualTo(second.Width), "every frame shown is the chosen stream's");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A kind can be turned off, and only that kind moves: the picture's chain is let go of, its device closed, and the
    /// sound carries on playing â€” the selection is one thing per kind, so leaving one out leaves the other untouched.
    /// </summary>
    [Test]
    public void TurningOneKindOffKeepsTheOtherPlaying()
    {
        string path = RawAudioVideoFixtureFile.Create(frameCount: 300);
        try
        {
            FakeVideoRenderer videoRenderer = new();
            FakeAudioRenderer audioRenderer = new() { PlaysWhatItIsGiven = true };
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, audioRenderer);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForPresentedFrames(pipeline, 2);
            WaitFor(() => audioRenderer.SubmittedBuffers > 2, "the sound to be playing");

            int submitted = audioRenderer.SubmittedBuffers;
            int rendered = videoRenderer.RenderedFrames;
            OperationResult turnedOff = Select(pipeline, MediaStreamKind.Video, null);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(turnedOff.IsSuccessful, Is.True, turnedOff.Message);
                Assert.That(pipeline.VideoChains[0].SelectedStream, Is.Null, "the kind is off");
                Assert.That(pipeline.AudioChains[0].SelectedStream, Is.Not.Null, "the other kind is untouched");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "turning a kind off is not a pause while one is left");
            }

            WaitFor(() => pipeline.VideoChains[0].RendererName is null, "the video chain to be let go of", 5000);
            WaitFor(() => audioRenderer.SubmittedBuffers > submitted, "the sound to keep playing without the picture");

            // The surface belongs to the host, so a renderer that is let go of empties it: the last frame standing
            // there for ever, with nothing behind it, is a picture of a file that is not playing any more.
            WaitFor(() => videoRenderer.ClearedSurfaces > 0, "the last frame to be given back", 5000);
            Thread.Sleep(200);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(videoRenderer.ClearedSurfaces, Is.EqualTo(1), "the surface is emptied once, not per iteration");
                Assert.That(videoRenderer.RenderedFrames, Is.EqualTo(rendered), "and no frame is drawn after the picture is off");
                Assert.That(videoRenderer.DisposeCount, Is.EqualTo(1), "the renderer is still let go of after it has cleared");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Choosing another video track is not the picture ending: the new chain draws, so the surface is not emptied
    /// between the two â€” the same reason a replaced renderer is not cleared.
    /// </summary>
    [Test]
    public void KeepsTheSurfaceWhenAnotherVideoTrackIsChosen()
    {
        string path = RawAudioVideoFixtureFile.CreateMultiTrack();
        try
        {
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer { PlaysWhatItIsGiven = true });

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Select(pipeline, MediaStreamKind.Video, pipeline.Sources[0].Info.VideoStreams[0]).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => videoRenderer.RenderedFrames > 2, "playback to be under way", 5000);

            VideoStreamInfo second = pipeline.Sources[0].Info.VideoStreams[1];
            Assert.That(Select(pipeline, MediaStreamKind.Video, second).IsSuccessful, Is.True);
            WaitFor(() => pipeline.VideoChains[0].SelectedStream?.Index == second.Index, "the other track to be playing", 5000);

            Assert.That(videoRenderer.ClearedSurfaces, Is.Zero, "a new track draws over the last frame rather than emptying it");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Turning every kind off leaves what an opened file is: open, with nothing to read for. Only a command moves the
    /// state, so playback stays in the state it was in â€” what is gone is the media that state describes, which is why a
    /// play is refused and why choosing a track again carries on from where playback stood rather than restarting.
    /// </summary>
    [Test]
    public void TurningEveryKindOffStopsReadingAndChoosingAgainCarriesOn()
    {
        string path = RawAudioVideoFixtureFile.Create(frameCount: 300);
        try
        {
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), new FakeAudioRenderer { PlaysWhatItIsGiven = true });

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.Position > TimeSpan.FromMilliseconds(300), "playback to be under way");

            TimeSpan position = pipeline.Position;
            Assert.That(Select(pipeline, MediaStreamKind.Video, null).IsSuccessful, Is.True);
            OperationResult turnedOff = Select(pipeline, MediaStreamKind.Audio, null);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(turnedOff.IsSuccessful, Is.True, turnedOff.Message);
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "only a command changes the state");
                Assert.That(Await(pipeline.Play()).IsSuccessful, Is.False, "a file with nothing chosen does not play");
            }

            VideoStreamInfo video = pipeline.Sources[0].Info.VideoStreams[0];
            AudioStreamInfo audio = pipeline.Sources[0].Info.AudioStreams[0];
            Assert.That(Select(pipeline, MediaStreamKind.Video, video).IsSuccessful, Is.True);
            Assert.That(Select(pipeline, MediaStreamKind.Audio, audio).IsSuccessful, Is.True);

            // Everything already read was for the selection that was turned off, so the read restarts â€” from the
            // position playback stands at, not from the beginning of the file.
            WaitFor(() => pipeline.Position > position, "playback to carry on from where it stood");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Turning off a kind that is already off is not a change: nothing is published and nothing restarts.</summary>
    [Test]
    public void TurningOffAKindThatIsAlreadyOffChangesNothing()
    {
        // Ten seconds of media: what is asserted is that the sound keeps coming and nothing restarts, so the file
        // must not be able to run out in the middle of the test on a loaded machine.
        string path = RawAudioVideoFixtureFile.Create(frameCount: 300);
        try
        {
            FakeAudioRenderer audioRenderer = new() { PlaysWhatItIsGiven = true };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => audioRenderer.SubmittedBuffers > 2, "playback to be under way");

            int flushedBefore = audioRenderer.FlushCount;
            Assert.That(Select(pipeline, MediaStreamKind.Video, null).IsSuccessful, Is.True);

            // The device is let go of the position playback left as the epoch moves, which is a moment after the
            // answer; from there on nothing may flush it again.
            WaitFor(() => audioRenderer.FlushCount > flushedBefore, "the device to be let go of the position playback left");
            int flushed = audioRenderer.FlushCount;
            int submitted = audioRenderer.SubmittedBuffers;

            OperationResult again = Select(pipeline, MediaStreamKind.Video, null);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(again.IsSuccessful, Is.True, again.Message);
                Assert.That(pipeline.VideoChains[0].SelectedStream, Is.Null);
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing));
            }

            WaitFor(() => audioRenderer.SubmittedBuffers > submitted, "the sound to be playing still");
            Assert.That(audioRenderer.FlushCount, Is.EqualTo(flushed), "a second turn-off restarts nothing");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Choosing both kinds in one step is one publication and one answer â€” the shape a host uses to say what to play.</summary>
    [Test]
    public void ChoosesBothKindsInOneStep()
    {
        string path = RawAudioVideoFixtureFile.CreateMultiTrack(frameCount: 30);
        try
        {
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), new FakeAudioRenderer { PlaysWhatItIsGiven = true });
            Assert.That(OpenFirstSource(pipeline, MediaLocator.Parse(path)).IsSuccessful, Is.True);

            VideoStreamInfo video = pipeline.Sources[0].Info.VideoStreams[1];
            AudioStreamInfo audio = pipeline.Sources[0].Info.AudioStreams[1];

            OperationResult chosen = Select(pipeline, MediaStreamKind.Video, video);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(chosen.IsSuccessful, Is.True, chosen.Message);
                Assert.That(pipeline.VideoChains[0].SelectedStream?.Index, Is.EqualTo(video.Index));
            }

            Assert.That(Select(pipeline, MediaStreamKind.Audio, audio).IsSuccessful, Is.True);
            Assert.That(pipeline.AudioChains[0].SelectedStream?.Index, Is.EqualTo(audio.Index));

            // The owners adopt what was published, which is a moment after the answer.
            WaitFor(() => pipeline.VideoChains[0].DecoderName is not null, "both chains were built");
            WaitFor(() => pipeline.AudioChains[0].DecoderName is not null, "both chains were built");

            // Naming nothing at all is how both kinds are switched off, and it leaves the file open with nothing to
            // play â€” the state an opened file is in.
            Assert.That(Select(pipeline, MediaStreamKind.Video, null).IsSuccessful, Is.True);
            OperationResult nothing = Select(pipeline, MediaStreamKind.Audio, null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(nothing.IsSuccessful, Is.True, nothing.Message);
                Assert.That(pipeline.VideoChains[0].SelectedStream, Is.Null);
                Assert.That(pipeline.AudioChains[0].SelectedStream, Is.Null);
                Assert.That(Await(pipeline.Play()).IsSuccessful, Is.False, "there is nothing to play");
            }

            // The chains are let go of by the threads that held them, so this is waited for as well.
            WaitFor(() => pipeline.VideoChains[0].DecoderName is null, "the video chain to be let go of");
            WaitFor(() => pipeline.AudioChains[0].DecoderName is null, "the audio chain to be let go of");
        }
        finally
        {
            File.Delete(path);
        }
    }

}
