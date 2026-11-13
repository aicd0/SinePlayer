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
    /// Opening another file in the same pipeline is a reset followed by a fresh source and a fresh selection. The chains are the
    /// same objects throughout, so what has to survive is that they are pointed at the new file rather than at what they last
    /// played: the picture must come from the new source, not from the one that was just disposed.
    /// </summary>
    [Test]
    public void PlaysTheSecondFileAfterTheFirst()
    {
        SyntheticSourceProviderFactory sources = new(new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(6),
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
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => videoRenderer.RenderedFrames > 3, "the first file to be playing", 5000);

            // The host switches to the next item: the same pipeline, reset and opened again.
            int framesBefore = videoRenderer.RenderedFrames;
            Assert.That(Open(pipeline, SyntheticPath()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitFor(() => videoRenderer.RenderedFrames > framesBefore + 3, "the second file to bring pictures", 5000);

            MediaSource playing = pipeline.Sources[0];
            Assert.That(pipeline.VideoChains[0].SelectedStream, Is.SameAs(playing.Info.VideoStreams[0]),
                "the picture is the new file's, not the one that was disposed");
            Assert.That(pipeline.VideoChains[0].PlayingStream, Is.SameAs(playing.Info.VideoStreams[0]),
                "and it is what the chain is actually reading");
        }
        finally
        {
            sources.Release();
        }
    }

    /// <summary>
    /// Two players at once: two pipelines, two devices, two clocks. Nothing in the engine is shared between them â€”
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
    /// Adding a file while something plays is not a jump: the track that was playing keeps playing, the picture keeps
    /// coming, and only the list of files that may be read has grown.
    /// </summary>
    [Test]
    public void KeepsPlayingTheFileItWasPlayingWhenAnotherIsAdded()
    {
        string playing = SyntheticPath("playing");
        string subtitles = SyntheticPath("subtitles");

        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(playing, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(3) });
        sources.Add(subtitles, new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(3))],
        });

        FakeVideoRenderer videoRenderer = new();
        FakeAudioRenderer audioRenderer = new() { PlaysWhatItIsGiven = true };
        FakeSubtitleRenderer subtitleRenderer = new();
        PlaybackPipeline pipeline = CreateMultiSourcePipeline(sources, subtitleRenderer, videoRenderer, audioRenderer);
        try
        {
            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(playing))).IsSuccessful, Is.True);

            MediaInfo info = pipeline.Sources[0].Info;
            Assert.That(Select(pipeline, MediaStreamKind.Video, info.VideoStreams[0]).IsSuccessful, Is.True);
            Assert.That(Select(pipeline, MediaStreamKind.Audio, info.AudioStreams[0]).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => videoRenderer.RenderedFrames > 0, "playback to be under way");

            int rendered = videoRenderer.RenderedFrames;
            TimeSpan position = pipeline.Position;

            Assert.That(AddSubtitleFile(pipeline, subtitles).IsSuccessful, Is.True);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "adding a file is not a pause");
                Assert.That(pipeline.AudioChains[0].SelectedStream?.Index, Is.EqualTo(info.AudioStreams[0].Index), "the track that was playing keeps playing");
            }

            WaitFor(() => videoRenderer.RenderedFrames > rendered, "the picture to keep coming");
            Assert.That(pipeline.Position, Is.GreaterThanOrEqualTo(position - TrackSwitchSlack), "playback was not moved by the file being added");
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// A source nothing is selected from is not part of playback at all: adding it does not publish anything, and a seek
    /// â€” which moves every file a selected stream comes from â€” leaves it where it is. It becomes part of playback only
    /// when a stream of it is chosen, which is the act that says so.
    /// </summary>
    [Test]
    public void LeavesAnUnselectedSourceOutOfPlayback()
    {
        string playing = SyntheticPath("playing");
        string unused = SyntheticPath("unused");

        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(playing, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(3) });
        sources.Add(unused, new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(3))],
        });

        FakeSubtitleRenderer subtitleRenderer = new();
        PlaybackPipeline pipeline = CreateMultiSourcePipeline(sources, subtitleRenderer);
        try
        {
            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(playing))).IsSuccessful, Is.True);
            Assert.That(Select(pipeline, MediaStreamKind.Video, pipeline.Sources[0].Info.VideoStreams[0]).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.Position > TimeSpan.Zero, "playback to be under way");

            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(unused))).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Seek(TimeSpan.FromSeconds(2))).IsSuccessful, Is.True);
            WaitFor(() => sources.Opened[0].LastSeekPosition == TimeSpan.FromSeconds(2), "the file that plays to be moved");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.Sources, Has.Count.EqualTo(2), "the file was given to the pipeline");
                Assert.That(sources.Opened[1].LastSeekPosition, Is.Null, "and nothing was asked of it, because nothing of it is being played");
                Assert.That(pipeline.SubtitleChains[0].SelectedStream, Is.Null);
            }
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// A seek moves every file, not only the one being played, so a track served by an added file carries on from where
    /// the picture does. The added file is seeked by its own streams: the numbers of the playing file mean nothing to it.
    /// </summary>
    [Test]
    public void MovesEveryFileToThePositionASeekAsksFor()
    {
        string playing = SyntheticPath("playing");
        string subtitles = SyntheticPath("subtitles");

        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(playing, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(3) });
        sources.Add(subtitles, new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(3))],
        });

        FakeSubtitleRenderer subtitleRenderer = new();
        PlaybackPipeline pipeline = CreateMultiSourcePipeline(sources, subtitleRenderer);
        try
        {
            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(playing))).IsSuccessful, Is.True);

            MediaInfo info = pipeline.Sources[0].Info;
            Assert.That(Select(pipeline, MediaStreamKind.Video, info.VideoStreams[0]).IsSuccessful, Is.True);
            OperationResult<SubtitleStreamInfo> added = AddSubtitleFile(pipeline, subtitles);
            Assert.That(added.IsSuccessful, Is.True, added.Message);
            Assert.That(Select(pipeline, MediaStreamKind.Subtitle, added.Result).IsSuccessful, Is.True);

            var target = TimeSpan.FromSeconds(2);
            Assert.That(Await(pipeline.Seek(target, SeekMode.Precise)).IsSuccessful, Is.True);

            WaitFor(() => sources.Opened[1].LastSeekPosition == target, "the added file to be moved with the picture");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(sources.Opened[0].LastSeekPosition, Is.EqualTo(target), "the file that plays is moved to the position");
                Assert.That(sources.Opened[0].LastSeekStreamIndex, Is.EqualTo(info.VideoStreams[0].Index), "and lands by its picture");
                Assert.That(sources.Opened[1].LastSeekStreamIndex, Is.EqualTo(-1), "the added file is left to land by itself");
            }
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// Two files number their streams from nothing, so the same number means one stream in each: what is selected is the
    /// stream itself, which is what decides whose cues end up on screen.
    /// </summary>
    [Test]
    public void TellsAStreamOfOneFileFromTheSameNumberInAnother()
    {
        string playing = SyntheticPath("playing");
        string subtitles = SyntheticPath("subtitles");

        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(playing, new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(3))],
        });
        sources.Add(subtitles, new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.4), TimeSpan.FromSeconds(3))],
        });

        FakeSubtitleRenderer subtitleRenderer = new();
        PlaybackPipeline pipeline = CreateMultiSourcePipeline(sources, subtitleRenderer);
        try
        {
            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(playing))).IsSuccessful, Is.True);

            MediaInfo info = pipeline.Sources[0].Info;
            Assert.That(Select(pipeline, MediaStreamKind.Video, info.VideoStreams[0]).IsSuccessful, Is.True);

            OperationResult<SubtitleStreamInfo> added = AddSubtitleFile(pipeline, subtitles);
            Assert.That(added.IsSuccessful, Is.True, added.Message);
            Assert.That(added.Result.Index, Is.EqualTo(info.SubtitleStreams[0].Index), "the two files number their subtitle tracks alike");

            Assert.That(Select(pipeline, MediaStreamKind.Subtitle, info.SubtitleStreams[0]).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            string ownCue = SyntheticSubtitleDecoderFactory.TextAt(TimeSpan.FromSeconds(0.2));
            string addedCue = SyntheticSubtitleDecoderFactory.TextAt(TimeSpan.FromSeconds(0.4));
            WaitFor(() => subtitleRenderer.Texts.Contains(ownCue), "the cue of the file that plays");

            Assert.That(Select(pipeline, MediaStreamKind.Subtitle, added.Result).IsSuccessful, Is.True);
            WaitFor(() => subtitleRenderer.Texts.Contains(addedCue), "the cue of the added file, on the same track number");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.SubtitleChains[0].SelectedStream, Is.SameAs(added.Result), "the track that is selected is the one that was asked for");
                Assert.That(subtitleRenderer.Texts, Does.Not.Contain(ownCue), "and the cues of the other file are gone");
            }
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// What an added file holds is what it hands back: a caller that wanted words reads the streams it brought, and a
    /// file with none says so itself rather than being refused on its way in. Nothing of the file is read, because
    /// nothing of it was selected.
    /// </summary>
    [Test]
    public void HandsBackTheStreamsOfTheFileThatWasAdded()
    {
        string playing = SyntheticPath("playing");
        string plain = SyntheticPath("plain");

        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(playing, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(3) });
        sources.Add(plain, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(3) });

        FakeSubtitleRenderer subtitleRenderer = new();
        PlaybackPipeline pipeline = CreateMultiSourcePipeline(sources, subtitleRenderer);
        try
        {
            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(playing))).IsSuccessful, Is.True);

            OperationResult<MediaSource> added = Await(pipeline.AddSource(MediaLocator.Parse(plain)));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(added.IsSuccessful, Is.True, added.Message);
                Assert.That(added.Result.Locator.Value, Is.EqualTo(plain));
                Assert.That(added.Result.Info.Streams, Is.Not.Empty, "the file answers with what it holds");
                Assert.That(added.Result.Info.Streams.OfType<SubtitleStreamInfo>(), Is.Empty, "and this one holds no words");
                Assert.That(pipeline.Sources, Has.Count.EqualTo(2), "the file is on record whether or not anything is chosen from it");
            }
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// No source is a special case, the one given first included: a file that holds only subtitles is a source like any
    /// other, and what it cannot do is play â€” which is the same answer every source with no playable track chosen gets,
    /// and it is said by <c>Play</c> rather than by the file being refused.
    /// </summary>
    [Test]
    public void TakesASourceWithNothingButSubtitlesJustLikeAnyOther()
    {
        string wordsOnly = SyntheticPath("words");
        string playing = SyntheticPath("playing");

        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(wordsOnly, new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(3))],
            AudioStopsAt = TimeSpan.Zero,
            VideoStopsAt = TimeSpan.Zero,
        });
        sources.Add(playing, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(3) });

        FakeSubtitleRenderer subtitleRenderer = new();
        PlaybackPipeline pipeline = CreateMultiSourcePipeline(sources, subtitleRenderer);
        try
        {
            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);

            OperationResult<MediaSource> added = Await(pipeline.AddSource(MediaLocator.Parse(wordsOnly)));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(added.IsSuccessful, Is.True, added.Message);
                Assert.That(added.Result.Info.SubtitleStreams, Is.Not.Empty);
                Assert.That(pipeline.Sources, Has.Count.EqualTo(1));
            }

            // It cannot be played, and the reason is what any source with nothing chosen is told.
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.False);

            // And a file that can play is added on top of it exactly as it would be on top of anything else.
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(playing))).IsSuccessful, Is.True);
            MediaInfo info = pipeline.Sources[1].Info;
            Assert.That(Select(pipeline, MediaStreamKind.Video, info.VideoStreams[0]).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.Position > TimeSpan.Zero, "playback to be under way");
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

}
