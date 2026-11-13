// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Filter;
using SinePlayer.Core.Playback.Backends.Downmix;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Backends.Gain;
using SinePlayer.Core.Playback.Hosting;

namespace SinePlayer.Core.Tests.Playback;

public partial class PlaybackPipelineTests
{
    /// <summary>
    /// The registry decides which decoders can serve a stream; the pipeline walks the list it is given and
    /// takes the first that opens. A factory that refuses has to leave the file to the next one.
    /// </summary>
    [Test]
    public void LetsTheNextDecoderServeAFileWhenTheFirstOneRefuses()
    {
        RecordingVideoDecoderFactory silent = new();

        string path = CreateBitmapFile();
        try
        {
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), new FakeAudioRenderer());
            pipeline.VideoChains[0].Components.ClearDecoders();
            pipeline.VideoChains[0].Components.Register(new RefusingVideoDecoderFactory());
            pipeline.VideoChains[0].Components.Register(silent);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True, pipeline.Error);
            Assert.That(silent.Options, Is.Not.Null, "the second decoder has to be the one that was created");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The audio slots are resolved from the registry like everything else, so a factory that refuses leaves
    /// the file to the next one instead of ending the open.
    /// </summary>
    [Test]
    public void LetsTheNextAudioDecoderServeTheFileWhenTheFirstOneRefuses()
    {
        string path = CreateWaveFile();
        try
        {
            using PlaybackPipeline pipeline = CreatePipeline(p =>
            {
                p.VideoChains[0].Components.Register(new FakeVideoRendererFactory(new FakeVideoRenderer()));
                p.AudioChains[0].Components.Register(new FixedAudioRendererFactory(new FakeAudioRenderer()));
                p.AudioChains[0].Components.Register(new RefusingAudioDecoderFactory());
                p.AudioChains[0].Components.Register(new FFmpegAudioDecoderFactory());
            });

            OperationResult opened = Open(pipeline, path);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(opened.IsSuccessful, Is.True, opened.Message);
                Assert.That(pipeline.Sources[0].Info.AudioStreams, Is.Not.Empty);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The sound device is resolved from the registry too, which is what makes "play through that device" a
    /// registration rather than a factory handed to a setter.
    /// </summary>
    [Test]
    public void LetsTheNextAudioRendererServeTheFileWhenTheFirstOneRefuses()
    {
        string path = CreateWaveFile();
        try
        {
            FakeAudioRenderer audioRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(p =>
            {
                p.VideoChains[0].Components.Register(new FakeVideoRendererFactory(new FakeVideoRenderer()));
                p.AudioChains[0].Components.Register(new RefusingAudioRendererFactory());
                p.AudioChains[0].Components.Register(new FixedAudioRendererFactory(audioRenderer));
                p.AudioChains[0].Components.Register(new FFmpegAudioDecoderFactory());
            });

            OperationResult opened = Open(pipeline, path);

            Assert.That(opened.IsSuccessful, Is.True, opened.Message);

            // The device itself is opened by the audio thread, so the second one having been configured is a thing to
            // wait for rather than to find already done; what the open answers is which factory was used.
            WaitFor(() => audioRenderer.ConfiguredStream is not null, "the second device to be the one that was configured");
            Assert.That(audioRenderer.ConfiguredStream, Is.Not.Null, "the second device has to be the one that was configured");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The picture's output is resolved the same way, so a device that cannot be made is skipped.</summary>
    [Test]
    public void LetsTheNextVideoRendererServeTheFileWhenTheFirstOneRefuses()
    {
        string path = CreateBitmapFile();
        try
        {
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(p =>
            {
                p.VideoChains[0].Components.Register(new RefusingVideoRendererFactory());
                p.VideoChains[0].Components.Register(new FakeVideoRendererFactory(videoRenderer));
                p.VideoChains[0].Components.Register(new FFmpegVideoDecoderFactory());
                p.AudioChains[0].Components.Register(new FixedAudioRendererFactory(new FakeAudioRenderer()));
            });

            OperationResult opened = Open(pipeline, path);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(opened.IsSuccessful, Is.True, opened.Message);
                Assert.That(videoRenderer.ConfiguredStream, Is.Not.Null, "the second renderer has to be the one that was configured");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A reconcile answers per chain, keyed by the chain itself, so a failure is looked up by the chain that failed
    /// rather than by its kind â€” and a chain of another kind is not dragged into it.
    /// </summary>
    [Test]
    public void ReportsEachChainsResultUnderThatChain()
    {
        string path = CreateBitmapFile();
        try
        {
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), new FakeAudioRenderer());
            pipeline.VideoChains[0].Components.ClearDecoders();
            pipeline.VideoChains[0].Components.Register(new RefusingVideoDecoderFactory());

            Assert.That(Open(pipeline, path).IsSuccessful, Is.False);

            OperationResult<ReconcileResult> reconciled = Await(pipeline.Reconcile());
            Assert.That(reconciled.IsSuccessful, Is.True, reconciled.Message);

            ReconcileResult result = reconciled.Result;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Results.Keys, Is.EqualTo<IEnumerable<IPlaybackChain>>([.. pipeline.VideoChains, .. pipeline.AudioChains, .. pipeline.SubtitleChains]),
                    "one entry per chain, keyed by the chain");
                Assert.That(result[pipeline.VideoChains[0]].IsSuccessful, Is.False, "the chain that could not be built says so");
                Assert.That(result[pipeline.AudioChains[0]].IsSuccessful, Is.True, "another kind's chain is untouched by it");
                Assert.That(result[pipeline.VideoChains[0]].Message, Is.Not.Empty, "the failure carries the reason the decoder gave");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The video decoder can be replaced while a file plays. It has to be a replacement and not a restart: the
    /// state stays playing, the position stays where it was, and frames keep coming â€” from the new decoder.
    /// </summary>
    [Test]
    public void ReplacesTheVideoDecoderWithoutLeavingPlayback()
    {
        string path = CreateY4mFile(frameCount: 60, frameRate: "30:1");
        try
        {
            RecordingOptionsVideoDecoderFactory decoders = new();
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer(), videoDecoders: decoders);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Await(pipeline.Play());
            WaitFor(() => videoRenderer.RenderedFrames > 2, "playback to be under way", 5000);

            long rendered = videoRenderer.RenderedFrames;
            TimeSpan position = pipeline.Position;

            // The host says what it wants by changing the registry, then tells the pipeline to look again.
            pipeline.VideoChains[0].Components.Preferences = pipeline.VideoChains[0].Components.Preferences with { PreferHardwareDecoding = false };
            OperationResult<ReconcileResult> reconciled = Await(pipeline.Reconcile());

            Assert.That(reconciled.IsSuccessful, Is.True, reconciled.Message);

            WaitFor(() => decoders.RequestCount >= 2, "the decoder to be replaced");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(decoders.GetRequest(1).HardwareDecoding, Is.False, "the new decoder has to be created with the new preference");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "changing the decoder is not a pause");
                Assert.That(pipeline.Position, Is.GreaterThanOrEqualTo(position - TimeSpan.FromSeconds(1)), "the stream has to pick up where it was");
            }

            WaitFor(() => videoRenderer.RenderedFrames > rendered, "frames to keep coming from the new decoder");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// What the pipeline reports is the decoder that is running, not the one that was asked for: switching the kind
    /// of decoder moves the report with it, and playback carries on through the new one.
    /// <para>
    /// The two stand-ins decode exactly the same way and differ only in what they say about themselves, because that
    /// is what the report is about. Whether a real hardware decoder can be built for a file is a property of the
    /// machine and the codec, so a test that waited for one would be testing the machine.
    /// </para>
    /// </summary>
    [Test]
    public void ReportsTheDecoderThatIsRunning()
    {
        string path = CreateY4mFile(frameCount: 60, frameRate: "30:1");
        try
        {
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer());
            pipeline.VideoChains[0].Components.ClearDecoders();
            pipeline.VideoChains[0].Components.Register(new FlaggedVideoDecoderFactory(hardware: true, "flagged:hardware"));

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);

            // The decoder is taken over by the decode thread, so what is running is reported once it has it.
            WaitFor(() => pipeline.VideoChains[0].IsHardwareAccelerated, "the decoder that says it runs on hardware");

            Await(pipeline.Play());
            WaitFor(() => videoRenderer.RenderedFrames > 2, "playback to be under way", 5000);

            long rendered = videoRenderer.RenderedFrames;
            pipeline.VideoChains[0].Components.ClearDecoders();
            pipeline.VideoChains[0].Components.Register(new FlaggedVideoDecoderFactory(hardware: false, "flagged:software"));
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);
            WaitFor(() => !pipeline.VideoChains[0].IsHardwareAccelerated, "the replacement to be reported");

            WaitFor(() => videoRenderer.RenderedFrames > rendered, "the new decoder to keep the picture coming");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The video renderer can be replaced while a file plays: the factory the caller names makes the new one,
    /// the picture carries on without a pause, and the renderer it replaced is released. It goes through the
    /// same rebuild as a decoder switch, because a decoder decodes on the device its renderer owns.
    /// </summary>
    [Test]
    public void SwapsTheVideoRendererWithoutLeavingPlayback()
    {
        string path = CreateY4mFile(frameCount: 60, frameRate: "30:1");
        try
        {
            FakeVideoRenderer first = new() { Name = "first" };
            FakeVideoRenderer second = new() { Name = "second" };
            RecordingOptionsVideoDecoderFactory decoders = new();
            using PlaybackPipeline pipeline = CreatePipeline(first, new FakeAudioRenderer(), videoDecoders: decoders);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Await(pipeline.Play());
            WaitFor(() => first.RenderedFrames > 2, "playback to be under way");
            Assert.That(pipeline.VideoChains[0].RendererName, Is.EqualTo("first"));
            Assert.That(decoders.RequestCount, Is.EqualTo(1), "the decoder was created for the first renderer");

            TimeSpan position = pipeline.Position;
            SwapVideoRenderer(pipeline, new FakeVideoRendererFactory(second));
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);
            WaitFor(() => second.RenderedFrames > 0, "the new renderer to be drawing");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.VideoChains[0].RendererName, Is.EqualTo("second"));
                Assert.That(first.DisposeCount, Is.EqualTo(1), "the renderer that was replaced has to be released");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "changing the renderer is not a pause");
                Assert.That(pipeline.Position, Is.GreaterThanOrEqualTo(position - TimeSpan.FromSeconds(1)), "the stream has to pick up where it was");
            }

            // A new renderer brings a new device, so the decoder is rebuilt even though the decoder registry was
            // not touched: frames decoded before the swap belong to a device that is no longer drawing.
            WaitFor(() => decoders.RequestCount >= 2, "the decoder to be rebuilt on the new renderer's device");

            long rendered = second.RenderedFrames;
            WaitFor(() => second.RenderedFrames > rendered, "frames to keep coming through the new renderer");

            // The choice outlives the file, like the decoder's: the next one is drawn by the same factory.
            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            WaitFor(() => pipeline.VideoChains[0].RendererName == "second", "the next file to be taken up by the chosen renderer");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The renderer can be replaced while a file plays, and playback stays where it is. A rebuilt chain is a fresh
    /// start â€” what the old one decoded is not what the new one will decode â€” so the stream is started again at the
    /// position playback has reached: the device is flushed and fed from there, and what matters is that the position
    /// neither rewinds nor jumps.
    /// </summary>
    [Test]
    public void SwapsTheVideoRendererWithoutMovingPlayback()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            FakeVideoRenderer first = new() { Name = "first" };
            FakeVideoRenderer second = new() { Name = "second" };

            // A device that plays what it is given, so "kept playing" is something this test can see.
            FakeAudioRenderer audioRenderer = new() { PlaysWhatItIsGiven = true };
            using PlaybackPipeline pipeline = CreatePipeline(first, audioRenderer);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Await(pipeline.Play());
            WaitFor(() => first.RenderedFrames > 2, "playback to be under way");
            WaitFor(() => audioRenderer.SubmittedSamples > 0, "the sound to be playing");

            int flushed = audioRenderer.FlushCount;
            TimeSpan position = pipeline.Position;

            SwapVideoRenderer(pipeline, new FakeVideoRendererFactory(second));
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.VideoChains[0].RendererName == "second", "the new renderer to take over");
            WaitFor(() => second.RenderedFrames > 0, "the new renderer to draw");

            // Long enough that a device which was going to be let go of would have been let go of by now.
            TimeSpan afterSwap = pipeline.Position;
            WaitFor(() => pipeline.Position > afterSwap + TimeSpan.FromMilliseconds(200), "the clock to keep running");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(audioRenderer.FlushCount, Is.EqualTo(flushed + 1), "the stream is started again where playback is, which flushes the device");
                Assert.That(audioRenderer.ClockIsRunning, Is.True, "the device keeps playing");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "changing the picture is not a pause");
                Assert.That(pipeline.Position, Is.GreaterThan(position), "the file has to keep playing, not be moved");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A renderer can be swapped while the picture is not moving, and the swap happens then rather than when
    /// playback resumes. The render thread is asleep in that state â€” an unbounded wait on its own node â€” so
    /// what makes it look at the request is the wake the request leaves behind.
    /// </summary>
    [Test]
    public void SwapsTheVideoRendererWhilePaused()
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
            Await(pipeline.Pause());

            SwapVideoRenderer(pipeline, new FakeVideoRendererFactory(second));
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.VideoChains[0].RendererName == "second", "the swap to happen while the picture is still");

            Await(pipeline.Play());
            WaitFor(() => second.RenderedFrames > 0, "the new renderer to draw once playback resumes");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A creator that cannot make what it said it would is passed over for the next registration: a machine that
    /// will not give what one factory promised may well give what the next one offers, and a picture that only
    /// comes from the second one is worth more than a report about the first.
    /// </summary>
    [Test]
    public void PassesOverACreatorThatCannotMakeItsComponent()
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

            // The registration that promises a renderer it cannot build comes first; the one that can comes second.
            pipeline.VideoChains[0].Components.ClearRenderers();
            pipeline.VideoChains[0].Components.Register(new FailingVideoRendererFactory("broken"));
            pipeline.VideoChains[0].Components.Register(new FakeVideoRendererFactory(second));

            OperationResult<ReconcileResult> swapped = Await(pipeline.Reconcile());

            Assert.That(swapped.IsSuccessful, Is.True, swapped.Message);

            // The swap is published by the reconcile and taken up by the render thread, so the name changes a beat
            // after the command returns.
            WaitFor(() => pipeline.VideoChains[0].RendererName == "second", "the registration that could deliver to be the one running");

            WaitFor(() => second.RenderedFrames > 0, "the second renderer to draw");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A renderer that cannot be made must not take the running one down with it: the caller is told, and the
    /// picture keeps coming from the renderer that was already drawing.
    /// </summary>
    [Test]
    public void KeepsTheRunningRendererWhenTheNewOneFails()
    {
        string path = CreateY4mFile(frameCount: 60, frameRate: "30:1");
        try
        {
            FakeVideoRenderer first = new() { Name = "first" };
            using PlaybackPipeline pipeline = CreatePipeline(first, new FakeAudioRenderer());

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Await(pipeline.Play());
            WaitFor(() => first.RenderedFrames > 2, "playback to be under way");

            long rendered = first.RenderedFrames;

            // The renderer that works is removed from the registry, so what is left is one that offers a renderer
            // it cannot build: the reconcile has to say so and leave the running renderer alone.
            SwapVideoRenderer(pipeline, new FailingVideoRendererFactory("broken"));

            OperationResult<ReconcileResult> swapped = Await(pipeline.Reconcile());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(swapped.Result[pipeline.VideoChains[0]].IsSuccessful, Is.False, "a renderer that cannot be made is the caller's failure to see");
                Assert.That(pipeline.VideoChains[0].RendererName, Is.EqualTo("first"), "the running renderer is untouched");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing));
            }

            WaitFor(() => first.RenderedFrames > rendered, "the running renderer to carry on");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The device the sound comes out of can be replaced while a file plays. The new device takes over on the
    /// audio thread, the one being replaced is flushed and released there, and playback carries on: the state
    /// stays playing, the clock keeps moving, and the level the host set survives the swap. A rebuilt chain restarts
    /// the stream where playback is, so the device now in use is flushed once as part of that â€” and the position is
    /// still where it was, which is what "the file does not move for it" means.
    /// </summary>
    [Test]
    public void SwapsTheAudioRendererWithoutLeavingPlayback()
    {
        string path = CreateWaveFile(sampleCount: SampleRate * 5);
        try
        {
            FakeVideoRenderer videoRenderer = new();
            // A device that never takes the audio is the state a swap is interesting in: playback waits for it,
            // so the file cannot run out while the test is setting the swap up.
            FakeAudioRenderer first = new() { Name = "first", QueuedSampleCount = SampleRate, NoRoom = true };
            FakeAudioRenderer second = new() { Name = "second", QueuedSampleCount = SampleRate };
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, first);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Await(pipeline.Play());
            WaitFor(() => first.SubmitsStarted > 0, "the pipeline to be handing the device audio");
            Assert.That(pipeline.AudioChains[0].RendererName, Is.EqualTo("first"));

            TimeSpan position = pipeline.Position;
            SwapAudioRenderer(pipeline, new FixedAudioRendererFactory(second));
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);
            WaitFor(() => second.SubmittedBuffers > 0, "the new device to be playing");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.AudioChains[0].RendererName, Is.EqualTo("second"));
                Assert.That(first.DisposeCount, Is.EqualTo(1), "the device that was replaced has to be released");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "changing the device is not a pause");
                Assert.That(pipeline.Position, Is.GreaterThanOrEqualTo(position - TimeSpan.FromSeconds(1)), "the stream has to pick up where it was");
                Assert.That(second.FlushCount, Is.EqualTo(1), "the stream is started again where it stands, which is a flush of the device now in use");
            }

            WaitFor(() => pipeline.Position > position, "the clock to keep moving");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A device that cannot be opened must not take the running one down with it: the caller is told, and the
    /// sound keeps coming out of the device that was already playing.
    /// </summary>
    [Test]
    public void KeepsTheRunningAudioRendererWhenTheNewOneFails()
    {
        string path = CreateWaveFile(sampleCount: SampleRate * 5);
        try
        {
            FakeVideoRenderer videoRenderer = new();
            FakeAudioRenderer first = new() { Name = "first", QueuedSampleCount = SampleRate, NoRoom = true };
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, first);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Await(pipeline.Play());
            WaitFor(() => first.SubmitsStarted > 0, "the pipeline to be handing the device audio");

            TimeSpan position = pipeline.Position;
            FakeAudioRenderer refuses = new() { CanConfigure = false, RefusalReason = "This device refuses the stream." };
            SwapAudioRenderer(pipeline, new FixedAudioRendererFactory(refuses));
            OperationResult<ReconcileResult> swapped = Await(pipeline.Reconcile());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(swapped.Result[pipeline.AudioChains[0]].IsSuccessful, Is.False, "a device that cannot be opened is the caller's failure to see");
                Assert.That(pipeline.AudioChains[0].RendererName, Is.EqualTo("first"), "the playing device is untouched");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing));
            }

            WaitFor(() => pipeline.Position > position, "the clock to keep moving");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The audio decoder is rebuilt too, on the thread that decodes audio â€” the reader. It is the last slot to
    /// get the treatment the video decoder has had since S13, and the reason it is worth having: without it,
    /// "which audio decoder" could only ever be answered at open.
    /// <para>
    /// A rebuilt chain restarts the stream where playback stands (the reconcile publishes the clock's position
    /// as the position to re-read from), so the device is flushed and fed again from there. What this test is
    /// about is that the decoder really was replaced and that nothing else moved: playback keeps playing, the
    /// sound keeps coming, and the position does not go backwards.
    /// </para>
    /// </summary>
    [Test]
    public void ReplacesTheAudioDecoderWithoutLeavingPlayback()
    {
        string path = CreateWaveFile(sampleCount: SampleRate * 5);
        try
        {
            // A device that plays what it is handed, so the file lasts as long as its sound does, and a decoder that
            // is slow on purpose, so the reader is still reading when the swap lands â€” which is the state where a
            // new decoder has something left to decode.
            FakeAudioRenderer audioRenderer = new() { PlaysWhatItIsGiven = true };
            using PlaybackPipeline pipeline = CreatePipeline(p =>
            {
                p.VideoChains[0].Components.Register(new FakeVideoRendererFactory(new FakeVideoRenderer()));
                p.AudioChains[0].Components.Register(new FixedAudioRendererFactory(audioRenderer));
                p.AudioChains[0].Components.Register(new PacedAudioDecoderFactory());
            });

            OperationResult opened = Open(pipeline, path);
            Assert.That(opened.IsSuccessful, Is.True, opened.Message);
            Await(pipeline.Play());
            WaitFor(() => audioRenderer.SubmittedBuffers > 2, "playback to be under way");

            // A second audio decoder under a signature of its own, in place of the one playing: the decoder changes,
            // and the reader is the thread that has to act on it.
            int before = audioRenderer.SubmittedBuffers;
            int flushed = audioRenderer.FlushCount;
            TimeSpan position = pipeline.Position;

            NamedAudioDecoderFactory second = new("audio-second");
            pipeline.AudioChains[0].Components.ClearDecoders();
            pipeline.AudioChains[0].Components.Register(second);

            OperationResult<ReconcileResult> reconciled = Await(pipeline.Reconcile());
            Assert.That(reconciled.IsSuccessful, Is.True, reconciled.Message);

            // Each wait is on the thing that is asserted below, and in the order the pipeline does them: the
            // device is let go of what it holds, the new decoder is the one decoding, and only then does the
            // sound it produces arrive. Waiting on the submissions first would let the count be raised by media
            // the device still had from the decoder that was replaced, and the flush read before it happened.
            WaitFor(() => audioRenderer.FlushCount > flushed, "the device to be restarted at the position playback has reached");
            WaitFor(() => second.Offers > 0, "the pipeline to ask the new decoder factory for a decoder");
            WaitFor(() => audioRenderer.SubmittedBuffers > before, $"the new decoder to keep the sound coming ({before} before, {audioRenderer.SubmittedBuffers} now, {pipeline.State}, starved {pipeline.StarvedFor ?? "nothing"})");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(second.Offers, Is.EqualTo(1), "the decoder the host asked for is the one that was made");
                Assert.That(audioRenderer.FlushCount, Is.EqualTo(flushed + 1), "the stream is started again where it stands, once");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "replacing an audio decoder is not a pause");
                Assert.That(pipeline.Position, Is.GreaterThanOrEqualTo(position), "the clock is not moved backwards");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// One reconcile applies everything that changed, not the last thing: a host that swaps the picture's
    /// output and the sound's device in one edit gets both, and one call.
    /// </summary>
    [Test]
    public void AppliesBothChainsInOneReconcile()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            FakeVideoRenderer firstVideo = new() { Name = "first-video" };
            FakeVideoRenderer secondVideo = new() { Name = "second-video" };

            // The first device holds what it is given, the way a real one does â€” a device with room for the whole
            // file would take all of it before the swap and leave the new one nothing to play.
            FakeAudioRenderer firstAudio = new() { Name = "first-audio", QueuedSampleCount = SampleRate, NoRoom = true };
            FakeAudioRenderer secondAudio = new() { Name = "second-audio", QueuedSampleCount = SampleRate };

            using PlaybackPipeline pipeline = CreatePipeline(firstVideo, firstAudio);
            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Await(pipeline.Play());
            WaitFor(() => firstVideo.RenderedFrames > 2, "playback to be under way");

            SwapVideoRenderer(pipeline, new FakeVideoRendererFactory(secondVideo));
            SwapAudioRenderer(pipeline, new FixedAudioRendererFactory(secondAudio));

            OperationResult<ReconcileResult> reconciled = Await(pipeline.Reconcile());

            Assert.That(reconciled.IsSuccessful, Is.True, reconciled.Message);

            WaitFor(() => secondVideo.RenderedFrames > 0, "the new video renderer to draw");
            WaitFor(() => secondAudio.SubmitsStarted > 0, "the new audio device to be handed sound");
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Asking for the decoder that is already running changes nothing.</summary>
    [Test]
    public void KeepsTheRunningDecoderWhenThePreferenceIsUnchanged()
    {
        string path = CreateY4mFile(frameCount: 60, frameRate: "30:1");
        try
        {
            RecordingOptionsVideoDecoderFactory decoders = new();
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer(), videoDecoders: decoders);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Await(pipeline.Play());
            WaitFor(() => decoders.RequestCount >= 1, "the decoder to be created");

            long rendered = videoRenderer.RenderedFrames;

            // Asking for what is already in force is not a change: the resolver is asked again, and because the
            // answer is the decoder that is running, nothing is built.
            OperationResult<ReconcileResult> reconciled = Await(pipeline.Reconcile());

            Assert.That(reconciled.IsSuccessful, Is.True, reconciled.Message);

            WaitFor(() => videoRenderer.RenderedFrames > rendered, "playback to go on");
            Assert.That(decoders.RequestCount, Is.EqualTo(2), "the running decoder was offered again and not rebuilt");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A decoder that cannot be created must not take playback down with it: the running one stays, the reconcile
    /// reports why, and the stream carries on.
    /// </summary>
    [Test]
    public void KeepsTheRunningDecoderWhenTheReplacementFails()
    {
        string path = CreateY4mFile(frameCount: 60, frameRate: "30:1");
        try
        {
            RecordingOptionsVideoDecoderFactory decoders = new();
            FakeVideoRenderer videoRenderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(videoRenderer, new FakeAudioRenderer(), videoDecoders: decoders);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Await(pipeline.Play());
            WaitFor(() => videoRenderer.RenderedFrames > 2, "playback to be under way", 5000);

            long rendered = videoRenderer.RenderedFrames;

            // A decoder that refuses is the only one left, so nothing can be built to replace the running one â€” the
            // reconcile says so, and because nothing was published the picture never stops.
            pipeline.VideoChains[0].Components.ClearDecoders();
            pipeline.VideoChains[0].Components.Register(new RefusingVideoDecoderFactory());

            OperationResult<ReconcileResult> reconciled = Await(pipeline.Reconcile());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(reconciled.Result[pipeline.VideoChains[0]].IsSuccessful, Is.False, "a replacement that cannot be built is the caller's to see");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "a failed replacement is not a playback failure");
            }

            WaitFor(() => videoRenderer.RenderedFrames > rendered, "the running decoder to carry on");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A renderer that draws with a GPU offers the device it draws through. The pipeline has to hand it to the
    /// decoder it creates next: a decoder on that device produces textures the renderer can sample, and one on
    /// a device of its own produces frames that have to be copied back.
    /// </summary>
    [Test]
    public void GivesTheDecoderTheDeviceItsRendererDrawsWith()
    {
        GpuDeviceVideoRenderer renderer = new();
        RecordingVideoDecoderFactory decoders = new();

        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            using PlaybackPipeline pipeline = CreatePipeline(renderer, new FakeAudioRenderer(), videoDecoders: decoders);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            WaitFor(() => decoders.Options is not null, "the decoder to be created");

            // The whole thing, not just the handle: the lock that guards the device is what keeps the decode
            // thread and the drawing thread off one context at a time. What the pipeline handed over has to be the
            // device the renderer exposes, not something it worked out from the renderer's type.
            Assert.That(decoders.Options!.GpuDevice, Is.SameAs(renderer.GpuDevice));
            Assert.That(decoders.Options.BufferedFrames, Is.GreaterThanOrEqualTo(8), "the decoder has to know how many frames the pipeline keeps alive");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The sound's device belongs to one pipeline for one open. A renderer that outlived the open would carry
    /// its clock, its buffer and its flush into the next file â€” and a renderer shared between two pipelines
    /// would give them one clock, so the second file would play at the first one's position.
    /// </summary>
    [Test]
    public void CreatesItsOwnAudioRendererForEachOpen()
    {
        string path = CreateWaveFile();
        try
        {
            CreatingAudioRendererFactory factory = new();

            using (PlaybackPipeline first = CreatePipeline(factory))
            {
                Assert.That(Open(first, path).IsSuccessful, Is.True);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(factory.Created, Has.Count.EqualTo(1), "opening a file asks the factory for a device");
                    Assert.That(factory.Created[0].DisposeCount, Is.Zero, "the device is in use, so it is not let go of");
                }
            }

            FakeAudioRenderer letGo = factory.Created[0];
            Assert.That(letGo.DisposeCount, Is.EqualTo(1), "closing lets go of the device it was playing on");

            // A second pipeline from the same factory gets a device of its own rather than the one the first
            // pipeline disposed: that is what makes two pipelines two streams the audio engine mixes.
            using PlaybackPipeline second = CreatePipeline(factory);
            Assert.That(Open(second, path).IsSuccessful, Is.True);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(factory.Created, Has.Count.EqualTo(2), "a second pipeline gets a second device");
                Assert.That(factory.Created[1], Is.Not.SameAs(letGo));
                Assert.That(factory.Created[1].SubmittedSamples, Is.Zero);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A filter the host added is part of the audio chain: it is configured for the stream before the sound starts
    /// and every block handed to the device has been through it.
    /// </summary>
    [Test]
    public void RunsTheAudioFiltersTheHostAdded()
    {
        string path = CreateWaveFile();
        try
        {
            RecordingAudioFilter filter = new("record");
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), new FakeAudioRenderer());
            pipeline.AudioChains[0].Filters.Add(filter);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitFor(() => filter.AppliedBlocks > 0, "the chain to see the sound");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(filter.ConfiguredStream, Is.Not.Null, "a filter is configured for the stream before a block reaches it");
                Assert.That(pipeline.AudioChains[0].Filters.Filters, Is.EqualTo(new IAudioFilter[] { filter }));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A host that adds a fold down gets a device opened for the folded format, and blocks that are folded: the audio
    /// engine's own conversion is what this exists to avoid, because it divides the level down until no arrangement
    /// of the source could clip â€” about ten decibels quieter than the file.
    /// </summary>
    [Test]
    public void FoldsTheMixDownToWhatTheDevicePlays()
    {
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(2),
            AudioChannelCount = 6,
            AudioChannelLayout = ChannelLayout.Surround51,
        };

        FakeAudioRenderer renderer = new() { PlaysWhatItIsGiven = true, ChannelCapacity = 2 };
        SyntheticSourceProviderFactory sources = new(script);
        PlaybackPipeline pipeline = CreatePipeline(
            new FakeVideoRenderer(),
            renderer,
            videoDecoders: new SyntheticVideoDecoderFactory(),
            sourceFactory: sources,
            audioDecoders: new SyntheticAudioDecoderFactory());

        try
        {
            pipeline.AudioChains[0].Filters.Add(new DownmixAudioFilter());
            Assert.That(Open(pipeline, SyntheticPath()).IsSuccessful, Is.True);
            Assert.That(Select(pipeline, MediaStreamKind.Video, pipeline.Sources[0].Info.VideoStreams[0]).IsSuccessful, Is.True);
            Assert.That(Select(pipeline, MediaStreamKind.Audio, pipeline.Sources[0].Info.AudioStreams[0]).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => renderer.SubmittedBuffers > 0, "the sound to be playing");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(renderer.ConfiguredStream?.ChannelCount, Is.EqualTo(2), "the device is opened for what the fold produces");
                Assert.That(renderer.SubmittedChannelCounts, Is.All.EqualTo(2), "and every block it is handed matches");
            }
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// And a device that can play the stream as it is gets it untouched: the fold decides for itself, so nothing is
    /// converted, copied or opened for a format the file does not have.
    /// </summary>
    [Test]
    public void HandsTheStreamToADeviceThatCanPlayItAsItIs()
    {
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(2),
            AudioChannelCount = 6,
            AudioChannelLayout = ChannelLayout.Surround51,
        };

        FakeAudioRenderer renderer = new() { PlaysWhatItIsGiven = true, ChannelCapacity = 6 };
        SyntheticSourceProviderFactory sources = new(script);
        PlaybackPipeline pipeline = CreatePipeline(
            new FakeVideoRenderer(),
            renderer,
            videoDecoders: new SyntheticVideoDecoderFactory(),
            sourceFactory: sources,
            audioDecoders: new SyntheticAudioDecoderFactory());

        try
        {
            pipeline.AudioChains[0].Filters.Add(new DownmixAudioFilter());
            Assert.That(Open(pipeline, SyntheticPath()).IsSuccessful, Is.True);
            Assert.That(Select(pipeline, MediaStreamKind.Audio, pipeline.Sources[0].Info.AudioStreams[0]).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => renderer.SubmittedBuffers > 0, "the sound to be playing");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(renderer.ConfiguredStream?.ChannelCount, Is.EqualTo(6), "the device plays six channels, so it is asked for six");
                Assert.That(renderer.SubmittedChannelCounts, Is.All.EqualTo(6), "and the fold down has nothing to do");
            }
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// A filter that changes the format can join while the sound plays, and the device is opened again for what the
    /// chain now hands on: the chain's version is what tells the audio thread to look, and the reopening happens there
    /// so nothing is submitted in a shape the device was not opened for.
    /// </summary>
    [Test]
    public void ReconfiguresTheDeviceWhenAFilterChangesTheFormatWhilePlaying()
    {
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(4),
            AudioChannelCount = 6,
            AudioChannelLayout = ChannelLayout.Surround51,
        };

        FakeAudioRenderer renderer = new() { PlaysWhatItIsGiven = true, ChannelCapacity = 2 };
        SyntheticSourceProviderFactory sources = new(script);
        PlaybackPipeline pipeline = CreatePipeline(
            new FakeVideoRenderer(),
            renderer,
            videoDecoders: new SyntheticVideoDecoderFactory(),
            sourceFactory: sources,
            audioDecoders: new SyntheticAudioDecoderFactory());

        try
        {
            Assert.That(Open(pipeline, SyntheticPath()).IsSuccessful, Is.True);
            Assert.That(Select(pipeline, MediaStreamKind.Audio, pipeline.Sources[0].Info.AudioStreams[0]).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => renderer.SubmittedBuffers > 0, "the sound to be playing");
            Assert.That(renderer.ConfiguredStream?.ChannelCount, Is.EqualTo(6), "with no filter, the device is opened for the stream");

            pipeline.AudioChains[0].Filters.Add(new DownmixAudioFilter());

            WaitFor(() => renderer.SubmittedChannelCounts.Contains(2), "the device to be reopened for the folded shape");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(renderer.ConfigureCount, Is.GreaterThanOrEqualTo(2), "the device was opened again");
                Assert.That(renderer.ConfiguredStream?.ChannelCount, Is.EqualTo(2));
                Assert.That(renderer.SubmittedChannelCounts[^1], Is.EqualTo(2), "and the blocks after it are folded");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "a format change is not a stop");
            }
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// And a host that adds no fold down gets the stream as it is: the pipeline knows no filter exists, so a surround
    /// mix goes to a two-channel device unchanged â€” which is what handing it to the audio engine's own conversion is,
    /// and quieter for it. That is the price of the pipeline not knowing what a fold down is, and it is the host's
    /// line to write.
    /// </summary>
    [Test]
    public void HandsTheStreamOverUnfoldedWhenTheHostAddedNoFilter()
    {
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(2),
            AudioChannelCount = 6,
            AudioChannelLayout = ChannelLayout.Surround51,
        };

        FakeAudioRenderer renderer = new() { PlaysWhatItIsGiven = true, ChannelCapacity = 2 };
        SyntheticSourceProviderFactory sources = new(script);
        PlaybackPipeline pipeline = CreatePipeline(
            new FakeVideoRenderer(),
            renderer,
            videoDecoders: new SyntheticVideoDecoderFactory(),
            sourceFactory: sources,
            audioDecoders: new SyntheticAudioDecoderFactory());

        try
        {
            Assert.That(Open(pipeline, SyntheticPath()).IsSuccessful, Is.True);
            Assert.That(Select(pipeline, MediaStreamKind.Audio, pipeline.Sources[0].Info.AudioStreams[0]).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => renderer.SubmittedBuffers > 0, "the sound to be playing");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(renderer.ConfiguredStream?.ChannelCount, Is.EqualTo(6), "nothing narrowed it, so the stream is handed over as it is");
                Assert.That(renderer.SubmittedChannelCounts, Is.All.EqualTo(6));
            }
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// Editing the chain is immediate: the chain already knows the stream, so the filter takes part in the next
    /// block without a reconcile.
    /// </summary>
    [Test]
    public void TakesAFilterAddedWhilePlayingAtOnce()
    {
        string path = CreateWaveFile(sampleCount: SampleRate * 5);
        try
        {
            FakeAudioRenderer renderer = new();
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), renderer);
            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => renderer.SubmittedBuffers > 0, "the sound to be playing");

            RecordingAudioFilter late = new("late");
            pipeline.AudioChains[0].Filters.Add(late);

            WaitFor(() => late.AppliedBlocks > 0, "the new filter to take part without a reconcile");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(late.ConfiguredStream, Is.Not.Null, "the stream the chain already knew is what it was prepared for");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "adding a filter is not a restart");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A filter belongs to whoever added it, so taking it out of the chain hands it back rather than ending it â€”
    /// and the sound carries on without it, from the next block.
    /// </summary>
    [Test]
    public void StopsUsingARemovedFilterWithoutDisposingIt()
    {
        string path = CreateWaveFile(sampleCount: SampleRate * 5);
        try
        {
            FakeAudioRenderer renderer = new();
            RecordingAudioFilter filter = new("record");
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), renderer);
            pipeline.AudioChains[0].Filters.Add(filter);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => filter.AppliedBlocks > 0, "the chain to see the sound");

            Assert.That(pipeline.AudioChains[0].Filters.Remove(filter), Is.True);

            // A block already on its way may still carry the filter, so the count is taken once the device has
            // taken a block that was queued after the removal.
            int submitted = renderer.SubmittedBuffers;
            WaitFor(() => renderer.SubmittedBuffers > submitted, "the sound to carry on after the removal");
            int applied = filter.AppliedBlocks;

            submitted = renderer.SubmittedBuffers;
            WaitFor(() => renderer.SubmittedBuffers > submitted, "a second block after the removal");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(filter.AppliedBlocks, Is.EqualTo(applied), "a filter that was taken out is not run");
                Assert.That(filter.Disposed, Is.False, "removing a filter hands it back to its owner");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The level belongs to a filter the host added, so a filter added before anything is open is prepared for the
    /// stream the open brings, and the very first block the device is handed has already been through it.
    /// </summary>
    [Test]
    public void AppliesTheLevelOfAFilterAddedBeforeAnythingIsOpen()
    {
        string path = CreateWaveFile();
        try
        {
            CreatingAudioRendererFactory factory = new();
            using PlaybackPipeline pipeline = CreatePipeline(factory);

            GainAudioFilter gain = new();
            gain.SetLevel(0.25, false);
            pipeline.AudioChains[0].Filters.Add(gain);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => factory.Created.Count == 1 && factory.Created[0].LastSamples is not null, "the device to be handed a block");

            short[] samples = factory.Created[0].LastSamples!;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(samples.Any(sample => sample != 0), Is.True, "the block is the file's own sound, not silence");
                Assert.That(samples.Max(Math.Abs), Is.LessThanOrEqualTo(1992), "the loudest sample of the file is 7967, and a quarter of it is 1992");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The level survives a device swap, which is the point of moving it out of the renderer: the filter chain is
    /// not rebuilt when only the device changes, so what the host set keeps applying to what the new device is
    /// handed.
    /// </summary>
    [Test]
    public void KeepsTheLevelWhenTheAudioRendererIsSwapped()
    {
        string path = CreateWaveFile(sampleCount: SampleRate * 5);
        try
        {
            FakeAudioRenderer first = new() { Name = "first", QueuedSampleCount = SampleRate };
            FakeAudioRenderer second = new() { Name = "second", QueuedSampleCount = SampleRate };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), first);

            GainAudioFilter gain = new();
            gain.SetLevel(0.5, false);
            pipeline.AudioChains[0].Filters.Add(gain);

            Assert.That(Open(pipeline, path).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => first.LastSamples is not null, "the first device to be handed a block");

            SwapAudioRenderer(pipeline, new FixedAudioRendererFactory(second));
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);
            WaitFor(() => second.LastSamples is not null, "the new device to be handed a block");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(first.LastSamples!.Max(Math.Abs), Is.LessThanOrEqualTo(3984), "the level was applied before the swap");
                Assert.That(second.LastSamples!.Max(Math.Abs), Is.LessThanOrEqualTo(3984), "and it still is after it");
                Assert.That(second.LastSamples!.Any(sample => sample != 0), Is.True, "a level is not a silence");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A device can arrive after playback has begun â€” one that was plugged in, or one that replaced a failed. The
    /// sound has to still be there when it does. That depends on the read-ahead being bounded while no chain is
    /// decoding: a target the sound queue can never reach lets the reader take the whole file at once, and a device
    /// attached afterwards finds nothing left to play.
    /// </summary>
    [Test]
    public void TakesSoundWhenADeviceArrivesAfterPlaybackBegan()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            FakeAudioRenderer refusing = new() { CanConfigure = false, RefusalReason = "no device yet" };
            FakeAudioRenderer late = new() { Name = "late" };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), refusing);

            Assert.That(OpenWithoutSound(pipeline, path).IsSuccessful, Is.True, "the picture still plays without a device");
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.Position > TimeSpan.FromMilliseconds(300), "playback to be under way");

            // The device arriving is the host asking for the sound again, so the answer is a selection: what the file
            // still holds is read for it, from the position playback stands at.
            SwapAudioRenderer(pipeline, new FixedAudioRendererFactory(late));
            OperationResult chosen = Select(pipeline, MediaStreamKind.Audio, pipeline.Sources[0].Info.AudioStreams[0]);
            Assert.That(chosen.IsSuccessful, Is.True, chosen.Message);

            WaitFor(() => late.SubmittedBuffers > 0, "the device that arrived to be given sound");
            Assert.That(pipeline.AudioChains[0].RendererName, Is.EqualTo("late"));
            Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "the picture never stopped");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A device that arrives after the position was set still has to have its offset chosen. The picture armed the
    /// position â€” or an earlier device did â€” so arming is not a reason to choose one, and a device that is not synced
    /// to leaves the pipeline free-running on its own stopwatch for the rest of the position. A device running three
    /// times as fast is what makes the difference visible.
    /// </summary>
    [Test]
    public void SyncsToADeviceThatArrivesAfterThePositionWasSet()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            FakeAudioRenderer refusing = new() { CanConfigure = false, RefusalReason = "no device yet" };
            FakeAudioRenderer late = new() { Name = "late", ClockRate = 3.0 };
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), refusing);

            Assert.That(OpenWithoutSound(pipeline, path).IsSuccessful, Is.True, "the picture still plays without a device");
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            WaitFor(() => pipeline.Position > TimeSpan.FromMilliseconds(300), "playback to be under way");

            // The device arriving is the host asking for the sound again: one selection, and the position it restarts
            // at is the one playback has reached.
            SwapAudioRenderer(pipeline, new FixedAudioRendererFactory(late));
            OperationResult chosen = Select(pipeline, MediaStreamKind.Audio, pipeline.Sources[0].Info.AudioStreams[0]);
            Assert.That(chosen.IsSuccessful, Is.True, chosen.Message);
            WaitFor(() => late.SubmittedBuffers > 0, "the device that arrived to be given sound");

            // A device that holds sound is what makes the pipeline correct its clock, and this one runs at three times
            // real time: a clock that follows it travels three hundred milliseconds per hundred.
            late.QueuedSampleCount = SampleRate;
            TimeSpan before = pipeline.Position;
            Thread.Sleep(300);
            TimeSpan advanced = pipeline.Position - before;

            Assert.That(advanced, Is.GreaterThan(TimeSpan.FromMilliseconds(500)),
                "the clock follows the device that arrived, rather than free-running on its own stopwatch");
        }
        finally
        {
            File.Delete(path);
        }
    }

}
