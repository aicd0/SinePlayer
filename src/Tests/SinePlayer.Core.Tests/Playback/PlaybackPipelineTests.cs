// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Filter;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Abstractions.Timing;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Engine;
using SinePlayer.Core.Playback.Hosting;
using SinePlayer.Core.Playback.Timing;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Drives the whole orchestration with the real source and decoders but stand-in renderers, so
/// playback can be run to the end without a window and without a device. The fixtures are files the
/// test writes: a bitmap is a one frame video stream and a wave file is an audio stream.
/// </summary>
[TestFixture]
public partial class PlaybackPipelineTests
{
    private const int Width = 32;
    private const int Height = 24;
    private const int SampleRate = 44100;
    private const int ChannelCount = 2;
    private const int SampleCount = 8820;

    private int _liveThreadsBefore;

    /// <summary>
    /// Every test in this fixture is a pipeline and its threads, so every test is also a chance to leak one. What each
    /// test starts is counted before it runs and expected back when it ends, whether or not that test is about removal:
    /// a chain that keeps a thread of its own after it is gone fails here rather than in a profiler.
    /// </summary>
    [SetUp]
    public void RememberLiveThreads()
    {
        _liveThreadsBefore = PipelineThread.LiveCount;
    }

    [TearDown]
    public void ExpectTheThreadsBack()
    {
        int remaining = PipelineThread.LiveCount - _liveThreadsBefore;
        Assert.That(
            WaitForThreads(() => PipelineThread.LiveCount == _liveThreadsBefore),
            Is.True,
            $"{remaining} pipeline thread(s) were still running after the test ended");
    }

    /// <summary>
    /// A decoder that can be held inside a decode, so a test can move the position while the decoder is
    /// working on the old one â€” which is what a real seek does to a real decoder, and the window in which its
    /// tail describes a position that no longer exists.
    /// </summary>
    private sealed class GatedVideoDecoderFactory : IVideoDecoderFactory
    {
        private readonly FFmpegVideoDecoderFactory _inner = new();
        private readonly ManualResetEventSlim _gate = new(true);
        private readonly ManualResetEventSlim _decoding = new(false);
        internal void Close()
        {
            _gate.Reset();
            _decoding.Reset();
        }

        internal void Open()
        {
            _gate.Set();
        }

        /// <summary>Waits for the decoder to be inside a decode, which is when the gate holds it.</summary>
        internal bool WaitUntilDecoding() => _decoding.Wait(TimeSpan.FromSeconds(5));

        public OperationResult<IComponentCreator<IVideoDecoder>> Create(IMediaSource source, VideoStreamInfo stream, DecoderOptions options)
        {
            OperationResult<IComponentCreator<IVideoDecoder>> offered = _inner.Create(source, stream, options);
            return TestCreators.Wrap(offered, component => new GatedDecoder(component, this));
        }

        private sealed class GatedDecoder(IVideoDecoder inner, GatedVideoDecoderFactory owner) : IVideoDecoder
        {
            public string Name => inner.Name;
            public MediaStreamInfo Stream => inner.Stream;

            public bool IsHardwareAccelerated => inner.IsHardwareAccelerated;

            public PixelFormat OutputFormat => inner.OutputFormat;

            public OperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
            {
                owner._decoding.Set();
                owner._gate.Wait(TimeSpan.FromSeconds(5));
                return inner.TryDecode(packet, out frames);
            }

            public OperationResult Drain(out IReadOnlyList<RawVideoFrame> frames) => inner.Drain(out frames);

            public OperationResult Flush() => inner.Flush();

            public void Dispose() => inner.Dispose();
        }
    }

    /// <summary>
    /// A decoder that is slow on the first packet it is given, and on the first packet after every flush â€”
    /// which is what a seek costs a real decoder, and what makes the first frames of the new position late
    /// unless the clock waited for them.
    /// </summary>
    private sealed class SlowFirstDecodeVideoDecoderFactory : IVideoDecoderFactory
    {
        private readonly FFmpegVideoDecoderFactory _inner = new();

        internal int FirstDecodeMilliseconds { get; init; }

        /// <summary>How long each packet after the first takes, for a decoder that is slow at the start and
        /// then merely behind.</summary>
        internal int SubsequentDecodeMilliseconds { get; init; }
        public OperationResult<IComponentCreator<IVideoDecoder>> Create(IMediaSource source, VideoStreamInfo stream, DecoderOptions options)
        {
            OperationResult<IComponentCreator<IVideoDecoder>> offered = _inner.Create(source, stream, options);
            return TestCreators.Wrap(offered, component => new SlowFirstDecoder(component, FirstDecodeMilliseconds, SubsequentDecodeMilliseconds));
        }

        private sealed class SlowFirstDecoder(IVideoDecoder inner, int firstDecodeMilliseconds, int subsequentDecodeMilliseconds) : IVideoDecoder
        {
            private int _pending = 1;

            public string Name => inner.Name;
            public MediaStreamInfo Stream => inner.Stream;

            public bool IsHardwareAccelerated => inner.IsHardwareAccelerated;

            public PixelFormat OutputFormat => inner.OutputFormat;

            public OperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
            {
                bool first = Interlocked.Exchange(ref _pending, 0) == 1;
                int delay = first ? firstDecodeMilliseconds : subsequentDecodeMilliseconds;
                if (delay > 0)
                {
                    Thread.Sleep(delay);
                }

                return inner.TryDecode(packet, out frames);
            }

            public OperationResult Drain(out IReadOnlyList<RawVideoFrame> frames) => inner.Drain(out frames);

            public OperationResult Flush()
            {
                OperationResult flushed = inner.Flush();
                Interlocked.Exchange(ref _pending, 1);
                return flushed;
            }

            public void Dispose() => inner.Dispose();
        }
    }

    private static string SyntheticPath()
    {
        return Path.Combine(Path.GetTempPath(), "sineplayer-synthetic-fixture");
    }

    /// <summary>A locator of its own for each file a test gives the pipeline, since a file is known by where it came from.</summary>
    private static string SyntheticPath(string name)
    {
        return Path.Combine(Path.GetTempPath(), $"sineplayer-synthetic-{name}");
    }

    /// <summary>
    /// Looks for a position the file can only land *before*: past a packet boundary but inside the block the
    /// source resumes on. A seek to it therefore has a block of the position playback left in front of it, which
    /// is what a precise seek has to give up on before the clock is allowed to start.
    /// </summary>
    private static TimeSpan FindASeekThatLandsJustBefore(IMediaSource source)
    {
        for (var target = TimeSpan.FromMilliseconds(400); target <= TimeSpan.FromSeconds(2); target += TimeSpan.FromMilliseconds(5))
        {
            Assert.That(source.Seek(target, -1, SeekMode.Precise).IsSuccessful, Is.True);
            Assert.That(source.TryReadPacket(out MediaPacket? packet).IsSuccessful, Is.True);
            Assert.That(packet, Is.Not.Null, "the fixture has to hold something at every position tried");

            using (packet)
            {
                if (packet is not null && packet.Timestamp < target)
                {
                    return target;
                }
            }
        }

        return TimeSpan.Zero;
    }

    /// <summary>
    /// Looks for a position the file cannot resume at, well past a keyframe but before the next one, and
    /// hands back that position. Reading the packet after the seek is what says where the file went, since
    /// no demuxer promises to land exactly where it was pointed.
    /// </summary>
    private static TimeSpan FindASeekThatMisses(IMediaSource source)
    {
        var margin = TimeSpan.FromMilliseconds(300);
        for (var target = TimeSpan.FromMilliseconds(300); target <= TimeSpan.FromSeconds(2.6); target += TimeSpan.FromMilliseconds(20))
        {
            Assert.That(source.Seek(target, -1, SeekMode.Precise).IsSuccessful, Is.True);
            Assert.That(source.TryReadPacket(out MediaPacket? packet).IsSuccessful, Is.True);
            Assert.That(packet, Is.Not.Null, "the fixture has to hold something at every position tried");

            using (packet)
            {
                if (packet is not null && target - packet.Timestamp >= margin)
                {
                    return target;
                }
            }
        }

        return TimeSpan.Zero;
    }

    private static PlaybackPipeline CreatePipeline(
        IVideoRenderer videoRenderer,
        FakeAudioRenderer audioRenderer,
        IVideoDecoderFactory? videoDecoders = null,
        ISourceProviderFactory? sourceFactory = null,
        IAudioDecoderFactory? audioDecoders = null,
        ISubtitleDecoderFactory? subtitleDecoders = null,
        ISubtitleRendererFactory? subtitleRenderers = null,
        IVideoRenderTarget? videoTarget = null,
        IVideoRenderTarget? subtitleTarget = null)
    {
        PlaybackPipeline pipeline = new()
        {
            SourceFactory = sourceFactory ?? new FFmpegSourceProviderFactory(),
        };

        AddChains(pipeline, videoTarget, subtitleTarget);

        pipeline.VideoChains[0].Components.Register(videoDecoders ?? new FFmpegVideoDecoderFactory());
        pipeline.VideoChains[0].Components.Register(new FakeVideoRendererFactory(videoRenderer));
        pipeline.AudioChains[0].Components.Register(audioDecoders ?? new FFmpegAudioDecoderFactory());
        pipeline.AudioChains[0].Components.Register(new FixedAudioRendererFactory(audioRenderer));

        // A pipeline with no subtitle factory of its own has no subtitle support at all, which is what every test that
        // is not about subtitles wants: the kind stays unreachable rather than half-wired.
        if (subtitleDecoders is not null)
        {
            pipeline.SubtitleChains[0].Components.Register(subtitleDecoders);
        }

        if (subtitleRenderers is not null)
        {
            pipeline.SubtitleChains[0].Components.Register(subtitleRenderers);
        }

        return pipeline;
    }

    /// <summary>
    /// A pipeline whose sound devices are made by the factory under test, with a stand-in picture so the file
    /// only has to contain audio.
    /// </summary>
    private static PlaybackPipeline CreatePipeline(IAudioRendererFactory audioRendererFactory)
    {
        PlaybackPipeline pipeline = new() { SourceFactory = new FFmpegSourceProviderFactory() };
        AddChains(pipeline, null, null);

        pipeline.VideoChains[0].Components.Register(new FFmpegVideoDecoderFactory());
        pipeline.VideoChains[0].Components.Register(new FakeVideoRendererFactory(new FakeVideoRenderer()));
        pipeline.AudioChains[0].Components.Register(new FFmpegAudioDecoderFactory());
        pipeline.AudioChains[0].Components.Register(audioRendererFactory);
        return pipeline;
    }

    /// <summary>
    /// A signature nothing else will have. The pipeline decides what to swap by comparing signatures, so a stand-in
    /// that wants to be recognised as a *new* component has to say something new â€” the same stand-in handed over
    /// again keeps the signature it already had, which is what "not a change" has to look like.
    /// </summary>
    private static string NextSignature(string kind)
    {
        return $"{kind}-{Interlocked.Increment(ref _signatures)}";
    }

    private static int _signatures;

    /// <summary>
    /// Puts a new renderer in place of the one the pipeline is drawing with. A test holds the factory it
    /// registered, not the instance the pipeline built from it, so the role is emptied instead of one entry being
    /// replaced â€” which is what a host that wants "this one instead" does anyway.
    /// </summary>
    private static void SwapVideoRenderer(PlaybackPipeline pipeline, IVideoRendererFactory factory)
    {
        pipeline.VideoChains[0].Components.ClearRenderers();
        pipeline.VideoChains[0].Components.Register(factory);
    }

    private static void SwapAudioRenderer(PlaybackPipeline pipeline, IAudioRendererFactory factory)
    {
        pipeline.AudioChains[0].Components.ClearRenderers();
        pipeline.AudioChains[0].Components.Register(factory);
    }

    /// <summary>A pipeline whose registrations are made by the test, which is what a host does.</summary>
    private static PlaybackPipeline CreatePipeline(Action<PlaybackPipeline> register)
    {
        PlaybackPipeline pipeline = new() { SourceFactory = new FFmpegSourceProviderFactory() };
        AddChains(pipeline, null, null);
        register(pipeline);
        return pipeline;
    }

    /// <summary>
    /// The three chains every test gets unless it says otherwise: a picture surface to draw into, the sound, and a
    /// surface of its own for the words. A surface belongs to a chain for its whole life, so it is chosen here rather
    /// than at open time.
    /// </summary>
    private static void AddChains(PlaybackPipeline pipeline, IVideoRenderTarget? videoTarget, IVideoRenderTarget? subtitleTarget)
    {
        Await(pipeline.AddVideoChain(videoTarget ?? new FakeRenderTarget()));
        Await(pipeline.AddAudioChain());

        // A host names the picture its words belong to; the pipeline no longer picks one.
        ISubtitleChain subtitles = Await(pipeline.AddSubtitleChain(subtitleTarget ?? SubtitleSurfaceTarget));
        subtitles.LayoutVideoChain = pipeline.VideoChains[0];
    }

    /// <summary>
    /// A decoder that refuses every stream in the one place a refusal can be explained â€” the creator â€” which is
    /// what a factory that cannot handle a codec does.
    /// </summary>
    private sealed class RefusingVideoDecoderFactory : IVideoDecoderFactory
    {
        public OperationResult<IComponentCreator<IVideoDecoder>> Create(IMediaSource source, VideoStreamInfo stream, DecoderOptions options)
        {
            IComponentCreator<IVideoDecoder> creator = new Creator();
            return new OperationResultCreator<IComponentCreator<IVideoDecoder>>().Success(creator);
        }

        private sealed class Creator : IComponentCreator<IVideoDecoder>
        {
            public string Signature => "refusing:video";

            public OperationResult Create(out IVideoDecoder? component)
            {
                component = null;
                return OperationResult.Failure("This decoder refuses the stream.");
            }
        }
    }

    /// <summary>The real decoder, wrapped so that a test can read the settings each request was made with.</summary>
    private sealed class RecordingOptionsVideoDecoderFactory : IVideoDecoderFactory
    {
        private readonly FFmpegVideoDecoderFactory _inner = new();
        private readonly List<DecoderOptions> _requests = [];

        internal int RequestCount
        {
            get
            {
                lock (_requests)
                {
                    return _requests.Count;
                }
            }
        }

        /// <summary>The requests, in order. Read it through <see cref="GetRequest"/> so a test never races one.</summary>
        internal DecoderOptions GetRequest(int index)
        {
            lock (_requests)
            {
                return _requests[index];
            }
        }

        public OperationResult<IComponentCreator<IVideoDecoder>> Create(IMediaSource source, VideoStreamInfo stream, DecoderOptions options)
        {
            lock (_requests)
            {
                _requests.Add(options);
            }

            return _inner.Create(source, stream, options);
        }
    }

    /// <summary>
    /// A decoder that serves only the kind of request it declares: the one that was asked for serves it, and the
    /// other kind offers nothing rather than quietly serving it anyway â€” which is what makes the requirement a
    /// requirement and the order alone unable to override it.
    /// </summary>
    /// <summary>
    /// The real decoder, decoding exactly as it always would, saying something else about where it runs â€” which is
    /// the one thing a report can be tested against without depending on what the machine can do.
    /// </summary>
    /// <summary>
    /// A synthetic decoder that is asked the way its chain's preferences say, and remembers how it was asked â€” which
    /// is what a test can pin about "hardware or not" without depending on the machine.
    /// </summary>
    private sealed class RecordingSyntheticVideoDecoderFactory : IVideoDecoderFactory
    {
        private readonly SyntheticVideoDecoderFactory _inner = new();
        private readonly List<bool> _hardwareRequests = [];

        internal bool[] HardwareRequests
        {
            get
            {
                lock (_hardwareRequests)
                {
                    return [.. _hardwareRequests];
                }
            }
        }

        public OperationResult<IComponentCreator<IVideoDecoder>> Create(IMediaSource source, VideoStreamInfo stream, DecoderOptions options)
        {
            lock (_hardwareRequests)
            {
                _hardwareRequests.Add(options.HardwareDecoding);
            }

            return _inner.Create(source, stream, options);
        }
    }
    private sealed class FlaggedVideoDecoderFactory(bool hardware, string signature) : IVideoDecoderFactory
    {
        private readonly FFmpegVideoDecoderFactory _inner = new();

        public OperationResult<IComponentCreator<IVideoDecoder>> Create(IMediaSource source, VideoStreamInfo stream, DecoderOptions options)
        {
            OperationResult<IComponentCreator<IVideoDecoder>> offered = _inner.Create(source, stream, options);
            if (!offered.IsSuccessful)
            {
                return offered;
            }

            IComponentCreator<IVideoDecoder> creator = new Creator(offered.Result, hardware, signature);
            return new OperationResultCreator<IComponentCreator<IVideoDecoder>>().Success(creator);
        }

        private sealed class Creator(IComponentCreator<IVideoDecoder> inner, bool hardware, string signature) : IComponentCreator<IVideoDecoder>
        {
            public string Signature => signature;

            public OperationResult Create(out IVideoDecoder? component)
            {
                OperationResult created = inner.Create(out IVideoDecoder? made);
                component = created.IsSuccessful && made is not null ? new Decoder(made, hardware) : null;
                return created;
            }
        }

        private sealed class Decoder(IVideoDecoder inner, bool hardware) : IVideoDecoder
        {
            public string Name => inner.Name;
            public MediaStreamInfo Stream => inner.Stream;

            public bool IsHardwareAccelerated => hardware;

            public PixelFormat OutputFormat => inner.OutputFormat;

            public OperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> buffers)
            {
                return inner.TryDecode(packet, out buffers);
            }

            public OperationResult Drain(out IReadOnlyList<RawVideoFrame> buffers)
            {
                return inner.Drain(out buffers);
            }

            public OperationResult Flush()
            {
                return inner.Flush();
            }

            public void Dispose()
            {
                inner.Dispose();
            }
        }
    }

    /// <summary>
    /// Hands out the one renderer the test is going to inspect. The pipeline creates its own renderer per
    /// open and disposes it on close, so this is only sound because the stand-in's dispose does nothing and
    /// it can be configured again â€” which is exactly what the tests need to read its counters afterwards.
    /// </summary>
    private sealed class FixedAudioRendererFactory : IAudioRendererFactory
    {
        private readonly IAudioRenderer _renderer;
        private readonly string _signature = NextSignature("fixed-audio");

        internal FixedAudioRendererFactory(IAudioRenderer renderer)
        {
            _renderer = renderer;
        }

        public OperationResult<IComponentCreator<IAudioRenderer>> Create()
        {
            return new OperationResultCreator<IComponentCreator<IAudioRenderer>>().Success(new FixedCreator<IAudioRenderer>(_signature, () => _renderer));
        }
    }

    /// <summary>
    /// A filter that records what the chain asked of it. It is added by the test itself, which is how a real host
    /// uses the chain â€” and which is why the pipeline never sees the type.
    /// </summary>
    private sealed class RecordingAudioFilter(string name) : IAudioFilter
    {
        private int _appliedBlocks;

        public string Name => name;

        public AudioStreamInfo? ConfiguredStream { get; private set; }

        public AudioStreamInfo? ConfiguredTarget { get; private set; }

        public int AppliedBlocks => Volatile.Read(ref _appliedBlocks);

        public bool Disposed { get; private set; }

        public AudioStreamInfo? OutputFormat => null;

        public OperationResult Configure(AudioStreamInfo stream, AudioStreamInfo target)
        {
            ConfiguredStream = stream;
            ConfiguredTarget = target;
            return OperationResult.Success();
        }

        public OperationResult Apply(RawAudioBuffer input, out RawAudioBuffer output)
        {
            Interlocked.Increment(ref _appliedBlocks);
            output = input;
            return OperationResult.Success();
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    /// <summary>
    /// Hands out a fresh stand-in device per call, which is what the real factory does, and keeps every one it
    /// made so a test can see both how many opens asked for a device and what became of each.
    /// </summary>
    private sealed class CreatingAudioRendererFactory : IAudioRendererFactory
    {
        private readonly object _lock = new();
        private readonly string _signature = NextSignature("created-audio");

        public List<FakeAudioRenderer> Created { get; } = [];

        public OperationResult<IComponentCreator<IAudioRenderer>> Create()
        {
            FakeAudioRenderer renderer = new();
            lock (_lock)
            {
                Created.Add(renderer);
            }

            return new OperationResultCreator<IComponentCreator<IAudioRenderer>>().Success(new FixedCreator<IAudioRenderer>(_signature, () => renderer));
        }
    }

    /// <summary>A video decoder factory that refuses one stream index, which is a codec nothing on this machine can take.</summary>
    private sealed class RefusingIndexVideoDecoderFactory : IVideoDecoderFactory
    {
        private readonly FFmpegVideoDecoderFactory _inner = new();

        /// <summary>The container index this factory refuses. Set after the file is open, which is when it is known.</summary>
        internal int RefusedIndex { get; set; } = -1;

        public OperationResult<IComponentCreator<IVideoDecoder>> Create(IMediaSource source, VideoStreamInfo stream, DecoderOptions options)
        {
            if (stream.Index == RefusedIndex)
            {
                return new OperationResultCreator<IComponentCreator<IVideoDecoder>>().Failure($"Stream {stream.Index} cannot be decoded.");
            }

            return _inner.Create(source, stream, options);
        }
    }

    /// <summary>The real audio decoder wrapped so that its end-of-stream drain refuses.</summary>
    private sealed class BrokenDrainAudioDecoderFactory : IAudioDecoderFactory
    {
        private readonly FFmpegAudioDecoderFactory _inner = new();

        public OperationResult<IComponentCreator<IAudioDecoder>> Create(IMediaSource source, AudioStreamInfo stream, DecoderOptions options)
        {
            OperationResult<IComponentCreator<IAudioDecoder>> offered = _inner.Create(source, stream, options);
            return TestCreators.Wrap(offered, component => new BrokenDrainAudioDecoder(component));
        }

        private sealed class BrokenDrainAudioDecoder(IAudioDecoder inner) : IAudioDecoder
        {
            public string Name => inner.Name;
            public MediaStreamInfo Stream => inner.Stream;

            public bool IsHardwareAccelerated => inner.IsHardwareAccelerated;

            public AudioSampleFormat OutputFormat => inner.OutputFormat;

            public OperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawAudioBuffer> buffers)
            {
                return inner.TryDecode(packet, out buffers);
            }

            public OperationResult Drain(out IReadOnlyList<RawAudioBuffer> buffers)
            {
                buffers = [];
                return OperationResult.Failure("The decoder could not flush its tail.");
            }

            public OperationResult Flush()
            {
                return inner.Flush();
            }

            public void Dispose()
            {
                inner.Dispose();
            }
        }
    }

    /// <summary>
    /// The real decoder wrapped so that its end-of-stream drain is broken â€” the shape of a hardware decoder whose
    /// device has gone by the time the file ends. Either it reports a failure, or it breaks the contract and says it
    /// succeeded without handing anything back.
    /// </summary>
    private sealed class BrokenDrainVideoDecoderFactory(bool returnsNothing) : IVideoDecoderFactory
    {
        private readonly FFmpegVideoDecoderFactory _inner = new();

        public OperationResult<IComponentCreator<IVideoDecoder>> Create(IMediaSource source, VideoStreamInfo stream, DecoderOptions options)
        {
            OperationResult<IComponentCreator<IVideoDecoder>> offered = _inner.Create(source, stream, options);
            return TestCreators.Wrap(offered, component => new BrokenDrainVideoDecoder(component, returnsNothing));
        }

        private sealed class BrokenDrainVideoDecoder(IVideoDecoder inner, bool returnsNothing) : IVideoDecoder
        {
            public string Name => inner.Name;
            public MediaStreamInfo Stream => inner.Stream;

            public bool IsHardwareAccelerated => inner.IsHardwareAccelerated;

            public PixelFormat OutputFormat => inner.OutputFormat;

            public OperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
            {
                return inner.TryDecode(packet, out frames);
            }

            public OperationResult Drain(out IReadOnlyList<RawVideoFrame> frames)
            {
                if (returnsNothing)
                {
                    frames = null!;
                    return OperationResult.Success();
                }

                frames = [];
                return OperationResult.Failure("The decoder could not flush its tail.");
            }

            public OperationResult Flush()
            {
                return inner.Flush();
            }

            public void Dispose()
            {
                inner.Dispose();
            }
        }
    }

    /// <summary>
    /// The real decoder wrapped so that it keeps everything it decodes until it is drained, which is how a
    /// codec with output delay behaves: the frames of the last packets are only released when the decoder is
    /// told the stream has ended. It is also the strictest form of the question the pipeline answers at the end
    /// of a file â€” the tail is not "the frames that came out of the last packet", it is "whatever the decoder
    /// is still holding", and nothing but the drain can produce it.
    /// </summary>
    private sealed class DelayedVideoDecoderFactory : IVideoDecoderFactory
    {
        private readonly FFmpegVideoDecoderFactory _inner = new();
        public OperationResult<IComponentCreator<IVideoDecoder>> Create(IMediaSource source, VideoStreamInfo stream, DecoderOptions options)
        {
            OperationResult<IComponentCreator<IVideoDecoder>> offered = _inner.Create(source, stream, options);
            return TestCreators.Wrap(offered, component => new DelayedVideoDecoder(component));
        }

        private sealed class DelayedVideoDecoder : IVideoDecoder
        {
            private readonly IVideoDecoder _inner;
            private readonly List<RawVideoFrame> _held = [];

            internal DelayedVideoDecoder(IVideoDecoder inner)
            {
                _inner = inner;
            }

            public string Name => _inner.Name;
            public MediaStreamInfo Stream => _inner.Stream;

            public bool IsHardwareAccelerated => _inner.IsHardwareAccelerated;

            public PixelFormat OutputFormat => _inner.OutputFormat;

            public OperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
            {
                OperationResult decoded = _inner.TryDecode(packet, out IReadOnlyList<RawVideoFrame> produced);
                if (decoded.IsSuccessful)
                {
                    _held.AddRange(produced);
                }

                frames = [];
                return decoded;
            }

            public OperationResult Drain(out IReadOnlyList<RawVideoFrame> frames)
            {
                OperationResult drained = _inner.Drain(out IReadOnlyList<RawVideoFrame> tail);
                if (!drained.IsSuccessful)
                {
                    frames = tail;
                    return drained;
                }

                _held.AddRange(tail);
                frames = _held.ToArray();
                _held.Clear();
                return OperationResult.Success();
            }

            public OperationResult Flush()
            {
                ReleaseHeld();
                return _inner.Flush();
            }

            public void Dispose()
            {
                ReleaseHeld();
                _inner.Dispose();
            }

            private void ReleaseHeld()
            {
                foreach (RawVideoFrame frame in _held)
                {
                    frame.Dispose();
                }

                _held.Clear();
            }
        }
    }

    /// <summary>
    /// The real packets of a file, wrapped so that the stream never ends and never moves: a source that
    /// refuses to be seeked is a live one, and a live one has no end to reach. No file on disk refuses, so
    /// this is the only way to reach either refusal path with a real file behind it.
    /// </summary>
    private sealed class LiveRefusingSourceFactory(bool refusesInAdvance) : ISourceProviderFactory
    {
        private readonly FFmpegSourceProviderFactory _inner = new();
        public OperationResult TryOpen(MediaLocator locator, out IMediaSource? source)
        {
            OperationResult opened = _inner.TryOpen(locator, out IMediaSource? inner);
            source = opened.IsSuccessful && inner is not null ? new LiveRefusingSource(inner, refusesInAdvance) : inner;
            return opened;
        }

        private sealed class LiveRefusingSource(IMediaSource inner, bool refusesInAdvance) : IMediaSource
        {
            public MediaInfo Info => inner.Info;

            public bool CanSeek => !refusesInAdvance;

            public OperationResult Seek(TimeSpan position, int streamIndex, SeekMode mode)
            {
                return OperationResult.Failure("This source refuses to move.");
            }

            public bool TryChooseSeekStream(IReadOnlyList<int> streamIndexes, TimeSpan position, SeekMode mode, out int streamIndex)
            {
                // Answering would move the file it refuses to be moved, so it cannot say which stream a seek should use.
                streamIndex = -1;
                return false;
            }

            public OperationResult TryReadPacket(out MediaPacket? packet)
            {
                OperationResult read = inner.TryReadPacket(out packet);
                if (read.IsSuccessful && packet is null)
                {
                    // The stream loops rather than ends, which is what a live one does.
                    inner.Seek(TimeSpan.Zero, -1, SeekMode.Precise);
                    read = inner.TryReadPacket(out packet);
                }

                // Paced a little, so a stream without an end does not spin the reader as fast as it can read.
                Thread.Sleep(1);
                return read;
            }

            public void Dispose()
            {
                inner.Dispose();
            }
        }
    }

    /// <summary>
    /// The real audio decoder, slowed down and counted. Slowed so that a failure elsewhere lands while the
    /// reader is still in the middle of reading the file â€” the reader decodes the sound inline, so this is
    /// what paces it â€” and counted because a call to this decoder is the reader having read another audio
    /// packet, which is the only way a test can see that a failed playback stopped reading.
    /// </summary>
    private sealed class PacedAudioDecoderFactory : IAudioDecoderFactory
    {
        /// <summary>How long one decode takes: the default keeps the reader just slow enough that a failure lands inside its read-ahead.</summary>
        private const int DecodePaceMilliseconds = 10;

        private readonly FFmpegAudioDecoderFactory _inner = new();
        private int _decodes;

        /// <summary>How long one decode takes, when a test needs the pipe to be slower than the device.</summary>
        internal int DecodeMilliseconds { get; init; } = DecodePaceMilliseconds;

        public int Decodes => Volatile.Read(ref _decodes);
        public OperationResult<IComponentCreator<IAudioDecoder>> Create(IMediaSource source, AudioStreamInfo stream, DecoderOptions options)
        {
            OperationResult<IComponentCreator<IAudioDecoder>> offered = _inner.Create(source, stream, options);
            return TestCreators.Wrap(offered, component => new PacedAudioDecoder(component, this, DecodeMilliseconds));
        }

        private sealed class PacedAudioDecoder(IAudioDecoder inner, PacedAudioDecoderFactory owner, int decodeMilliseconds) : IAudioDecoder
        {
            public string Name => inner.Name;
            public MediaStreamInfo Stream => inner.Stream;

            public bool IsHardwareAccelerated => inner.IsHardwareAccelerated;

            public AudioSampleFormat OutputFormat => inner.OutputFormat;

            public OperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawAudioBuffer> buffers)
            {
                Interlocked.Increment(ref owner._decodes);
                Thread.Sleep(decodeMilliseconds);
                return inner.TryDecode(packet, out buffers);
            }

            public OperationResult Drain(out IReadOnlyList<RawAudioBuffer> buffers)
            {
                return inner.Drain(out buffers);
            }

            public OperationResult Flush()
            {
                return inner.Flush();
            }

            public void Dispose()
            {
                inner.Dispose();
            }
        }
    }

    /// <summary>
    /// The real decoder wrapped so that it throws instead of returning a failure, which is how a decoder
    /// reaches it when the fault comes out of interop rather than out of a return code. The pipeline catches
    /// on the decode thread, and this is what keeps that catch from being emptied: without it, a throwing
    /// decoder kills the thread silently â€” playback stays `Playing` with a picture that never moves and
    /// nothing anywhere says why, which is the worst failure this design can have.
    /// </summary>
    private sealed class ThrowingVideoDecoderFactory : IVideoDecoderFactory
    {
        private readonly FFmpegVideoDecoderFactory _inner = new();

        /// <summary>How many packets decode cleanly before the decoder throws.</summary>
        internal int PacketsBeforeThrow { get; init; } = 1;
        public OperationResult<IComponentCreator<IVideoDecoder>> Create(IMediaSource source, VideoStreamInfo stream, DecoderOptions options)
        {
            OperationResult<IComponentCreator<IVideoDecoder>> offered = _inner.Create(source, stream, options);
            return TestCreators.Wrap(offered, component => new ThrowingVideoDecoder(component, PacketsBeforeThrow));
        }

        private sealed class ThrowingVideoDecoder : IVideoDecoder
        {
            private readonly IVideoDecoder _inner;
            private readonly int _packetsBeforeThrow;
            private int _decoded;

            internal ThrowingVideoDecoder(IVideoDecoder inner, int packetsBeforeThrow)
            {
                _inner = inner;
                _packetsBeforeThrow = packetsBeforeThrow;
            }

            public string Name => _inner.Name;
            public MediaStreamInfo Stream => _inner.Stream;

            public bool IsHardwareAccelerated => _inner.IsHardwareAccelerated;

            public PixelFormat OutputFormat => _inner.OutputFormat;

            public OperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
            {
                if (_decoded++ >= _packetsBeforeThrow)
                {
                    throw new InvalidOperationException("The decoder failed on this stream.");
                }

                return _inner.TryDecode(packet, out frames);
            }

            public OperationResult Drain(out IReadOnlyList<RawVideoFrame> frames)
            {
                return _inner.Drain(out frames);
            }

            public OperationResult Flush()
            {
                return _inner.Flush();
            }

            public void Dispose()
            {
                _inner.Dispose();
            }
        }
    }

    /// <summary>
    /// A video decoder that never produces a frame. It exists so a source other than the engine's own can be
    /// played at all â€” the real decoder insists on an FFmpeg source â€” which is what makes the refusal paths
    /// above reachable with a real file behind them.
    /// </summary>
    private sealed class SilentVideoDecoderFactory : IVideoDecoderFactory
    {
        public OperationResult<IComponentCreator<IVideoDecoder>> Create(IMediaSource source, VideoStreamInfo stream, DecoderOptions options)
        {
            IComponentCreator<IVideoDecoder> creator = new FixedCreator<IVideoDecoder>("silent:video", () => new SilentVideoDecoder(stream));
            return new OperationResultCreator<IComponentCreator<IVideoDecoder>>().Success(creator);
        }

        private sealed class SilentVideoDecoder(VideoStreamInfo stream) : IVideoDecoder
        {
            public string Name => "silent";
            public MediaStreamInfo Stream => stream;

            public bool IsHardwareAccelerated => false;

            public PixelFormat OutputFormat => PixelFormat.Yuv420P;

            public OperationResult Flush()
            {
                return OperationResult.Success();
            }

            public OperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
            {
                frames = [];
                return OperationResult.Success();
            }

            public OperationResult Drain(out IReadOnlyList<RawVideoFrame> frames)
            {
                frames = [];
                return OperationResult.Success();
            }

            public void Dispose()
            {
            }
        }
    }

    /// <summary>
    /// The real decoder wrapped so that it gives up after a few packets, which is how a codec that comes
    /// across something it cannot handle behaves. It pins the other half of the rule the refusal tests pin: a
    /// command that cannot be carried out is the caller's business, but a decoder that dies is playback's.
    /// </summary>
    private sealed class FailingVideoDecoderFactory : IVideoDecoderFactory
    {
        private readonly FFmpegVideoDecoderFactory _inner = new();

        /// <summary>How many packets decode cleanly before the decoder gives up.</summary>
        internal int PacketsBeforeFailure { get; init; } = 1;
        public OperationResult<IComponentCreator<IVideoDecoder>> Create(IMediaSource source, VideoStreamInfo stream, DecoderOptions options)
        {
            OperationResult<IComponentCreator<IVideoDecoder>> offered = _inner.Create(source, stream, options);
            return TestCreators.Wrap(offered, component => new FailingVideoDecoder(component, PacketsBeforeFailure));
        }

        private sealed class FailingVideoDecoder : IVideoDecoder
        {
            private readonly IVideoDecoder _inner;
            private readonly int _packetsBeforeFailure;
            private int _decoded;

            internal FailingVideoDecoder(IVideoDecoder inner, int packetsBeforeFailure)
            {
                _inner = inner;
                _packetsBeforeFailure = packetsBeforeFailure;
            }

            public string Name => _inner.Name;
            public MediaStreamInfo Stream => _inner.Stream;

            public bool IsHardwareAccelerated => _inner.IsHardwareAccelerated;

            public PixelFormat OutputFormat => _inner.OutputFormat;

            public OperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
            {
                if (_decoded++ >= _packetsBeforeFailure)
                {
                    frames = [];
                    return OperationResult.Failure("The decoder cannot decode this stream.");
                }

                return _inner.TryDecode(packet, out frames);
            }

            public OperationResult Drain(out IReadOnlyList<RawVideoFrame> frames)
            {
                return _inner.Drain(out frames);
            }

            public OperationResult Flush()
            {
                return _inner.Flush();
            }

            public void Dispose()
            {
                _inner.Dispose();
            }
        }
    }

    /// <summary>
    /// The real decoder with a delay added to every packet it is given, which is the one thing a decoder that
    /// keeps up never does and a decoder that cannot the only thing it does.
    /// </summary>
    private sealed class ThrottledVideoDecoderFactory : IVideoDecoderFactory
    {
        private readonly FFmpegVideoDecoderFactory _inner = new();

        /// <summary>How long one packet takes to decode, which is what makes the decoder too slow.</summary>
        internal int DecodeMilliseconds { get; init; }

        /// <summary>
        /// How many packets are decoded without the delay, before it starts. A decoder that is slow from the
        /// very first packet leaves the picture behind the clock before there is anything to judge; one that
        /// decodes a frame and then goes quiet for seconds is what a picture *starting* to fall behind looks
        /// like, and it is the arrangement in which the picture's verdict has nothing to wake it.
        /// </summary>
        internal int FastPackets { get; init; }
        public OperationResult<IComponentCreator<IVideoDecoder>> Create(IMediaSource source, VideoStreamInfo stream, DecoderOptions options)
        {
            OperationResult<IComponentCreator<IVideoDecoder>> offered = _inner.Create(source, stream, options);
            return TestCreators.Wrap(offered, component => new ThrottledVideoDecoder(component, DecodeMilliseconds, FastPackets));
        }

        private sealed class ThrottledVideoDecoder : IVideoDecoder
        {
            private readonly IVideoDecoder _inner;
            private readonly int _decodeMilliseconds;
            private readonly int _fastPackets;
            private int _decoded;

            internal ThrottledVideoDecoder(IVideoDecoder inner, int decodeMilliseconds, int fastPackets)
            {
                _inner = inner;
                _decodeMilliseconds = decodeMilliseconds;
                _fastPackets = fastPackets;
            }

            public string Name => _inner.Name;
            public MediaStreamInfo Stream => _inner.Stream;

            public bool IsHardwareAccelerated => _inner.IsHardwareAccelerated;

            public PixelFormat OutputFormat => _inner.OutputFormat;

            public OperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
            {
                if (Interlocked.Increment(ref _decoded) > _fastPackets)
                {
                    Thread.Sleep(_decodeMilliseconds);
                }

                return _inner.TryDecode(packet, out frames);
            }

            public OperationResult Drain(out IReadOnlyList<RawVideoFrame> frames)
            {
                return _inner.Drain(out frames);
            }

            public OperationResult Flush()
            {
                return _inner.Flush();
            }

            public void Dispose()
            {
                _inner.Dispose();
            }
        }
    }

    /// <summary>
    /// A renderer that draws with a GPU and says so. Nothing is drawn: the tests that use it are about what the
    /// pipeline does with the offer, not about pixels. It offers the general device only â€” an API's own vocabulary is
    /// a promise about what that renderer can do with it, and a stand-in with no handle behind it must not make one.
    /// </summary>
    private sealed class GpuDeviceVideoRenderer : IVideoRenderer, IGpuDevice
    {
        public string Name => "gpu-device-video";

        /// <summary>It is its own device, which is what a renderer that draws with one hands out.</summary>
        public IGpuDevice? GpuDevice => this;

        public OperationResult CanDisplayGpuFrame(PixelFormat format)
        {
            return OperationResult.Success();
        }

        public OperationResult Configure(VideoStreamInfo stream, IVideoRenderTarget target)
        {
            return OperationResult.Success();
        }

        public OperationResult Render(RawVideoFrame frame, VideoTransform transform)
        {
            return OperationResult.Success();
        }

        public OperationResult RefreshSurface(VideoTransform transform)
        {
            return OperationResult.Success();
        }

        public OperationResult ClearSurface()
        {
            return OperationResult.Success();
        }

        public bool TryAcquireFrameSlot()
        {
            return true;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A video decoder that produces no frames and remembers the settings it was created with, which is how a
    /// test reads what the pipeline handed its decoder.
    /// </summary>
    private sealed class RecordingVideoDecoderFactory : IVideoDecoderFactory
    {
        private readonly SilentVideoDecoderFactory _inner = new();

        internal DecoderOptions? Options { get; private set; }
        public OperationResult<IComponentCreator<IVideoDecoder>> Create(IMediaSource source, VideoStreamInfo stream, DecoderOptions options)
        {
            Options = options;
            return _inner.Create(source, stream, options);
        }
    }

    /// <summary>
    /// Hands the same stand-in renderer to the pipeline. The pipeline creates its renderer itself, on
    /// the thread that will use it, so that the graphics device belongs to that apartment.
    /// </summary>
    private sealed class FakeVideoRendererFactory : IVideoRendererFactory
    {
        private readonly IVideoRenderer _renderer;
        private readonly string _signature = NextSignature("fake-video");

        internal FakeVideoRendererFactory(IVideoRenderer renderer)
        {
            _renderer = renderer;
        }

        public OperationResult<IComponentCreator<IVideoRenderer>> Create()
        {
            return new OperationResultCreator<IComponentCreator<IVideoRenderer>>().Success(new FixedCreator<IVideoRenderer>(_signature, () => _renderer));
        }
    }

    /// <summary>
    /// Opens a file and waits for the answer, because whoever opens a file has to know whether it opened. The surfaces
    /// come first, as a host does it, and the file is added to what the reset left behind.
    /// </summary>
    private static OperationResult Open(PlaybackPipeline pipeline, string path)
    {
        OperationResult opened = OpenFirstSource(pipeline, MediaLocator.Parse(path));
        if (!opened.IsSuccessful)
        {
            return opened;
        }

        // Opening does not choose tracks â€” the host does, once it can see what the file holds. Every test that is not
        // about selection itself wants the file's own defaults, which is what this asks for.
        return SelectDefaultStreams(pipeline);
    }

    private static OperationResult OpenFirstSource(PlaybackPipeline pipeline, MediaLocator locator)
    {
        OperationResult reset = pipeline.Reset().GetAwaiter().GetResult();
        if (!reset.IsSuccessful)
        {
            return reset;
        }

        OperationResult<MediaSource> added = pipeline.AddSource(locator).GetAwaiter().GetResult();
        return added.IsSuccessful ? OperationResult.Success() : OperationResult.Failure(added);
    }

    private static OperationResult SelectDefaultStreams(PlaybackPipeline pipeline)
    {
        MediaInfo? info = pipeline.Sources is [MediaSource first, ..] ? first.Info : null;
        if (info is null)
        {
            return OperationResult.Success();
        }

        // One kind at a time, and a kind that cannot be played does not fail the file if the other one can: that is the
        // host's decision to make, and this is the host choosing to play what works.
        bool any = false;
        OperationResult? failure = null;

        if (info.VideoStreams.Count > 0)
        {
            OperationResult video = ChainOf(pipeline, MediaStreamKind.Video).SelectStream(info.VideoStreams[0]).GetAwaiter().GetResult();
            any |= video.IsSuccessful;
            failure = video.IsSuccessful ? failure : video;
        }

        if (info.AudioStreams.Count > 0)
        {
            OperationResult audio = ChainOf(pipeline, MediaStreamKind.Audio).SelectStream(info.AudioStreams[0]).GetAwaiter().GetResult();
            any |= audio.IsSuccessful;
            failure = audio.IsSuccessful ? failure : audio;
        }

        if (!any)
        {
            return failure ?? OperationResult.Success();
        }

        // Choosing records the decision; reconciling is what builds what the choice needs, and it is the call that says a
        // track cannot be played at all. Which chain failed is that call's answer, so the kinds that were chosen decide it.
        OperationResult<ReconcileResult> reconciled = Await(pipeline.Reconcile());
        if (!reconciled.IsSuccessful)
        {
            return OperationResult.Failure(reconciled);
        }

        ReconcileResult result = reconciled.Result;
        if (info.VideoStreams.Count > 0 && result[pipeline.VideoChains[0]] is { IsSuccessful: false } videoFailure)
        {
            return videoFailure;
        }

        if (info.AudioStreams.Count > 0 && result[pipeline.AudioChains[0]] is { IsSuccessful: false } audioFailure)
        {
            return audioFailure;
        }

        return OperationResult.Success();
    }

    /// <summary>
    /// Opens a file and chooses only its picture, which is what a host does when the file's sound is not something it can
    /// play: choosing a track is the host's decision, and reconciling what was chosen is what says a track cannot be
    /// played at all.
    /// </summary>
    private static OperationResult OpenWithoutSound(PlaybackPipeline pipeline, string path)
    {
        OperationResult opened = OpenFirstSource(pipeline, MediaLocator.Parse(path));
        if (!opened.IsSuccessful)
        {
            return opened;
        }

        MediaInfo info = pipeline.Sources[0].Info;
        return info.VideoStreams.Count > 0 ? Select(pipeline, MediaStreamKind.Video, info.VideoStreams[0]) : OperationResult.Success();
    }

    /// <summary>
    /// Waits for a command's own answer, which every call into the pipeline hands back: the job is queued, so
    /// waiting for the task is waiting for the command to have happened.
    /// </summary>
    /// <summary>
    /// Chooses a track and builds what the choice needs. Choosing only records the decision and reconciling is what makes
    /// it play, so a test that is not about that separation wants both; an answer that names a kind is the kind that
    /// could not be played.
    /// </summary>
    private static OperationResult Select(PlaybackPipeline pipeline, MediaStreamKind kind, MediaStreamInfo? stream)
    {
        IPlaybackChain chain = ChainOf(pipeline, kind);
        OperationResult selected = Await(chain.SelectStream(stream));
        if (!selected.IsSuccessful)
        {
            return selected;
        }

        OperationResult<ReconcileResult> reconciled = Await(pipeline.Reconcile());
        if (!reconciled.IsSuccessful)
        {
            return OperationResult.Failure(reconciled);
        }

        // The reconcile happened; what the chain that was chosen made of it is the answer about that choice.
        return ResultOf(reconciled.Result, chain);
    }

    /// <summary>What reconciling made of one chain, which is what the results are keyed by.</summary>
    private static OperationResult ResultOf(ReconcileResult result, IPlaybackChain chain)
    {
        return result[chain];
    }

    /// <summary>The chain of a kind a test means when it speaks of the kind rather than of a chain.</summary>
    private static IPlaybackChain ChainOf(PlaybackPipeline pipeline, MediaStreamKind kind)
    {
        return kind switch
        {
            MediaStreamKind.Video => pipeline.VideoChains[0],
            MediaStreamKind.Audio => pipeline.AudioChains[0],
            _ => pipeline.SubtitleChains[0],
        };
    }

    private static OperationResult Await(Task<OperationResult> command)
    {
        Assert.That(command.Wait(5000), Is.True, "the command was never carried out");
        return command.Result;
    }

    /// <summary>The same for a command that hands something back, such as the file it opened.</summary>
    private static OperationResult<T> Await<T>(Task<OperationResult<T>> command)
    {
        Assert.That(command.Wait(5000), Is.True, "the command was never carried out");
        return command.Result;
    }

    /// <summary>Waits for a command that answers with the thing it made rather than a result, such as adding a chain.</summary>
    private static T Await<T>(Task<T> command)
    {
        Assert.That(command.Wait(5000), Is.True, "the command was never carried out");
        return command.Result;
    }

    /// <summary>
    /// Waits for something about playback to become true. A call is posted, so what a test is usually about is
    /// what a call led to â€” a frame shown, the clock moving, a state reached before anything else changes it.
    /// </summary>
    private static void WaitFor(Func<bool> condition, string what)
    {
        WaitFor(condition, what, 2000);
    }

    /// <summary>Waits without failing, for a check that has a message of its own.</summary>
    private static bool WaitForThreads(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }

    /// <summary>
    /// Waits for a condition with a budget of its own, for a test whose subject is *when* something happened:
    /// what the ordinary budget would let pass is exactly what such a test is about.
    /// </summary>
    /// <summary>
    /// How far the position may read short of where it stood before a switch. The clock is corrected to the device
    /// while the device plays, so it can step back by the sync tolerance, and a switch moves it once more when it
    /// republishes the position: a few tens of milliseconds is that arithmetic, not a jump the test should fail on.
    /// </summary>
    private static readonly TimeSpan TrackSwitchSlack = TimeSpan.FromMilliseconds(100);

    private static void WaitFor(Func<bool> condition, string what, int milliseconds)
    {
        for (int attempt = 0; attempt < milliseconds / 10; attempt++)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.Fail(what);
    }

    private static void WaitForState(PlaybackPipeline pipeline, MediaEngineState state)
    {
        WaitFor(() => pipeline.State == state, $"The pipeline is {pipeline.State} where {state} was expected. {pipeline.Error}");
    }

    private static void WaitForPlaybackToFinish(PlaybackPipeline pipeline)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            MediaEngineState state = pipeline.State;
            if (state is MediaEngineState.Ended or MediaEngineState.Error)
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.Fail($"Playback did not finish; the pipeline is {pipeline.State}.");
    }

    private static void WaitForPresentedFrames(PlaybackPipeline pipeline, long count)
    {
        // Five seconds rather than two: the video fixtures run at a frame every two seconds, so a test asking for
        // the second frame needs more than the frame interval and nothing else.
        for (int attempt = 0; attempt < 500; attempt++)
        {
            if (pipeline.VideoChains[0].PresentedFrameCount >= count)
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.Fail($"The pipeline showed {pipeline.VideoChains[0].PresentedFrameCount} frame(s) where {count} were expected; it is {pipeline.State} at {pipeline.Position:mm\\:ss\\.fff} with the picture at {pipeline.VideoChains[0].LastPresentedTimestamp:mm\\:ss\\.fff}, starved for {pipeline.StarvedFor ?? "nothing"}, {pipeline.VideoChains[0].DroppedFrameCount} dropped and a lag of {pipeline.VideoChains[0].PictureLag.TotalMilliseconds:0} ms. {pipeline.Error}");
    }

    /// <summary>
    /// Seeks and waits for the answer: what a test is about â€” where the clock ended up, whether playback went
    /// on â€” is only settled once the queue item has run, and a seek the source refuses says so in the result.
    /// </summary>
    private static void SeekAndWait(PlaybackPipeline pipeline, TimeSpan position, SeekMode mode = SeekMode.PreviousKeyframe)
    {
        OperationResult seeked = Await(pipeline.Seek(position, mode));
        Assert.That(seeked.IsSuccessful, Is.True, seeked.Message);
    }

    /// <summary>
    /// The same seek from a thread of its own: a caller is free to be any thread, and the task is what says
    /// when the seek happened, wherever it was asked for.
    /// </summary>
    private static void SeekFromAnotherThread(PlaybackPipeline pipeline, TimeSpan position)
    {
        Task<OperationResult>? seek = null;
        var seeker = new Thread(() => seek = pipeline.Seek(position))
        {
            IsBackground = true,
        };

        seeker.Start();
        Assert.That(seeker.Join(10000), Is.True, "the seek was never asked for");

        if (seek is null)
        {
            Assert.Fail("The seek was never carried out.");
            return;
        }

        Assert.That(seek.Wait(10000), Is.True, "the seek was never carried out");
        Assert.That(seek.Result.IsSuccessful, Is.True, seek.Result.Message);
    }

    private static byte[] CreateWaveSamples(int sampleCount)
    {
        byte[] samples = new byte[sampleCount * ChannelCount * sizeof(short)];
        for (int index = 0; index < samples.Length; index++)
        {
            samples[index] = (byte)((index / 4) % 32);
        }

        return samples;
    }

    private static string CreateWaveFile(int sampleCount = SampleCount)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sineplayer-pipeline-{Guid.NewGuid():N}.wav");
        byte[] samples = CreateWaveSamples(sampleCount);

        using (BinaryWriter writer = new(File.Create(path)))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + samples.Length);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)ChannelCount);
            writer.Write(SampleRate);
            writer.Write(SampleRate * ChannelCount * sizeof(short));
            writer.Write((short)(ChannelCount * sizeof(short)));
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(samples.Length);
            writer.Write(samples);
        }

        return path;
    }

    /// <summary>
    /// How many packets the file holds, read straight from the source. A test about the read-ahead needs to know what
    /// the whole file is worth in packets, and playing it to the end to count them would take the length of the file.
    /// </summary>
    private static int CountPackets(string path)
    {
        ISourceProviderFactory factory = new FFmpegSourceProviderFactory();
        OperationResult opened = factory.TryOpen(MediaLocator.Parse(path), out IMediaSource? source);
        Assert.That(opened.IsSuccessful, Is.True, opened.Message);
        Assert.That(source, Is.Not.Null);

        using (source)
        {
            int count = 0;
            while (true)
            {
                OperationResult read = source.TryReadPacket(out MediaPacket? packet);
                Assert.That(read.IsSuccessful, Is.True, read.Message);
                if (packet is null)
                {
                    return count;
                }

                packet.Dispose();
                count++;
            }
        }
    }

    /// <summary>
    /// Writes a video file by hand as YUV4MPEG, which FFmpeg reads as raw frames. Half a frame per
    /// second is what makes it worth having: frame zero is due at once and the next one is two seconds
    /// away, long enough for the thread that shows frames to be certainly still waiting for it.
    /// </summary>
    private static string CreateY4mFile(int frameCount = 20, string frameRate = "1:2")
    {
        string path = Path.Combine(Path.GetTempPath(), $"sineplayer-pipeline-{Guid.NewGuid():N}.y4m");
        byte[] luma = new byte[Width * Height];
        byte[] chroma = new byte[Width / 2 * (Height / 2)];

        using (FileStream stream = File.Create(path))
        {
            WriteText(stream, $"YUV4MPEG2 W{Width} H{Height} F{frameRate} Ip A1:1 C420\n");
            for (int index = 0; index < frameCount; index++)
            {
                WriteText(stream, "FRAME\n");
                stream.Write(luma);
                stream.Write(chroma);
                stream.Write(chroma);
            }
        }

        return path;
    }

    private static void WriteText(Stream stream, string text)
    {
        stream.Write(Encoding.ASCII.GetBytes(text));
    }

    private static string CreateBitmapFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sineplayer-pipeline-{Guid.NewGuid():N}.bmp");
        int pixelBytes = Width * Height * sizeof(int);
        int dataOffset = 14 + 40;

        using (BinaryWriter writer = new(File.Create(path)))
        {
            writer.Write("BM"u8);
            writer.Write(dataOffset + pixelBytes);
            writer.Write(0);
            writer.Write(dataOffset);

            writer.Write(40);
            writer.Write(Width);
            writer.Write(Height);
            writer.Write((short)1);
            writer.Write((short)32);
            writer.Write(0);
            writer.Write(pixelBytes);
            writer.Write(2835);
            writer.Write(2835);
            writer.Write(0);
            writer.Write(0);

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    writer.Write((byte)(x * 8));
                    writer.Write((byte)(y * 10));
                    writer.Write((byte)200);
                    writer.Write((byte)255);
                }
            }
        }

        return path;
    }

    /// <summary>
    /// Waits for the pipeline to say something, since when it says it is the pipeline's decision â€” a stall is named
    /// once it has lasted, not the moment it starts. That a line was raised is what is waited for; what the line says
    /// is not a test's business.
    /// </summary>
    private static void WaitForWarning(WarningLog warnings)
    {
        var waitedFor = Stopwatch.StartNew();
        while (waitedFor.ElapsedMilliseconds < 5000)
        {
            if (warnings.Messages.Length > 0)
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.Fail("The pipeline never said anything.");
    }

    /// <summary>
    /// Records what the pipeline says about playback without stopping it, in the order it said it. A warning
    /// is a line rather than a state, so it is heard as it happens and what this keeps is the sequence.
    /// </summary>
    private sealed class WarningLog
    {
        private readonly object _lock = new();
        private readonly List<WarningEntry> _entries = [];

        internal WarningLog(PlaybackPipeline pipeline)
        {
            pipeline.WarningRaised += Record;
        }

        internal WarningEntry[] Entries
        {
            get
            {
                lock (_lock)
                {
                    return [.. _entries];
                }
            }
        }

        internal string[] Messages
        {
            get
            {
                lock (_lock)
                {
                    return [.. _entries.Select(entry => entry.Message)];
                }
            }
        }

        internal string? Latest
        {
            get
            {
                lock (_lock)
                {
                    return _entries.Count == 0 ? null : _entries[^1].Message;
                }
            }
        }

        private void Record(IPlaybackChain? chain, string warning)
        {
            lock (_lock)
            {
                _entries.Add(new WarningEntry(chain, warning));
            }
        }
    }

    /// <summary>A warning as it was raised, with the chain it came from â€” null when it came from the pipeline itself.</summary>
    private sealed record WarningEntry(IPlaybackChain? Chain, string Message);

    /// <summary>
    /// Hears what the logger is told, which is the only place a refused call leaves a trace: a warning is raised on
    /// the pipeline, but a failure the caller was handed is written to the log. It arrives on the logger's own thread,
    /// so what it keeps is a snapshot rather than a sequence.
    /// </summary>
    private sealed class LogCapture : Logger.ILogListener, IDisposable
    {
        private readonly ConcurrentQueue<string> _messages = new();

        internal LogCapture()
        {
            Logger.AddListener(this);
        }

        /// <summary>Whether any line written since this was installed contains <paramref name="text"/>.</summary>
        internal bool Has(string text)
        {
            foreach (string message in _messages)
            {
                if (message.Contains(text, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public void OnLog(LogEntry item)
        {
            if (item.Message is not null)
            {
                _messages.Enqueue(item.Message);
            }
        }

        public void Dispose()
        {
            Logger.RemoveListener(this);
        }
    }

    private sealed class FakeRenderTarget : IVideoRenderTarget
    {
        public int SurfaceWidth { get; init; } = 320;

        public int SurfaceHeight { get; init; } = 240;

        public double RasterizationScale { get; init; } = 1.0;

        public event Action? SurfaceSizeChanged;

        public void AttachPresentationSurface(nint swapChain)
        {
            SurfaceSizeChanged?.Invoke();
        }

        /// <summary>Stands in for the host resizing its surface or moving the window to another display.</summary>
        public void RaiseSizeChanged()
        {
            SurfaceSizeChanged?.Invoke();
        }
    }

    private sealed class FakeVideoRenderer : IVideoRenderer
    {
        public string Name { get; init; } = "fake";

        /// <summary>Nothing: a stand-in presents nothing, so no decoder is asked to decode onto it.</summary>
        public IGpuDevice? GpuDevice => null;

        public VideoStreamInfo? ConfiguredStream { get; private set; }

        public IVideoRenderTarget? Target { get; private set; }

        public int RenderedFrames { get; private set; }

        private readonly List<TimeSpan> _renderedTimestamps = [];
        private readonly List<int> _renderedWidths = [];

        /// <summary>The width of every frame handed over, in the order it was: which stream a frame came from.</summary>
        public int[] RenderedWidths
        {
            get
            {
                lock (_renderedWidths)
                {
                    return [.. _renderedWidths];
                }
            }
        }

        /// <summary>
        /// The timestamp of every frame the pipeline handed over, in the order it did: what a test reads when
        /// the question is *which* frames were shown rather than how many.
        /// </summary>
        public TimeSpan[] RenderedTimestamps
        {
            get
            {
                lock (_renderedTimestamps)
                {
                    return [.. _renderedTimestamps];
                }
            }
        }

        /// <summary>Stands in for a renderer that needs longer to show a frame than the file gives it.</summary>
        public int RenderDelayMilliseconds { get; init; }

        /// <summary>
        /// Stands in for a device that can no longer present â€” a removed adapter, a lost device. Every draw reports
        /// this failure, which is what the pipeline is expected to survive rather than exit on.
        /// </summary>
        public string? RenderFailure { get; init; }

        public OperationResult Configure(VideoStreamInfo stream, IVideoRenderTarget target)
        {
            ConfiguredStream = stream;
            Target = target;
            return OperationResult.Success();
        }

        public OperationResult Render(RawVideoFrame frame, VideoTransform transform)
        {
            if (RenderFailure is string failure)
            {
                return OperationResult.Failure(failure);
            }

            if (RenderDelayMilliseconds > 0)
            {
                Thread.Sleep(RenderDelayMilliseconds);
            }

            LastRenderedTransform = transform;
            RenderedFrames++;
            lock (_renderedWidths)
            {
                _renderedWidths.Add(frame.Width);
            }
            lock (_renderedTimestamps)
            {
                _renderedTimestamps.Add(frame.Timestamp);
            }

            return OperationResult.Success();
        }

        /// <summary>Where the last frame was told to go, which is how a test reads the transform the pipeline held.</summary>
        public VideoTransform LastRenderedTransform { get; private set; } = VideoTransform.Identity;

        /// <summary>Where the last redraw was told to go, for a transform that changes while nothing is presented.</summary>
        public VideoTransform LastRefreshedTransform { get; private set; } = VideoTransform.Identity;

        /// <summary>
        /// Counts the times the pipeline offered the renderer a surface change while nothing was being
        /// presented; the stand-in does not keep a surface of its own, so taking it up is nothing.
        /// </summary>
        public int RefreshedSurfaces { get; private set; }

        /// <summary>How many times the pipeline let this renderer go, which is what a swap has to do to the one it replaces.</summary>
        public int DisposeCount { get; private set; }

        public OperationResult RefreshSurface(VideoTransform transform)
        {
            RefreshedSurfaces++;
            LastRefreshedTransform = transform;
            return OperationResult.Success();
        }

        /// <summary>
        /// How many times the pipeline told this renderer to empty the surface, which is what it does when the picture
        /// is turned off and nothing will draw again.
        /// </summary>
        public int ClearedSurfaces { get; private set; }

        public OperationResult ClearSurface()
        {
            ClearedSurfaces++;
            return ClearFailure is string failure ? OperationResult.Failure(failure) : OperationResult.Success();
        }

        /// <summary>
        /// Whether the surface has room for another frame. A stand-in has as much room as it is asked for, so what the
        /// pipeline does when a display cannot take a frame is decided by the test that sets this.
        /// </summary>
        public bool FrameSlotsAvailable { get; init; } = true;

        public bool TryAcquireFrameSlot()
        {
            return FrameSlotsAvailable;
        }

        /// <summary>Stands in for a surface that cannot be emptied, so the pipeline's answer to that can be seen.</summary>
        public string? ClearFailure { get; init; }
        public void Dispose()
        {
            DisposeCount++;
        }
    }

    /// <summary>A factory that refuses to make a renderer, which is a device that cannot be created or a surface
    /// that cannot be presented into.</summary>
    private sealed class RefusingAudioRendererFactory : IAudioRendererFactory
    {
        public OperationResult<IComponentCreator<IAudioRenderer>> Create()
        {
            return new OperationResultCreator<IComponentCreator<IAudioRenderer>>().Failure("This audio device refuses to be opened.");
        }
    }

    /// <summary>A factory that refuses every stream, the same way a decoder with no path for a codec does.</summary>
    /// <summary>
    /// A factory that offers a decoder and then cannot open it, which is the shape a stream with no decoder in the build
    /// has: the reason lives in the creator, not in the factory, and that is the reason that has to reach the warning.
    /// </summary>
    private sealed class RefusesInsideTheCreator : IAudioDecoderFactory
    {
        public OperationResult<IComponentCreator<IAudioDecoder>> Create(IMediaSource source, AudioStreamInfo stream, DecoderOptions options)
        {
            return new OperationResultCreator<IComponentCreator<IAudioDecoder>>().Success(new Creator());
        }

        private sealed class Creator : IComponentCreator<IAudioDecoder>
        {
            public string Signature => "refusing:audio";

            public OperationResult Create(out IAudioDecoder? component)
            {
                component = null;
                return OperationResult.Failure("This build cannot decode dts (id 86020).");
            }
        }
    }

    private sealed class RefusingAudioDecoderFactory : IAudioDecoderFactory
    {
        public OperationResult<IComponentCreator<IAudioDecoder>> Create(IMediaSource source, AudioStreamInfo stream, DecoderOptions options)
        {
            return new OperationResultCreator<IComponentCreator<IAudioDecoder>>().Failure("This audio decoder refuses the stream.");
        }
    }

    /// <summary>
    /// The real audio decoder under a signature of its own, so that a test can register it beside the plain one and
    /// have the pipeline see a different decoder â€” which is what "the decoder was replaced" means now.
    /// </summary>
    private sealed class NamedAudioDecoderFactory : IAudioDecoderFactory
    {
        private readonly FFmpegAudioDecoderFactory _inner = new();
        private readonly string _signature;
        private int _offers;

        internal NamedAudioDecoderFactory(string signature)
        {
            _signature = signature;
        }

        /// <summary>
        /// How many times the pipeline asked this factory for a decoder. The decoder it makes is the same
        /// implementation the first factory made, so its name says nothing; this is what a test reads to know
        /// the replacement it asked for is the one that happened.
        /// </summary>
        internal int Offers => Volatile.Read(ref _offers);

        public OperationResult<IComponentCreator<IAudioDecoder>> Create(IMediaSource source, AudioStreamInfo stream, DecoderOptions options)
        {
            Interlocked.Increment(ref _offers);
            OperationResult<IComponentCreator<IAudioDecoder>> offered = _inner.Create(source, stream, options);
            if (!offered.IsSuccessful)
            {
                return offered;
            }

            IComponentCreator<IAudioDecoder> creator = new SignatureCreator(offered.Result, _signature);
            return new OperationResultCreator<IComponentCreator<IAudioDecoder>>().Success(creator);
        }

        /// <summary>Passes everything through but the signature, which is the only thing that makes this a change.</summary>
        private sealed class SignatureCreator(IComponentCreator<IAudioDecoder> inner, string signature) : IComponentCreator<IAudioDecoder>
        {
            public string Signature => signature;

            public OperationResult Create(out IAudioDecoder? component)
            {
                return inner.Create(out component);
            }
        }
    }

    private sealed class RefusingVideoRendererFactory : IVideoRendererFactory
    {
        public OperationResult<IComponentCreator<IVideoRenderer>> Create()
        {
            return new OperationResultCreator<IComponentCreator<IVideoRenderer>>().Failure("This renderer refuses to be made.");
        }
    }

    /// <summary>Offers a renderer and then cannot build it, which is an offer that has to be passed over.</summary>
    private sealed class FailingVideoRendererFactory(string signature) : IVideoRendererFactory
    {
        public OperationResult<IComponentCreator<IVideoRenderer>> Create()
        {
            return new OperationResultCreator<IComponentCreator<IVideoRenderer>>().Success(new Creator(signature));
        }

        private sealed class Creator(string signature) : IComponentCreator<IVideoRenderer>
        {
            public string Signature => signature;

            public OperationResult Create(out IVideoRenderer? component)
            {
                component = null;
                return OperationResult.Failure("This renderer refuses to be made.");
            }
        }
    }

    private sealed class FakeAudioRenderer : IAudioRenderer
    {
        private readonly DeviceClock _clock = new();
        private long _queuedSampleCount;
        private volatile bool _noRoom;

        public string Name { get; init; } = "fake";

        public bool IsDeviceAvailable { get; set; } = true;

        /// <summary>What the device says it lost between blocks; a test that cares about the figure sets it.</summary>
        public long UnderrunCount { get; set; }

        public bool CanConfigure { get; init; } = true;

        /// <summary>
        /// Whether the device takes the stream once it has been described. A device that can be described and then not
        /// opened is the one case left to the audio thread, which is the only thing that opens it.
        /// </summary>
        public bool CanOpen { get; init; } = true;

        public string RefusalReason { get; init; } = string.Empty;

        public int SubmitDelayMilliseconds { get; init; }

        /// <summary>
        /// Stands in for a device that cannot be handed audio: the hand-off reports this failure, which is what
        /// the pipeline is expected to turn into its own error rather than into a dead thread.
        /// </summary>
        public Exception? SubmitFailure { get; init; }

        /// <summary>
        /// Stands in for a device that has no room: a hand-off says so instead of completing, until it is
        /// given room again. The real renderer asks the device; this one is told, because "a full device" has
        /// to be arranged rather than waited for.
        /// </summary>
        public bool NoRoom
        {
            get => _noRoom;
            set => _noRoom = value;
        }

        public AudioStreamInfo? ConfiguredStream { get; private set; }

        public int SubmittedBuffers { get; private set; }

        public int SubmittedSamples { get; private set; }

        /// <summary>How many hand-offs the pipeline has started, whether or not they completed.</summary>
        public int SubmitsStarted { get; private set; }

        /// <summary>How many hand-offs the pipeline let go of before the device had taken the whole block.</summary>
        public int AbandonedSubmits { get; private set; }

        /// <summary>How many times the device has been let go of what it holds.</summary>
        public int FlushCount { get; private set; }

        public bool ClockIsRunning => Clock.IsRunning;

        /// <summary>
        /// Stands in for a device whose stream will not start: starting it throws, which is what the clock of a real
        /// device does â€” the pipeline has to turn that into a report, not into a dead audio thread.
        /// </summary>
        public Exception? ClockStartFailure
        {
            get => _clock.StartFailure;
            init => _clock.StartFailure = value;
        }

        /// <summary>
        /// Stands in for a device that dies while a hand-off is still waiting for room: the pipeline finds out when it
        /// next asks the device's clock to stop, which is the moment a pending hand-off has to be let go of.
        /// </summary>
        public Exception? ClockPauseFailure
        {
            get => _clock.PauseFailure;
            init => _clock.PauseFailure = value;
        }

        /// <summary>How many times the pipeline has told the device's clock to start, changed or not.</summary>
        public int ClockStarts => _clock.Starts;

        /// <summary>How many times the pipeline has told the device's clock to pause, changed or not.</summary>
        public int ClockPauses => _clock.Pauses;

        /// <summary>
        /// How fast this device's clock runs against real time. A device that runs fast is how "the pipeline follows
        /// the device" â€” rather than free-running on its own stopwatch â€” becomes something a test can see.
        /// </summary>
        public double ClockRate
        {
            get => _clock.RateMultiplier;
            init => _clock.RateMultiplier = value;
        }

        public IPlaybackClock Clock => PlaysWhatItIsGiven ? PlayingDevice : _clock;

        /// <summary>
        /// Stands in for a device that plays what it is given and then falls silent: its position counts what has
        /// been *played*, so it stops when it runs dry even though the stream is still marked running. Every other
        /// stand-in here advances with the wall clock, which is why none of them could show a picture waiting for a
        /// moment that has already gone by.
        /// </summary>
        public bool PlaysWhatItIsGiven { get; init; }

        private DrainingDeviceClock? _playingDevice;

        private DrainingDeviceClock PlayingDevice => _playingDevice ??= new DrainingDeviceClock();

        /// <summary>Takes a block into the device, which the draining clock then plays at real time.</summary>
        internal void OnBlockTaken(RawAudioBuffer buffer)
        {
            if (buffer.Buffer is IPlanarAudioBuffer planar && buffer.SampleFormat == AudioSampleFormat.Int16)
            {
                // Copied because the pipeline disposes the block as soon as the device has taken it.
                int byteCount = planar.GetPlaneByteCount(0);
                byte[] bytes = new byte[byteCount];
                Marshal.Copy(planar.GetPlanePointer(0), bytes, 0, byteCount);

                short[] samples = new short[byteCount / sizeof(short)];
                Buffer.BlockCopy(bytes, 0, samples, 0, byteCount);
                LastSamples = samples;
            }

            if (PlaysWhatItIsGiven)
            {
                PlayingDevice.Submit(buffer.Duration);
            }
        }

        /// <summary>The samples of the last block this device was handed, in the stream's own format.</summary>
        internal short[]? LastSamples { get; private set; }

        /// <summary>
        /// Run on whichever thread is asking the device how much it holds, just before the answer is given.
        /// The question is asked in one place â€” whether the device has played the last of it out â€” so a test
        /// that arms this once there is nothing left to hand over is inside that question, which is the only
        /// moment "the file ended while the user paused" can be arranged rather than raced for. One-shot,
        /// because what the test wants is the next answer, not every one.
        /// </summary>
        public Action? BeforeReadingQueuedSampleCount { get; set; }

        public long QueuedSampleCount
        {
            get
            {
                Action? beforeReading = BeforeReadingQueuedSampleCount;
                if (beforeReading is not null)
                {
                    BeforeReadingQueuedSampleCount = null;
                    beforeReading();
                }

                if (PlaysWhatItIsGiven)
                {
                    int sampleRate = ConfiguredStream?.SampleRate ?? 0;
                    return (long)Math.Round(PlayingDevice.QueuedSeconds * sampleRate);
                }

                return _queuedSampleCount;
            }

            set => _queuedSampleCount = value;
        }

        /// <summary>
        /// How many channels this stand-in device plays; the stream is narrowed to it the way a real endpoint with
        /// fewer speakers narrows one, which is what makes the pipeline fold the mix down.
        /// </summary>
        public int? ChannelCapacity { get; init; }

        public OperationResult<AudioStreamInfo> Negotiate(AudioStreamInfo stream)
        {
            OperationResultCreator<AudioStreamInfo> result = new();
            if (!CanConfigure)
            {
                // The device that will not be opened cannot be described either, which is how a selection comes to be
                // refused before anything is played.
                return result.Failure(RefusalReason);
            }

            if (ChannelCapacity is not int capacity || capacity <= 0 || capacity >= stream.ChannelCount)
            {
                return result.Success(stream);
            }

            return result.Success(stream.WithLayout(capacity, capacity == 2 ? ChannelLayout.Stereo : stream.ChannelLayout));
        }

        /// <summary>How many times the device has been opened: more than one is a device that was reopened mid-stream.</summary>
        public int ConfigureCount { get; private set; }

        public OperationResult Configure(AudioStreamInfo stream)
        {
            ConfigureCount++;
            if (!CanConfigure || !CanOpen)
            {
                return OperationResult.Failure(RefusalReason);
            }

            ConfiguredStream = stream;
            return OperationResult.Success();
        }

        /// <summary>
        /// A submit leaves the clock alone: whether the device runs is the pipeline's decision, and a stand-in
        /// that started it would release a hold the picture depends on â€” which is what the real renderer used
        /// to do, and what no test could see while this double was silent about it.
        /// </summary>
        public IAudioSubmit Submit(RawAudioBuffer buffer)
        {
            SubmitsStarted++;
            lock (_submittedTimestamps)
            {
                _submittedTimestamps.Add(buffer.Timestamp);
                _submittedChannelCounts.Add(buffer.ChannelCount);
            }

            return new FakeAudioSubmit(this, buffer);
        }

        private readonly List<int> _submittedChannelCounts = [];

        /// <summary>
        /// How many channels each block the device was handed had, in the order it was handed them: what a fold down
        /// is visible in, since the format the renderer was configured with says what it should be and this says what
        /// actually arrived.
        /// </summary>
        public int[] SubmittedChannelCounts
        {
            get
            {
                lock (_submittedTimestamps)
                {
                    return [.. _submittedChannelCounts];
                }
            }
        }

        private readonly List<TimeSpan> _submittedTimestamps = [];

        /// <summary>
        /// The timestamp of every block the pipeline handed to the device, in the order it did â€” including the
        /// ones the device later let go of, because what a seek is asked about is what was offered at all.
        /// </summary>
        public TimeSpan[] SubmittedTimestamps
        {
            get
            {
                lock (_submittedTimestamps)
                {
                    return [.. _submittedTimestamps];
                }
            }
        }

        public OperationResult Flush()
        {
            FlushCount++;

            // What the renderer's own flush does, in the same order: the device's travel starts over, and the
            // clock is re-anchored at the position it was showing, so a flush does not move playback. For the
            // draining device that also means the buffer is let go of, which is what a reset does.
            if (PlaysWhatItIsGiven)
            {
                TimeSpan playing = PlayingDevice.Position;
                PlayingDevice.Reset();
                PlayingDevice.SetPosition(playing);
            }
            else
            {
                TimeSpan position = _clock.Position;
                _clock.ResetPoint();
                _clock.SetPosition(position);
            }

            return OperationResult.Success();
        }

        /// <summary>How many times the pipeline has let go of this device â€” once per open it was made for.</summary>
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }

        /// <summary>
        /// One block on its way to the device: it is taken in one go unless the device has no room, in which
        /// case it says so and keeps its place â€” which is what the pipeline is expected to wait on and resume,
        /// and what a double that completed unconditionally could never show.
        /// </summary>
        private sealed class FakeAudioSubmit : IAudioSubmit
        {
            private readonly FakeAudioRenderer _renderer;
            private readonly RawAudioBuffer _buffer;
            private bool _delayed;
            private bool _finished;

            internal FakeAudioSubmit(FakeAudioRenderer renderer, RawAudioBuffer buffer)
            {
                _renderer = renderer;
                _buffer = buffer;
            }

            public AudioSubmitYield Current { get; private set; }

            public OperationResult Result { get; private set; } = OperationResult.Success();

            public bool MoveNext()
            {
                if (_finished)
                {
                    return false;
                }

                if (!_delayed)
                {
                    _delayed = true;
                    if (_renderer.SubmitDelayMilliseconds > 0)
                    {
                        Thread.Sleep(_renderer.SubmitDelayMilliseconds);
                    }
                }

                if (_renderer.NoRoom)
                {
                    Current = AudioSubmitYield.NoRoom;
                    return true;
                }

                _finished = true;

                if (_renderer.SubmitFailure is Exception failure)
                {
                    Result = OperationResult.Failure($"Unable to queue audio: {failure.Message}", failure);
                    return false;
                }

                _renderer.SubmittedBuffers++;
                _renderer.SubmittedSamples += _buffer.SampleCount;
                _renderer.OnBlockTaken(_buffer);
                return false;
            }

            public void Dispose()
            {
                if (!_finished)
                {
                    _renderer.AbandonedSubmits++;
                }

                _finished = true;
            }
        }
    }

    /// <summary>
    /// The device as a clock, which is what the renderer's own clock is: a base the pipeline sets, plus how
    /// far the device has travelled since it was last reset.
    /// <para>
    /// A wall clock stands in for this badly, and the difference is the reason this class exists: a device
    /// keeps its travel across a seek. A stand-in that always reported wall time could not see a pipeline
    /// that moved the position without letting go of what the device held â€” so a seek could play the old
    /// position's sound under the new position's picture, and take the clock with it, with every test green.
    /// Here <see cref="ResetPoint"/> is the device being reset, and the position after a seek says which of
    /// the two happened.
    /// </para>
    /// </summary>
    private sealed class DeviceClock : IPlaybackClock
    {
        private readonly StopwatchClock _device = new();

        private TimeSpan _zero;
        private TimeSpan _base;
        private int _starts;
        private int _pauses;

        /// <summary>How many times the pipeline has told this clock to start â€” changed or not.</summary>
        public int Starts => Volatile.Read(ref _starts);

        /// <summary>How many times the pipeline has told this clock to pause â€” changed or not.</summary>
        public int Pauses => Volatile.Read(ref _pauses);

        /// <summary>
        /// Stands in for a device whose stream will not start: the command throws, as the WASAPI clock lets it, and
        /// the pipeline is where that has to become a report rather than a dead thread.
        /// </summary>
        public Exception? StartFailure { get; set; }

        /// <summary>The same for stopping, which is where a device that died while a hand-off was in flight is found.</summary>
        public Exception? PauseFailure { get; set; }

        /// <summary>
        /// How fast this device's clock runs against real time. A device that runs ahead is how "the pipeline follows
        /// the device" â€” rather than free-running on its own stopwatch â€” becomes something a test can see.
        /// </summary>
        public double RateMultiplier { get; set; } = 1.0;

        public bool IsRunning => _device.IsRunning;

        public double Rate => 1.0;

        public TimeSpan Position => _base + TimeSpan.FromTicks((long)((_device.Position - _zero).Ticks * RateMultiplier));

        public void Start()
        {
            Interlocked.Increment(ref _starts);

            if (StartFailure is Exception failure)
            {
                throw failure;
            }

            _device.Start();
        }

        public void Pause()
        {
            Interlocked.Increment(ref _pauses);

            if (PauseFailure is Exception failure)
            {
                throw failure;
            }

            _device.Pause();
        }

        public void SetPosition(TimeSpan position)
        {
            // The device's travel is taken as the zero point, so time reads as the position that was asked
            // for from wherever the device is â€” reset or not, which is what makes the clock's answer honest
            // in the window before the device's own thread gets round to the reset.
            _base = position;
            _zero = _device.Position;
        }

        /// <summary>The device being reset: its travel starts over. What it holds is gone with it.</summary>
        public void ResetPoint()
        {
            _device.SetPosition(TimeSpan.Zero);
        }
    }

    /// <summary>
    /// The device as a WASAPI stream actually behaves: its position counts what has been **played**, so when it has
    /// played everything it was given, the position stops â€” while the stream is still marked running. That is the
    /// difference between this and <see cref="DeviceClock"/>, and it is the whole reason a picture whose last frames
    /// are due after the sound can be left waiting for a moment that never comes.
    /// </summary>
    private sealed class DrainingDeviceClock : IPlaybackClock
    {
        private readonly object _lock = new();
        private readonly Stopwatch _since = Stopwatch.StartNew();

        private TimeSpan _base;
        private double _playedSeconds;
        private double _queuedSeconds;
        private bool _running;

        public TimeSpan Position
        {
            get
            {
                lock (_lock)
                {
                    Drain();
                    return _base + TimeSpan.FromSeconds(_playedSeconds);
                }
            }
        }

        public bool IsRunning
        {
            get
            {
                lock (_lock)
                {
                    return _running;
                }
            }
        }

        public double Rate => 1.0;

        public void Start()
        {
            lock (_lock)
            {
                Drain();
                _running = true;
                _since.Restart();
            }
        }

        public void Pause()
        {
            lock (_lock)
            {
                Drain();
                _running = false;
            }
        }

        public void SetPosition(TimeSpan position)
        {
            lock (_lock)
            {
                Drain();
                _base = position;
                _playedSeconds = 0;
            }
        }

        /// <summary>How much of what it was given is still unplayed, which is what the device reports as queued.</summary>
        internal double QueuedSeconds
        {
            get
            {
                lock (_lock)
                {
                    Drain();
                    return _queuedSeconds;
                }
            }
        }

        /// <summary>The device takes a block and will play it at real time.</summary>
        internal void Submit(TimeSpan duration)
        {
            lock (_lock)
            {
                Drain();
                _queuedSeconds += duration.TotalSeconds;
                _since.Restart();
            }
        }

        /// <summary>What a reset does: the device lets go of everything it was holding.</summary>
        internal void Reset()
        {
            lock (_lock)
            {
                Drain();
                _queuedSeconds = 0;
            }
        }

        /// <summary>
        /// Plays whatever time has passed since the last look, but never more than it was given: this is the device
        /// running dry, and it is what makes the clock stop where the sound ends.
        /// </summary>
        private void Drain()
        {
            if (!_running)
            {
                _since.Restart();
                return;
            }

            double elapsed = _since.Elapsed.TotalSeconds;
            _since.Restart();

            double played = Math.Min(elapsed, _queuedSeconds);
            _queuedSeconds -= played;
            _playedSeconds += played;
        }
    }

    /// <summary>Asserts that a box has the shape of the 320Ã—180 picture, is centred on it, and is no taller than the surface.</summary>
    private static void AssertPicturesShape(SubtitleFrame frame, string because)
    {
        // The picture's surface is 320Ã—240, so a pixel is 320 Ã— 0.001 wide and 240 Ã— 0.001 high in the box's own units.
        double width = frame.Width * 320.0;
        double height = frame.Height * 240.0;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(width / height, Is.EqualTo(320.0 / 180.0).Within(0.01), because);
            Assert.That(height, Is.LessThanOrEqualTo(240.0), "and no taller than the surface");
            Assert.That(frame.Left + (frame.Width / 2.0), Is.EqualTo(0.5).Within(0.001), "centred on the picture's own centre");
            Assert.That(frame.Top + (frame.Height / 2.0), Is.EqualTo(0.5).Within(0.001));
        }
    }

    /// <summary>
    /// A pipeline whose only picture and sound are the scripted ones, plus the synthetic subtitle decoder â€” and the
    /// surfaces a subtitle chain needs to be buildable at all.
    /// </summary>
    private static PlaybackPipeline CreateSubtitlePipeline(
        SyntheticMediaScript script,
        FakeSubtitleRenderer subtitleRenderer,
        ISubtitleRendererFactory? subtitleRenderers = null,
        ISubtitleDecoderFactory? subtitleDecoders = null)
    {
        return CreatePipeline(
            new FakeVideoRenderer(),
            new FakeAudioRenderer { PlaysWhatItIsGiven = true },
            videoDecoders: new SyntheticVideoDecoderFactory(),
            sourceFactory: new SyntheticSourceProviderFactory(script),
            audioDecoders: new SyntheticAudioDecoderFactory(),
            subtitleDecoders: subtitleDecoders ?? new SyntheticSubtitleDecoderFactory(),
            subtitleRenderers: subtitleRenderers ?? new FakeSubtitleRendererFactory(subtitleRenderer));
    }

    /// <summary>
    /// A pipeline that can be given more than one file: each locator is scripted by the test, so a file added after the
    /// one being played brings tracks of its own.
    /// </summary>
    private static PlaybackPipeline CreateMultiSourcePipeline(
        SyntheticMultiSourceProviderFactory sources,
        FakeSubtitleRenderer subtitleRenderer,
        FakeVideoRenderer? videoRenderer = null,
        FakeAudioRenderer? audioRenderer = null)
    {
        return CreatePipeline(
            videoRenderer ?? new FakeVideoRenderer(),
            audioRenderer ?? new FakeAudioRenderer { PlaysWhatItIsGiven = true },
            videoDecoders: new SyntheticVideoDecoderFactory(),
            sourceFactory: sources,
            audioDecoders: new SyntheticAudioDecoderFactory(),
            subtitleDecoders: new SyntheticSubtitleDecoderFactory(),
            subtitleRenderers: new FakeSubtitleRendererFactory(subtitleRenderer));
    }

    /// <summary>
    /// Gives the pipeline a file and hands back the first subtitle track it holds â€” what a caller does when a file is
    /// added for its words, since the file's own answer is the list of streams it brought.
    /// </summary>
    private static OperationResult<SubtitleStreamInfo> AddSubtitleFile(PlaybackPipeline pipeline, string path)
    {
        OperationResult<MediaSource> added = Await(pipeline.AddSource(MediaLocator.Parse(path)));
        OperationResultCreator<SubtitleStreamInfo> result = new();
        if (!added.IsSuccessful)
        {
            return result.Failure(added);
        }

        SubtitleStreamInfo? stream = added.Result.Info.Streams.OfType<SubtitleStreamInfo>().FirstOrDefault();
        return stream is null ? result.Failure("The file holds no subtitle track.") : result.Success(stream);
    }

    private static readonly SubtitleSurface SubtitleSurfaceTarget = new();

    /// <summary>
    /// Opens the scripted file and selects the picture and the subtitle track, one kind at a time â€” the order the host
    /// uses, since opening chooses nothing by itself.
    /// </summary>
    private static OperationResult OpenForSubtitles(PlaybackPipeline pipeline, bool selectTheTrack = true)
    {
        OperationResult opened = OpenFirstSource(pipeline, MediaLocator.Parse(SyntheticPath()));
        if (!opened.IsSuccessful || !selectTheTrack)
        {
            return opened;
        }

        MediaInfo info = pipeline.Sources[0].Info;
        OperationResult video = Select(pipeline, MediaStreamKind.Video, info.VideoStreams[0]);
        if (!video.IsSuccessful)
        {
            return video;
        }

        return Select(pipeline, MediaStreamKind.Subtitle, info.SubtitleStreams[0]);
    }

    /// <summary>The surface subtitles are presented into, which is not the one the picture goes into.</summary>
    private sealed class SubtitleSurface : IVideoRenderTarget
    {
        public int SurfaceWidth => 640;

        public int SurfaceHeight => 480;

        public double RasterizationScale => 1.0;

        public event Action? SurfaceSizeChanged;

        public void AttachPresentationSurface(nint swapChain)
        {
            SurfaceSizeChanged?.Invoke();
        }
    }

    /// <summary>
    /// The synthetic subtitle decoder with one discrete change declared inside its cue â€” a karaoke syllable turning over,
    /// which has no length and so cannot be asked about as a window.
    /// </summary>
    private sealed class InstantSubtitleDecoderFactory(TimeSpan instant) : ISubtitleDecoderFactory
    {
        public OperationResult<IComponentCreator<ISubtitleDecoder>> Create(IMediaSource source, SubtitleStreamInfo stream)
        {
            IComponentCreator<ISubtitleDecoder> creator = new FixedCreator<ISubtitleDecoder>("instant:subtitle", () => new Decoder(stream, instant));
            return new OperationResultCreator<IComponentCreator<ISubtitleDecoder>>().Success(creator);
        }

        private sealed class Decoder(SubtitleStreamInfo stream, TimeSpan instant) : ISubtitleDecoder
        {
            public string Name => "instant";

            public MediaStreamInfo Stream => stream;

            public bool IsHardwareAccelerated => false;

            public SubtitleCueFormat CueFormat => SubtitleCueFormat.Ass;

            public OperationResult TryDecode(MediaPacket packet, out IReadOnlyList<SubtitleCue> cues)
            {
                cues = new SubtitleCue[]
                {
                    new SubtitleCue
                    {
                        Start = packet.Timestamp,
                        End = packet.Timestamp + packet.Duration,
                        Text = SyntheticSubtitleDecoderFactory.TextAt(packet.Timestamp),
                        Changes = [new SubtitleChange(instant, instant, false)],
                    },
                };

                return OperationResult.Success();
            }

            public OperationResult Drain(out IReadOnlyList<SubtitleCue> cues)
            {
                cues = [];
                return OperationResult.Success();
            }

            public OperationResult Flush()
            {
                return OperationResult.Success();
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeSubtitleRendererFactory(ISubtitleRenderer renderer) : ISubtitleRendererFactory
    {
        private readonly string _signature = NextSignature("fake-subtitle");

        /// <summary>The format the pipeline asked for, which is the decoder's word about what a cue is made of.</summary>
        internal SubtitleCueFormat? RequestedFormat { get; private set; }

        public OperationResult<IComponentCreator<ISubtitleRenderer>> Create(SubtitleStreamInfo stream, SubtitleCueFormat format)
        {
            RequestedFormat = format;
            return new OperationResultCreator<IComponentCreator<ISubtitleRenderer>>().Success(new FixedCreator<ISubtitleRenderer>(_signature, () => renderer));
        }
    }

    /// <summary>A factory that cannot draw what it is asked for, which is how a renderer says so at all.</summary>
    private sealed class RefusingSubtitleRendererFactory : ISubtitleRendererFactory
    {
        public OperationResult<IComponentCreator<ISubtitleRenderer>> Create(SubtitleStreamInfo stream, SubtitleCueFormat format)
        {
            return new OperationResultCreator<IComponentCreator<ISubtitleRenderer>>().Failure($"This renderer does not draw {format} cues.");
        }
    }

    /// <summary>
    /// Records every drawing rather than presenting it, because what a subtitle loop has to be checked for is what it
    /// decided to show and when â€” not what a compositor made of it.
    /// </summary>
    /// <summary>
    /// A factory whose renderer can be changed, with a new signature each time â€” which is how the pipeline recognises a
    /// switch rather than the same component being offered again.
    /// </summary>
    private sealed class SwappableSubtitleRendererFactory(ISubtitleRenderer renderer) : ISubtitleRendererFactory
    {
        private ISubtitleRenderer _renderer = renderer;
        private string _signature = NextSignature("swappable-subtitle");

        internal void Swap(ISubtitleRenderer renderer)
        {
            _renderer = renderer;
            _signature = NextSignature("swappable-subtitle");
        }

        public OperationResult<IComponentCreator<ISubtitleRenderer>> Create(SubtitleStreamInfo stream, SubtitleCueFormat format)
        {
            return new OperationResultCreator<IComponentCreator<ISubtitleRenderer>>().Success(new FixedCreator<ISubtitleRenderer>(_signature, () => _renderer));
        }
    }

    private sealed class FakeSubtitleRenderer : ISubtitleRenderer
    {
        private readonly Lock _lock = new();
        private readonly List<string[]> _texts = [];
        private readonly List<SubtitlePresentation> _presentations = [];

        public string Name => "fake-subtitle";

        public SubtitleStreamInfo? ConfiguredStream { get; private set; }

        /// <summary>The attachments the renderer was configured with, which is how a test reads what reached it.</summary>
        public MediaAttachment[] ConfiguredAttachments { get; private set; } = [];

        public IVideoRenderTarget? Target { get; private set; }

        /// <summary>The text of the cues of the most recent drawing, which is the whole visible set.</summary>
        public string[] Texts
        {
            get
            {
                lock (_lock)
                {
                    return _texts.Count == 0 ? [] : _texts[^1];
                }
            }
        }

        /// <summary>The presentation of every drawing, in order, so a test can check nothing was drawn without it.</summary>
        public SubtitlePresentation[] Presentations
        {
            get
            {
                lock (_lock)
                {
                    return [.. _presentations];
                }
            }
        }

        public int ShowCount
        {
            get
            {
                lock (_lock)
                {
                    return _texts.Count;
                }
            }
        }

        public bool Disposed { get; private set; }

        public OperationResult Configure(SubtitleStreamInfo stream, MediaAttachment[] attachments, IVideoRenderTarget target)
        {
            ConfiguredStream = stream;
            ConfiguredAttachments = [.. attachments];
            Target = target;
            return OperationResult.Success();
        }

        public OperationResult Show(IReadOnlyList<SubtitleCue> cues, TimeSpan position, SubtitlePresentation presentation)
        {
            string[] texts = [.. cues.Select(cue => cue.Text ?? string.Empty)];
            lock (_lock)
            {
                _texts.Add(texts);
                _presentations.Add(presentation);
            }

            return OperationResult.Success();
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
