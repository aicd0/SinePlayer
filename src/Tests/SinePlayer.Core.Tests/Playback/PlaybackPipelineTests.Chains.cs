// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.Gain;
using SinePlayer.Core.Playback.Hosting;
using SinePlayer.Core.Playback.Selection;

namespace SinePlayer.Core.Tests.Playback;

public partial class PlaybackPipelineTests
{
    /// <summary>
    /// Two pictures are two chains: each decodes and presents into the surface it was made with, and rebuilding one
    /// chain's selection leaves the other one drawing. This is the case a single pipeline-wide version could not
    /// express, because there was only ever one chain to be out of date.
    /// </summary>
    [Test]
    public void PlaysTwoPicturesOnTheirOwnSurfaces()
    {
        string firstPosition = SyntheticPath("first-picture");
        string secondPosition = SyntheticPath("second-picture");
        string afterTheSwitch = SyntheticPath("other-picture");

        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(firstPosition, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(10) });
        sources.Add(secondPosition, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(10) });
        sources.Add(afterTheSwitch, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(10) });

        FakeRenderTarget firstTarget = new();
        FakeRenderTarget secondTarget = new();
        FakeVideoRenderer firstRenderer = new();
        FakeVideoRenderer secondRenderer = new();
        PlaybackPipeline pipeline = new() { SourceFactory = sources };
        try
        {
            IVideoChain first = Await(pipeline.AddVideoChain(firstTarget));
            IVideoChain second = Await(pipeline.AddVideoChain(secondTarget));

            first.Components.Register(new SyntheticVideoDecoderFactory());
            first.Components.Register(new FakeVideoRendererFactory(firstRenderer));
            second.Components.Register(new SyntheticVideoDecoderFactory());
            second.Components.Register(new FakeVideoRendererFactory(secondRenderer));

            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(firstPosition))).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(secondPosition))).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(afterTheSwitch))).IsSuccessful, Is.True);

            Assert.That(Await(first.SelectStream(pipeline.Sources[0].Info.VideoStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(second.SelectStream(pipeline.Sources[1].Info.VideoStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitFor(() => firstRenderer.RenderedFrames > 2 && secondRenderer.RenderedFrames > 2, "both chains to draw");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(firstRenderer.Target, Is.SameAs(firstTarget), "each chain presents into the surface it was made with");
                Assert.That(secondRenderer.Target, Is.SameAs(secondTarget));
            }

            // One chain is pointed at another file while the other keeps playing: what is rebuilt is that chain.
            long drawnByTheOther = secondRenderer.RenderedFrames;
            Assert.That(Await(first.SelectStream(pipeline.Sources[2].Info.VideoStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);

            WaitFor(() => secondRenderer.RenderedFrames > drawnByTheOther, "the untouched chain to keep drawing across the other's rebuild");
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "rebuilding one chain is not a pause");
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// How a picture is decoded is the chain's business: one chain can be told to stay in software while the other is
    /// left to take whatever the machine offers. The preferences live on the chain's own resolver, so what one chain
    /// asks for is never what the other gets.
    /// </summary>
    [Test]
    public void GivesEachVideoChainItsOwnDecoderPreference()
    {
        string firstPosition = SyntheticPath("software-picture");
        string secondPosition = SyntheticPath("default-picture");

        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(firstPosition, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(3) });
        sources.Add(secondPosition, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(3) });

        RecordingSyntheticVideoDecoderFactory softwareOnly = new();
        RecordingSyntheticVideoDecoderFactory byDefault = new();
        PlaybackPipeline pipeline = new() { SourceFactory = sources };
        try
        {
            IVideoChain first = Await(pipeline.AddVideoChain(new FakeRenderTarget()));
            IVideoChain second = Await(pipeline.AddVideoChain(new FakeRenderTarget()));

            first.Components.Preferences = new VideoPreferences { PreferHardwareDecoding = false };
            first.Components.Register(softwareOnly);
            first.Components.Register(new FakeVideoRendererFactory(new FakeVideoRenderer()));
            second.Components.Register(byDefault);
            second.Components.Register(new FakeVideoRendererFactory(new FakeVideoRenderer()));

            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(firstPosition))).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(secondPosition))).IsSuccessful, Is.True);

            Assert.That(Await(first.SelectStream(pipeline.Sources[0].Info.VideoStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(second.SelectStream(pipeline.Sources[1].Info.VideoStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(softwareOnly.HardwareRequests, Has.None.True, "a chain asked for software never asks for hardware at all");
                Assert.That(byDefault.HardwareRequests, Has.Some.True, "the chain that was not asked anything tries the machine's decoder first");
                Assert.That(first.IsHardwareAccelerated, Is.False, "and the chain that stayed in software says so");
            }
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// Two sounds are two chains: each hands its own samples to its own device, each filters what it plays on its own,
    /// and only the one that arrived first moves the clock â€” a second sound plays alongside rather than pulling time
    /// its own way. One chain's device jamming leaves the other handing sound over.
    /// </summary>
    [Test]
    public void PlaysTwoAudioChainsOfWhichOnlyTheFirstKeepsTheClock()
    {
        string firstPosition = SyntheticPath("first-sound");
        string secondPosition = SyntheticPath("second-sound");

        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(firstPosition, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(3) });
        sources.Add(secondPosition, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(3) });

        FakeAudioRenderer firstDevice = new();
        FakeAudioRenderer secondDevice = new();
        PlaybackPipeline pipeline = new() { SourceFactory = sources };
        try
        {
            IAudioChain first = Await(pipeline.AddAudioChain());
            IAudioChain second = Await(pipeline.AddAudioChain());

            first.Components.Register(new SyntheticAudioDecoderFactory());
            first.Components.Register(new FixedAudioRendererFactory(firstDevice));
            second.Components.Register(new SyntheticAudioDecoderFactory());
            second.Components.Register(new FixedAudioRendererFactory(secondDevice));

            // What the two chains play is processed separately: a filter added to one is not something the other runs.
            first.Filters.Add(new GainAudioFilter());

            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(firstPosition))).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(secondPosition))).IsSuccessful, Is.True);

            Assert.That(Await(first.SelectStream(pipeline.Sources[0].Info.AudioStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(second.SelectStream(pipeline.Sources[1].Info.AudioStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);

            // One device has no room from the start. Its own chain waits in front of it; the other chain knows nothing
            // about it, which is the whole point of the two being chains rather than one shared stage.
            secondDevice.QueuedSampleCount = SampleRate;
            secondDevice.NoRoom = true;

            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => firstDevice.SubmittedBuffers > 2, "the chain whose device is open to play");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(secondDevice.SubmittedBuffers, Is.Zero, "the jammed device has taken nothing");
                Assert.That(first.OwnsClock, Is.True, "the sound that arrived first is what moves the clock");
                Assert.That(second.OwnsClock, Is.False, "a second sound plays alongside it");
                Assert.That(first.Filters.Count, Is.EqualTo(1), "the filter belongs to the chain it was added to");
                Assert.That(second.Filters.Count, Is.Zero, "and never reaches the other");
            }

            // Letting that device take sound again frees its own chain, and the sound it was holding reaches it.
            secondDevice.NoRoom = false;
            secondDevice.QueuedSampleCount = 0;
            WaitFor(() => secondDevice.SubmittedBuffers > 0, "the jammed chain to hand sound over once its device opens");
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// Two sets of words are two chains: each draws into its own surface, shows its own cues, and keeps its own
    /// presentation. Nothing about one chain's drawing is decided by another's.
    /// </summary>
    [Test]
    public void DrawsEachSubtitleChainOnItsOwnSurface()
    {
        string playing = SyntheticPath("playing");
        string words = SyntheticPath("words");

        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(playing, new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(3))],
        });
        sources.Add(words, new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(3))],
        });

        FakeSubtitleRenderer firstRenderer = new();
        FakeSubtitleRenderer secondRenderer = new();
        SubtitleSurface firstSurface = new();
        SubtitleSurface secondSurface = new();
        PlaybackPipeline pipeline = new() { SourceFactory = sources };
        try
        {
            // No sound at all: a pipeline is what the host makes it, and the kinds it never asked for are simply absent.
            IVideoChain video = Await(pipeline.AddVideoChain(new FakeRenderTarget()));
            ISubtitleChain first = Await(pipeline.AddSubtitleChain(firstSurface));
            ISubtitleChain second = Await(pipeline.AddSubtitleChain(secondSurface));

            video.Components.Register(new SyntheticVideoDecoderFactory());
            video.Components.Register(new FakeVideoRendererFactory(new FakeVideoRenderer()));
            first.Components.Register(new SyntheticSubtitleDecoderFactory());
            first.Components.Register(new FakeSubtitleRendererFactory(firstRenderer));
            second.Components.Register(new SyntheticSubtitleDecoderFactory());
            second.Components.Register(new FakeSubtitleRendererFactory(secondRenderer));

            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(playing))).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(words))).IsSuccessful, Is.True);

            Assert.That(Await(video.SelectStream(pipeline.Sources[0].Info.VideoStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(first.SelectStream(pipeline.Sources[0].Info.SubtitleStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(second.SelectStream(pipeline.Sources[1].Info.SubtitleStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);

            // One chain is asked to draw its words larger; the other has said nothing and keeps the decoder's own idea.
            second.Presentation = SubtitlePresentation.Identity with { FontSize = 32.0 };

            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            string firstCue = SyntheticSubtitleDecoderFactory.TextAt(TimeSpan.FromSeconds(0.2));
            string secondCue = SyntheticSubtitleDecoderFactory.TextAt(TimeSpan.FromSeconds(1.0));
            WaitFor(() => firstRenderer.Texts.Contains(firstCue), "the first chain's words to be drawn");
            WaitFor(() => secondRenderer.Texts.Contains(secondCue), "the second chain's words to be drawn");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(firstRenderer.Target, Is.SameAs(firstSurface), "each chain draws into the surface it was made with");
                Assert.That(secondRenderer.Target, Is.SameAs(secondSurface));
                Assert.That(firstRenderer.Texts, Does.Not.Contain(secondCue), "and shows only its own words");
                Assert.That(secondRenderer.Texts, Does.Not.Contain(firstCue));
                Assert.That(secondRenderer.Presentations[^1].FontSize, Is.EqualTo(32.0), "the presentation is the chain's own");
                Assert.That(firstRenderer.Presentations[^1].FontSize, Is.EqualTo(SubtitlePresentation.Identity.FontSize), "the other chain was never asked to change");
            }
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// Two chains may pick the same stream, and both are fed: the reader hands the packet to every chain that wants it
    /// rather than to the first one, so the second chain has a decoder of its own reading the same bytes. The packet
    /// carries a count rather than a copy, so a second chain costs an owner and not a payload.
    /// </summary>
    [Test]
    public void FeedsBothChainsThatSelectOneStream()
    {
        string playing = SyntheticPath("one-stream");
        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(playing, new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles =
            [
                new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(0.9)),
                new ScriptedSubtitle(TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(1.9)),
            ],
        });

        FakeSubtitleRenderer firstRenderer = new();
        FakeSubtitleRenderer secondRenderer = new();
        PlaybackPipeline pipeline = new() { SourceFactory = sources };
        try
        {
            IVideoChain video = Await(pipeline.AddVideoChain(new FakeRenderTarget()));
            ISubtitleChain first = Await(pipeline.AddSubtitleChain(new SubtitleSurface()));
            ISubtitleChain second = Await(pipeline.AddSubtitleChain(new SubtitleSurface()));

            video.Components.Register(new SyntheticVideoDecoderFactory());
            video.Components.Register(new FakeVideoRendererFactory(new FakeVideoRenderer()));
            first.Components.Register(new SyntheticSubtitleDecoderFactory());
            first.Components.Register(new FakeSubtitleRendererFactory(firstRenderer));
            second.Components.Register(new SyntheticSubtitleDecoderFactory());
            second.Components.Register(new FakeSubtitleRendererFactory(secondRenderer));

            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(playing))).IsSuccessful, Is.True);

            Assert.That(Await(video.SelectStream(pipeline.Sources[0].Info.VideoStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(first.SelectStream(pipeline.Sources[0].Info.SubtitleStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(second.SelectStream(pipeline.Sources[0].Info.SubtitleStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            // Two cues, not one: the first proves the packet reaches both chains, the second proves the ones after it
            // do too â€” each chain has a decoder of its own reading the whole stream.
            string firstCue = SyntheticSubtitleDecoderFactory.TextAt(TimeSpan.FromSeconds(0.2));
            string secondCue = SyntheticSubtitleDecoderFactory.TextAt(TimeSpan.FromSeconds(1.2));
            WaitFor(() => firstRenderer.Texts.Contains(firstCue), "one chain on the stream to be fed");
            WaitFor(() => secondRenderer.Texts.Contains(firstCue), "the other chain on the same stream to be fed too");
            WaitFor(() => secondRenderer.Texts.Contains(secondCue), "the second cue to reach the chain that came second");
            WaitFor(() => firstRenderer.Texts.Contains(secondCue), "and to reach the chain that came first");
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// Two chains on one stream are two independent consumers of it, so taking one away leaves the other being read
    /// for: the reader drops the removed chain's row and keeps the source for the one that stays. The source here is
    /// live, because a file is read ten seconds ahead â€” a cue from a file would already be in both queues before the
    /// removal, and then the test would say nothing about what happens afterwards.
    /// </summary>
    [Test]
    public void KeepsReadingTheStreamForTheChainThatStays()
    {
        string playing = SyntheticPath("shared-live");
        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(playing, new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(30),
            ReportsTheEndOfTheFile = false,
            Subtitles =
            [
                new ScriptedSubtitle(TimeSpan.FromSeconds(0.4), TimeSpan.FromSeconds(0.9)),
                new ScriptedSubtitle(TimeSpan.FromSeconds(1.4), TimeSpan.FromSeconds(1.9)),
            ],
        });

        FakeSubtitleRenderer firstRenderer = new();
        FakeSubtitleRenderer secondRenderer = new();
        PlaybackPipeline pipeline = new() { SourceFactory = sources };
        try
        {
            IVideoChain video = Await(pipeline.AddVideoChain(new FakeRenderTarget()));
            ISubtitleChain first = Await(pipeline.AddSubtitleChain(new SubtitleSurface()));
            ISubtitleChain second = Await(pipeline.AddSubtitleChain(new SubtitleSurface()));

            video.Components.Register(new SyntheticVideoDecoderFactory());
            video.Components.Register(new FakeVideoRendererFactory(new FakeVideoRenderer()));
            first.Components.Register(new SyntheticSubtitleDecoderFactory());
            first.Components.Register(new FakeSubtitleRendererFactory(firstRenderer));
            second.Components.Register(new SyntheticSubtitleDecoderFactory());
            second.Components.Register(new FakeSubtitleRendererFactory(secondRenderer));

            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(playing))).IsSuccessful, Is.True);

            Assert.That(Await(video.SelectStream(pipeline.Sources[0].Info.VideoStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(first.SelectStream(pipeline.Sources[0].Info.SubtitleStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(second.SelectStream(pipeline.Sources[0].Info.SubtitleStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            string firstCue = SyntheticSubtitleDecoderFactory.TextAt(TimeSpan.FromSeconds(0.4));
            string secondCue = SyntheticSubtitleDecoderFactory.TextAt(TimeSpan.FromSeconds(1.4));
            WaitFor(() => firstRenderer.Texts.Contains(firstCue) && secondRenderer.Texts.Contains(firstCue), "both chains on the stream to be drawing");

            int reads = sources.Opened[0].Reads;
            Assert.That(Await(pipeline.RemoveChain(first)).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);

            // By the time the reconcile is done the chain is joined, so anything it draws from here is a leak.
            int stoppedAt = firstRenderer.ShowCount;

            WaitFor(() => secondRenderer.Texts.Contains(secondCue), "the chain that stayed to be read for after the other one went", 4000);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.SubtitleChains, Is.EqualTo(new[] { second }), "only the chain that was asked to go has gone");
                Assert.That(firstRenderer.Texts, Does.Not.Contain(secondCue), "the chain that went is fed nothing more");
                Assert.That(firstRenderer.ShowCount, Is.EqualTo(stoppedAt), "and draws nothing more");
                Assert.That(sources.Opened[0].Reads, Is.GreaterThan(reads), "and the reader kept reading the stream for the one that stayed");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "playback never left");
            }
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// A seek moves every chain, not just the first: two pictures on one stream are both told to land at the same
    /// place and both present from it. The landing the demuxer is given is a single stream index for the whole
    /// source, so this is also the case that says choosing it from the chains that are here is enough.
    /// </summary>
    [Test]
    public void SeeksBothChainsOnOneStream()
    {
        string playing = SyntheticPath("seek-both");
        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(playing, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(10) });

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
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(playing))).IsSuccessful, Is.True);
            Assert.That(Await(first.SelectStream(pipeline.Sources[0].Info.VideoStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(second.SelectStream(pipeline.Sources[0].Info.VideoStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitFor(() => firstRenderer.RenderedFrames > 0 && secondRenderer.RenderedFrames > 0, "both pictures to be presented");

            var target = TimeSpan.FromSeconds(5);
            Assert.That(firstRenderer.RenderedTimestamps.Any(timestamp => timestamp >= target), Is.False, "nothing from where the seek is going had been presented yet");
            Assert.That(secondRenderer.RenderedTimestamps.Any(timestamp => timestamp >= target), Is.False);

            Assert.That(Await(pipeline.Seek(target, SeekMode.Precise)).IsSuccessful, Is.True);

            WaitFor(() => firstRenderer.RenderedTimestamps.Any(timestamp => timestamp >= target), "the first chain to present from where it was moved to", 4000);
            WaitFor(() => secondRenderer.RenderedTimestamps.Any(timestamp => timestamp >= target), "the second chain to present from where it was moved to too", 4000);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.Position, Is.GreaterThanOrEqualTo(target), "the position is where the seek asked");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "and playback carried on");
            }
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// Playback is over when every chain has reached the end of what it was playing, not when the first one has: a
    /// picture that runs for a second and one that runs for three keep the pipeline playing until the longer one is
    /// done. Both chains drain at their own pace, and the one that finished first stops claiming anything.
    /// </summary>
    [Test]
    public void PlaysUntilEveryChainHasReachedItsEnd()
    {
        string shorter = SyntheticPath("shorter");
        string longer = SyntheticPath("longer");
        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(shorter, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(1) });
        sources.Add(longer, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(3) });

        FakeVideoRenderer shorterRenderer = new();
        FakeVideoRenderer longerRenderer = new();
        PlaybackPipeline pipeline = new() { SourceFactory = sources };
        try
        {
            IVideoChain shortChain = Await(pipeline.AddVideoChain(new FakeRenderTarget()));
            IVideoChain longChain = Await(pipeline.AddVideoChain(new FakeRenderTarget()));

            shortChain.Components.Register(new SyntheticVideoDecoderFactory());
            shortChain.Components.Register(new FakeVideoRendererFactory(shorterRenderer));
            longChain.Components.Register(new SyntheticVideoDecoderFactory());
            longChain.Components.Register(new FakeVideoRendererFactory(longerRenderer));

            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(shorter))).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(longer))).IsSuccessful, Is.True);
            Assert.That(Await(shortChain.SelectStream(pipeline.Sources[0].Info.VideoStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(longChain.SelectStream(pipeline.Sources[1].Info.VideoStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitFor(() => pipeline.Position > TimeSpan.FromSeconds(1.5), "playback to run past the end of the shorter file", 4000);

            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "the other chain still has media, so playback is not over");

            WaitFor(() => pipeline.State == MediaEngineState.Ended, $"playback to end with the longer file (is {pipeline.State})", 5000);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(shorterRenderer.RenderedFrames, Is.GreaterThan(0), "the shorter picture played to its own end");
                Assert.That(longerRenderer.RenderedFrames, Is.GreaterThan(0), "and so did the longer one");
                Assert.That(pipeline.VideoChains, Is.EqualTo(new[] { shortChain, longChain }), "neither chain was taken away by the end of its file");
            }
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

}
