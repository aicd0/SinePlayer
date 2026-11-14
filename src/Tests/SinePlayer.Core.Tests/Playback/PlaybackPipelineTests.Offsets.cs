// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Hosting;

namespace SinePlayer.Core.Tests.Playback;

public partial class PlaybackPipelineTests
{
    /// <summary>
    /// Moving every track earlier moves where reading starts later, and the file is moved there even though what it needs
    /// was already delivered: leaving it would have the reader and the decoders go through everything in between to reach
    /// the new start. The clock itself does not move.
    /// </summary>
    [Test]
    public void MovesTheReadStartWithTheTracksItServes()
    {
        SyntheticSourceProviderFactory sources = new(new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(3),
            KeyFrameInterval = 30,
            LandsOnKeyframes = true,
        });

        FakeVideoRenderer videoRenderer = new();
        using PlaybackPipeline pipeline = CreatePipeline(
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

            // Both the picture and the sound move, so the earliest content anybody needs is 200 ms further on.
            var advance = TimeSpan.FromMilliseconds(-200);
            pipeline.VideoChains[0].Offset = advance;
            pipeline.AudioChains[0].Offset = advance;

            WaitFor(() => sources.Source?.LastSeekPosition is TimeSpan position && position > TimeSpan.FromMilliseconds(200),
                "the file to be read from where the shifted tracks need it", 5000);

            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), pipeline.Error);
            Assert.That(pipeline.VideoChains[0].Offset, Is.EqualTo(advance));
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>
    /// Only a change of where reading starts is carried out: moving the picture while the sound stays where it is leaves the
    /// earliest content where it was, so nothing changes for the reader and playback never stops for the correction.
    /// </summary>
    [Test]
    public void DoesNotMoveTheReadStartWhenATrackStillNeedsItEarlier()
    {
        SyntheticSourceProviderFactory sources = new(new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(3),
            KeyFrameInterval = 30,
            LandsOnKeyframes = true,
        });

        FakeVideoRenderer videoRenderer = new();
        using PlaybackPipeline pipeline = CreatePipeline(
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

            // Reading has to have started before it can be said to stay where it is, or the seek that starts it would be
            // taken for the correction having moved it.
            WaitFor(() => sources.Source?.LastSeekPosition is not null, "reading to have started", 5000);

            TimeSpan? asked = sources.Source?.LastSeekPosition;
            IVideoChain chain = pipeline.VideoChains[0];
            TimeSpan before = pipeline.Position;
            int framesBefore = videoRenderer.RenderedFrames;

            chain.Offset = TimeSpan.FromMilliseconds(-200);

            // The sound still needs its content from the clock itself, so reading starts where it already did.
            WaitFor(() => pipeline.Position > before, "playback to carry on", 3000);
            WaitFor(() => videoRenderer.RenderedFrames > framesBefore + 3, "frames to keep arriving", 5000);
            Assert.That(sources.Source?.LastSeekPosition, Is.EqualTo(asked), "the read start was already early enough");
            Assert.That(chain.Offset, Is.EqualTo(TimeSpan.FromMilliseconds(-200)));
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing));
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>
    /// Moving a track later asks for content from before where reading started, which cannot be served from what is here, so
    /// the file is gone back for â€” at the position playing now, so the clock does not move with it.
    /// </summary>
    [Test]
    public void GoesBackForATrackMovedPastWhereReadingStarted()
    {
        SyntheticSourceProviderFactory sources = new(new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(60),
            KeyFrameInterval = 30,
            LandsOnKeyframes = true,
        });

        FakeVideoRenderer videoRenderer = new();
        using PlaybackPipeline pipeline = CreatePipeline(
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
            WaitFor(() => pipeline.Position > TimeSpan.FromSeconds(0.2), "playback to be under way", 5000);

            // Reading starts at 20 s, and then the picture is delayed by 5 s, so the content it needs starts at 15 s â€” before
            // anything that was read.
            var start = TimeSpan.FromSeconds(20);
            Assert.That(Await(pipeline.Seek(start, SeekMode.Precise)).IsSuccessful, Is.True);
            WaitFor(() => sources.Source?.LastSeekPosition == start, "the file to be where reading starts", 5000);
            WaitFor(() => pipeline.Position > start + TimeSpan.FromMilliseconds(200), "playback to be under way again", 5000);

            IVideoChain chain = pipeline.VideoChains[0];
            chain.Offset = TimeSpan.FromSeconds(5);

            WaitFor(() => sources.Source?.LastSeekPosition is TimeSpan position && position < start - TimeSpan.FromSeconds(4),
                "the file to be read from five seconds before where reading started", 5000);

            Assert.That(pipeline.Position, Is.GreaterThan(start), "the clock stayed where it was playing");
            Assert.That(chain.Offset, Is.EqualTo(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>
    /// A delay larger than the read-ahead ceiling must not freeze the reader. The chain that is furthest along reports a margin
    /// inflated by its shift, so a ceiling taken on that margin alone is satisfied from the first packet and the file is never
    /// read again â€” the clock then runs on into a sound with nothing behind it. The ceiling has to be raised by the spread of the
    /// shifts, which puts it back on the chain with the least in hand.
    /// </summary>
    [Test]
    public void KeepsReadingWhenAPictureIsDelayedPastTheCeiling()
    {
        SyntheticSourceProviderFactory sources = new(new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(60),
            KeyFrameInterval = 30,
            LandsOnKeyframes = true,
        });

        FakeVideoRenderer videoRenderer = new();
        FakeAudioRenderer audioRenderer = new();
        using PlaybackPipeline pipeline = CreatePipeline(
            videoRenderer,
            audioRenderer,
            videoDecoders: new SyntheticVideoDecoderFactory(),
            sourceFactory: sources,
            audioDecoders: new SyntheticAudioDecoderFactory());

        try
        {
            Assert.That(Open(pipeline, SyntheticPath()).IsSuccessful, Is.True);
            Assert.That(Select(pipeline, MediaStreamKind.Video, pipeline.Sources[0].Info.VideoStreams[0]).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.Position > TimeSpan.FromSeconds(0.5), "playback to be under way", 5000);

            // Twice the ceiling: a margin read on the delayed chain alone would be over the limit after one packet.
            var delay = TimeSpan.FromSeconds(20);
            pipeline.VideoChains[0].Offset = delay;

            int submitted = audioRenderer.SubmittedTimestamps.Length;

            // What matters is that reading did not park: the sound has to keep arriving, whatever the clock did when the shift
            // was taken (it can be carried by the sound, which makes the position jump rather than advance).
            WaitFor(() => audioRenderer.SubmittedTimestamps.Length > submitted + 8,
                "the sound to keep arriving, which it cannot do if the reader parked", 10000);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), pipeline.Error);
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>
    /// A track moved later needs content from before what reading has reached â€” content it may already have dropped â€” so the file
    /// is read again for it even where where-reading-starts does not change, which is the case here: the picture is already far
    /// enough ahead that the sound moving later stays within what it pins. What the re-read must not do is move the start itself.
    /// </summary>
    [Test]
    public void ReadsAgainForATrackMovedLaterWithinWhatTheStartAlreadyReaches()
    {
        SyntheticSourceProviderFactory sources = new(new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(60),
            KeyFrameInterval = 30,
            LandsOnKeyframes = true,
        });

        FakeVideoRenderer videoRenderer = new();
        using PlaybackPipeline pipeline = CreatePipeline(
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
            WaitFor(() => pipeline.Position > TimeSpan.FromMilliseconds(500), "playback to be under way", 5000);

            // The picture held back 800 ms pins where reading starts that much early; that start is what the sound has to stay
            // inside when it moves later.
            var picture = TimeSpan.FromMilliseconds(800);
            pipeline.VideoChains[0].Offset = picture;
            WaitFor(() => sources.Source?.LastSeekPosition is TimeSpan landed && landed < pipeline.Position - TimeSpan.FromMilliseconds(700),
                "the file to be read from where the delayed picture needs it", 5000);
            TimeSpan? start = sources.Source?.LastSeekPosition;
            int seeks = sources.Source?.SeekCount ?? 0;

            // Half of that: the sound is moved later, but the read start stays exactly where the picture put it.
            pipeline.AudioChains[0].Offset = TimeSpan.FromMilliseconds(400);

            WaitFor(() => (sources.Source?.SeekCount ?? 0) > seeks, "the file to be read again for the sound", 5000);
            Assert.That(sources.Source?.LastSeekPosition, Is.EqualTo(start).Within(TimeSpan.FromMilliseconds(500)),
                "the start itself did not move, the clock having gone on a little between the two publishes");
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), pipeline.Error);
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>
    /// The shift is added to the track's own times, so a delayed track presents content older than the clock by that much
    /// while the clock itself, the position a host shows, does not move.
    /// </summary>
    [Test]
    public void ShiftsThePresentedTimestampWithoutMovingTheClock()
    {
        SyntheticSourceProviderFactory sources = new(new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(40),
            KeyFrameInterval = 30,
            LandsOnKeyframes = true,
        });

        FakeVideoRenderer videoRenderer = new();
        using PlaybackPipeline pipeline = CreatePipeline(
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
            WaitFor(() => pipeline.Position > TimeSpan.FromSeconds(0.5), "playback to be under way", 5000);

            IVideoChain chain = pipeline.VideoChains[0];
            var delay = TimeSpan.FromMilliseconds(400);
            long before = chain.PresentedFrameCount;
            chain.Offset = delay;

            // Frames presented after the shift are the ones that can be wrong about it, so the wait is for those rather than
            // for the state as it was when the shift arrived.
            WaitFor(() => chain.PresentedFrameCount > before + 3, "the picture after the shift to be up", 4000);

            Assert.That(chain.Offset, Is.EqualTo(delay));
            Assert.That(pipeline.Position - chain.LastPresentedTimestamp, Is.LessThan(TimeSpan.FromMilliseconds(200)),
                "a shift of the track is not the picture falling behind");
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>
    /// A seek moves the position and nothing else: the shifts stay in force, so reading starts the same distance ahead of
    /// the new position as it did of the old one, and the shifted track keeps presenting its content at the same distance
    /// from the clock.
    /// </summary>
    [Test]
    public void KeepsTheShiftsInForceAcrossASeek()
    {
        SyntheticSourceProviderFactory sources = new(new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(40),
            KeyFrameInterval = 30,
            LandsOnKeyframes = true,
        });

        FakeVideoRenderer videoRenderer = new();
        using PlaybackPipeline pipeline = CreatePipeline(
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
            WaitFor(() => pipeline.Position > TimeSpan.FromSeconds(0.5), "playback to be under way", 5000);

            IVideoChain chain = pipeline.VideoChains[0];
            var delay = TimeSpan.FromMilliseconds(400);
            chain.Offset = delay;
            WaitFor(() => sources.Source?.LastSeekPosition is TimeSpan position && position < TimeSpan.FromMilliseconds(400),
                "the file to be read from where the shifted picture needs it", 5000);

            var start = TimeSpan.FromSeconds(20);
            Assert.That(Await(pipeline.Seek(start, SeekMode.Precise)).IsSuccessful, Is.True);

            // The picture is 400 ms late, so its content at that position starts 400 ms before it.
            WaitFor(() => sources.Source?.LastSeekPosition == start - delay,
                "the file to be read the same distance ahead of the new position", 5000);

            long before = chain.PresentedFrameCount;
            WaitFor(() => chain.PresentedFrameCount > before + 3, "the picture after the seek to be up", 5000);

            Assert.That(chain.Offset, Is.EqualTo(delay));
            Assert.That(pipeline.Position - chain.LastPresentedTimestamp, Is.LessThan(TimeSpan.FromMilliseconds(200)),
                "the shift survived the seek, so it is still not the picture falling behind");
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>
    /// A shift is taken even while nothing is playing: the reader is asleep then, and the request has to wake it, or the
    /// move would sit unread until something else happened to stir the pipeline.
    /// </summary>
    [Test]
    public void TakesAShiftWhilePaused()
    {
        SyntheticSourceProviderFactory sources = new(new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(3),
            KeyFrameInterval = 30,
            LandsOnKeyframes = true,
        });

        FakeVideoRenderer videoRenderer = new();
        using PlaybackPipeline pipeline = CreatePipeline(
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
            Assert.That(Await(pipeline.Pause()).IsSuccessful, Is.True);

            var advance = TimeSpan.FromMilliseconds(-200);
            pipeline.VideoChains[0].Offset = advance;
            pipeline.AudioChains[0].Offset = advance;

            WaitFor(() => sources.Source?.LastSeekPosition is TimeSpan position && position > TimeSpan.FromMilliseconds(200),
                "the file to be read from where the shifted tracks need it", 5000);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused));
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>
    /// A sound that is moved while nothing has to be read again â€” the content it now needs is still to come â€” takes the move
    /// itself: what the device holds is let go of, the start time is taken again from the gate, and the blocks that are now
    /// too old are dropped, so what is heard starts the shift further on. No new epoch is published for it, so the picture
    /// keeps what it has and where reading starts is untouched.
    /// </summary>
    [Test]
    public void DropsThePartOfTheSoundThatIsNowTooOld()
    {
        SyntheticSourceProviderFactory sources = new(new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(20),
            KeyFrameInterval = 30,
            LandsOnKeyframes = true,
        });

        FakeVideoRenderer videoRenderer = new();
        FakeAudioRenderer audioRenderer = new() { QueuedSampleCount = SampleRate };
        using PlaybackPipeline pipeline = CreatePipeline(
            videoRenderer,
            audioRenderer,
            videoDecoders: new SyntheticVideoDecoderFactory(),
            sourceFactory: sources,
            audioDecoders: new SyntheticAudioDecoderFactory());

        try
        {
            Assert.That(Open(pipeline, SyntheticPath()).IsSuccessful, Is.True);
            Assert.That(Select(pipeline, MediaStreamKind.Video, pipeline.Sources[0].Info.VideoStreams[0]).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.Position > TimeSpan.FromSeconds(0.5), "playback to be under way", 5000);

            // The picture is held back half a second, so reading starts that much earlier than the clock and stays there: the
            // sound moved afterwards is not what decides where reading starts. The wait is on the file being read again rather
            // than on where reading starts, because the seek that began playback already starts behind the clock.
            int seeks = sources.Source?.SeekCount ?? 0;
            pipeline.VideoChains[0].Offset = TimeSpan.FromMilliseconds(500);
            WaitFor(() => (sources.Source?.SeekCount ?? 0) > seeks, "the file to be read again for the picture", 5000);
            WaitFor(() => sources.Source?.LastSeekPosition is TimeSpan landed && landed < pipeline.Position - TimeSpan.FromMilliseconds(300),
                "the file to be read from before the clock", 5000);
            TimeSpan? start = sources.Source?.LastSeekPosition;

            int flushes = audioRenderer.FlushCount;
            TimeSpan before = pipeline.Position;
            var advance = TimeSpan.FromMilliseconds(-400);
            pipeline.AudioChains[0].Offset = advance;

            // Let go of what the device holds, without reading anything again.
            WaitFor(() => audioRenderer.FlushCount > flushes, "the sound to be let go of", 3000);
            int marker = audioRenderer.SubmittedTimestamps.Length;
            WaitFor(() => audioRenderer.SubmittedTimestamps.Length > marker, "the sound to carry on", 3000);

            TimeSpan resumed = audioRenderer.SubmittedTimestamps[marker];
            Assert.That(resumed, Is.GreaterThanOrEqualTo(before - advance - TimeSpan.FromMilliseconds(100)),
                "the part of the sound that is now too old was left out");
            Assert.That(sources.Source?.LastSeekPosition, Is.EqualTo(start), "nothing had to be read again");
            Assert.That(pipeline.Position, Is.GreaterThanOrEqualTo(before), "the clock itself was not taken back");
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing));
        }
        finally
        {
            sources.Release();
        }
    }

}
