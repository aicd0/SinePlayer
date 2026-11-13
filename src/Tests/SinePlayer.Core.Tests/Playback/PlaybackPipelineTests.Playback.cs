// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Engine;
using SinePlayer.Core.Playback.Hosting;

namespace SinePlayer.Core.Tests.Playback;

public partial class PlaybackPipelineTests
{
    [Test]
    public void PlaysAVideoOnlyFileToTheEnd()
    {
        string path = CreateBitmapFile();
        try
        {
            FakeVideoRenderer videoRenderer = new();
            FakeAudioRenderer audioRenderer = new() { CanConfigure = false };
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, audioRenderer);

            OperationResult opened = Open(pipeline, path);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(opened.IsSuccessful, Is.True, opened.Message);
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused));
                Assert.That(pipeline.Sources[0].Info.AudioStreams, Is.Empty);
                Assert.That(pipeline.Sources[0].Info.VideoStreams, Has.Count.EqualTo(1));
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
                Assert.That(pipeline.VideoChains[0].PresentedFrameCount, Is.GreaterThan(0));
                Assert.That(pipeline.Position, Is.GreaterThanOrEqualTo(TimeSpan.Zero));
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

            OperationResult opened = Open(pipeline, path);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(opened.IsSuccessful, Is.True, opened.Message);
                Assert.That(pipeline.Sources[0].Info.AudioStreams, Has.Count.EqualTo(1));
                Assert.That(pipeline.Sources[0].Info.VideoStreams, Is.Empty);
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

            // The clock is stopped by the render threads rather than by the pause itself â€” they are the ones
            // that decide it, and the pause is what wakes them â€” so the device stops a moment after the state
            // does, which is the moment this waits for.
            WaitFor(() => !audioRenderer.ClockIsRunning, "a paused pipeline has to stop the clock");

            TimeSpan paused = pipeline.Position;
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused));

            Thread.Sleep(50);
            Assert.That(pipeline.Position, Is.EqualTo(paused), "a paused pipeline must not move");

            // Pausing what is already paused names a state the pipeline is not in, so it is refused like any
            // other command that cannot be carried out â€” and nothing about playback changes with it.
            OperationResult pausedAgain = Await(pipeline.Pause());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pausedAgain.IsSuccessful, Is.False);
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused));
            }

            // Playing what is already playing is the same kind of refusal, from the other side.
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.False);

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

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            pipeline.Play();
            WaitForPlaybackToFinish(pipeline);
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);

            long once = pipeline.VideoChains[0].PresentedFrameCount;
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

    /// <summary>
    /// A stop has to release every thread, and a thread parked on an empty queue is the one with no other way
    /// out: there is no clock in that wait, so the close is the word it is waiting for, and the stop's joins
    /// depend on it arriving. The arrangement is the one where the picture is parked most of the time â€” a
    /// decoder far too slow for the stream, so the queue it waits on is empty â€” and the stop is bounded, so a
    /// thread left parked fails this test rather than hanging the suite.
    /// </summary>
    [Test]
    public void StopsWhileAThreadIsParkedOnAnEmptyQueue()
    {
        string path = CreateY4mFile(frameCount: 200);
        try
        {
            using PlaybackPipeline pipeline = CreatePipeline(
                new FakeVideoRenderer(),
                new FakeAudioRenderer(),
                new ThrottledVideoDecoderFactory { DecodeMilliseconds = 80 });

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForPresentedFrames(pipeline, 2);

            // The picture has just shown a frame and the decoder is 80 ms from producing the next one, which
            // is where the queue it takes frames from is empty and it is parked.
            Thread.Sleep(20);

            Assert.That(Await(pipeline.Stop()).IsSuccessful, Is.True, "a stop has to release a parked consumer");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.Sources, Is.Empty, "a stop leaves nothing open");
                Assert.That(pipeline.Error, Is.Null);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A stop ends a playback, not the pipeline: the same instance opens the file again and plays it, which
    /// is the path the stop's epoch advance exists for â€” everything in flight belongs to a position that no
    /// longer exists afterwards, so a report posted by a thread on its way out cannot become the next
    /// playback's statement that the file has run out. The window is milliseconds wide and this test does not
    /// hit it (the advance was removed and the suite still passed, which is what that costs); what it covers
    /// is the path: the second playback runs the file to the end and hands every sample over.
    /// </summary>
    [Test]
    public void PlaysAgainAfterAStop()
    {
        string path = CreateWaveFile();
        try
        {
            FakeAudioRenderer audioRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), audioRenderer);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForPlaybackToFinish(pipeline);

            Assert.That(Await(pipeline.Stop()).IsSuccessful, Is.True);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.Sources, Is.Empty, "a stop leaves nothing open");
                Assert.That(pipeline.Error, Is.Null);
            }

            int playedOnce = audioRenderer.SubmittedSamples;
            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitForPlaybackToFinish(pipeline);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Ended), pipeline.Error);
                Assert.That(audioRenderer.SubmittedSamples - playedOnce, Is.EqualTo(SampleCount),
                    "the second playback plays the whole file rather than believing it has already run out");
                Assert.That(pipeline.Position, Is.GreaterThan(TimeSpan.Zero));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Threads are a resource: a chain that is gone has none left running, and disposing the pipeline hands back every
    /// thread it took â€” the reader's, the decoders' and the renderers'. Counted against a baseline, because other tests
    /// may be running pipelines of their own.
    /// </summary>
    [Test]
    public void HandsBackEveryThreadItTook()
    {
        int baseline = PipelineThread.LiveCount;

        string playing = SyntheticPath("threads");
        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(playing, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(30) });

        PlaybackPipeline pipeline = new() { SourceFactory = sources };
        try
        {
            IVideoChain video = Await(pipeline.AddVideoChain(new FakeRenderTarget()));
            Await(pipeline.AddAudioChain());
            ISubtitleChain first = Await(pipeline.AddSubtitleChain(new SubtitleSurface()));
            ISubtitleChain second = Await(pipeline.AddSubtitleChain(new SubtitleSurface()));

            video.Components.Register(new SyntheticVideoDecoderFactory());
            video.Components.Register(new FakeVideoRendererFactory(new FakeVideoRenderer()));
            pipeline.AudioChains[0].Components.Register(new SyntheticAudioDecoderFactory());
            pipeline.AudioChains[0].Components.Register(new FixedAudioRendererFactory(new FakeAudioRenderer()));
            foreach (ISubtitleChain subtitle in pipeline.SubtitleChains)
            {
                subtitle.Components.Register(new SyntheticSubtitleDecoderFactory());
                subtitle.Components.Register(new FakeSubtitleRendererFactory(new FakeSubtitleRenderer()));
            }

            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(playing))).IsSuccessful, Is.True);
            Assert.That(Await(video.SelectStream(pipeline.Sources[0].Info.VideoStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);

            // The reader, two per picture, one per sound and one per set of words.
            WaitFor(() => PipelineThread.LiveCount - baseline == 6, $"all six threads to be running (was {PipelineThread.LiveCount - baseline})");

            Assert.That(Await(pipeline.RemoveChain(second)).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);
            WaitFor(() => PipelineThread.LiveCount - baseline == 5, $"the removed chain to cost no thread (was {PipelineThread.LiveCount - baseline})");

            Assert.That(Await(pipeline.RemoveChain(first)).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);
            WaitFor(() => PipelineThread.LiveCount - baseline == 4, $"the second removed chain to cost no thread (was {PipelineThread.LiveCount - baseline})");
        }
        finally
        {
            pipeline.Dispose();
            sources.Release();
        }

        WaitFor(() => PipelineThread.LiveCount == baseline, $"the pipeline to hand every thread back (was {PipelineThread.LiveCount - baseline})");
    }

    /// <summary>
    /// A chain removed and never reconciled is still running, so stopping the pipeline has to stop it too rather than
    /// leave it behind.
    /// </summary>
    [Test]
    public void StopsAChainRemovedButNeverReconciled()
    {
        int baseline = PipelineThread.LiveCount;

        string playing = SyntheticPath("removed-then-stopped");
        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(playing, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(30) });

        PlaybackPipeline pipeline = new() { SourceFactory = sources };
        try
        {
            IVideoChain video = Await(pipeline.AddVideoChain(new FakeRenderTarget()));
            ISubtitleChain subtitle = Await(pipeline.AddSubtitleChain(new SubtitleSurface()));

            video.Components.Register(new SyntheticVideoDecoderFactory());
            video.Components.Register(new FakeVideoRendererFactory(new FakeVideoRenderer()));
            subtitle.Components.Register(new SyntheticSubtitleDecoderFactory());
            subtitle.Components.Register(new FakeSubtitleRendererFactory(new FakeSubtitleRenderer()));

            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(playing))).IsSuccessful, Is.True);
            Assert.That(Await(video.SelectStream(pipeline.Sources[0].Info.VideoStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);

            // The reader, two for the picture and one for the words.
            WaitFor(() => PipelineThread.LiveCount - baseline == 4, $"the pipeline to be running (was {PipelineThread.LiveCount - baseline})");

            Assert.That(Await(pipeline.RemoveChain(subtitle)).IsSuccessful, Is.True);

            // No reconcile: the removal is recorded and the chain is still winding down when the pipeline is stopped.
            pipeline.Dispose();
        }
        finally
        {
            sources.Release();
        }

        WaitFor(() => PipelineThread.LiveCount == baseline, $"the unreconciled chain to be stopped with the rest (was {PipelineThread.LiveCount - baseline})");
    }

    /// <summary>
    /// A pipeline that has been reset holds nothing: there is no file to report and nothing to play. The surfaces are
    /// taken by the reset, so the file that is added next is prepared against them. A seek, on the other hand, is
    /// accepted: it moves the position of a pipeline with nothing open and nothing to move, which is a no-op rather
    /// than a refusal.
    /// </summary>
    [Test]
    public void HoldsNothingAfterAReset()
    {
        string playing = SyntheticPath("playing");

        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(playing, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(3) });

        FakeSubtitleRenderer subtitleRenderer = new();
        PlaybackPipeline pipeline = CreateMultiSourcePipeline(sources, subtitleRenderer);
        try
        {
            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(playing))).IsSuccessful, Is.True);

            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Paused), "a reset pipeline is open and empty rather than unopened");
                Assert.That(pipeline.Sources, Is.Empty, "no file is reported");
                Assert.That(Await(pipeline.Play()).IsSuccessful, Is.False, "a file with nothing chosen does not play");
            }
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

}
