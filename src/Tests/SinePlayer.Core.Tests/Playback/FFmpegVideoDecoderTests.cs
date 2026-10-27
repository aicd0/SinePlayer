// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.Direct3D11;
using SinePlayer.Core.Playback.Backends.FFmpeg;

using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Exercises the decoder end to end against real bitstreams. The bitmap the tests write is demuxed as a
/// single frame video stream and decodes through the same codec plumbing a video stream uses; the
/// generated streams are what the hardware paths need, because a bitmap has no hardware decoder.
/// </summary>
[TestFixture]
public class FFmpegVideoDecoderTests
{
    private const int Width = 32;
    private const int Height = 24;

    private string _imagePath = string.Empty;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        FFmpegRuntime.EnsureInitialized();
        _imagePath = CreateBitmapFile();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (File.Exists(_imagePath))
        {
            File.Delete(_imagePath);
        }
    }

    [Test]
    public void DecodesAFrameWithTheExpectedGeometry()
    {
        using FFmpegSource source = OpenImage();
        using IVideoDecoder decoder = CreateDecoder(source);

        List<RawVideoFrame> frames = DecodeAll(source, decoder);

        Assert.That(frames, Is.Not.Empty);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoder.IsHardwareAccelerated, Is.False);
            Assert.That(decoder.OutputFormat, Is.EqualTo(PixelFormat.Bgra8));

            RawVideoFrame frame = frames[0];
            Assert.That(frame.Width, Is.EqualTo(Width));
            Assert.That(frame.Height, Is.EqualTo(Height));
            Assert.That(frame.Format, Is.EqualTo(decoder.OutputFormat));
            Assert.That(frame.IsHardwareBacked, Is.False);
        }

        DisposeFrames(frames);
    }

    [Test]
    public void DoesNotLeaveTheDecoderOnOneThread()
    {
        // A codec context asks for one thread unless it is told otherwise, and one thread is one core. The
        // stream that showed it is 4K AV1: 12.9 ms a frame is three to four times what a frame-threaded
        // decoder needs, which is the difference between a dropped frame every few seconds and none at all.
        // Hardware decoding pins the thread count to one, so the software path is what this holds in place.
        if (Environment.ProcessorCount < 2)
        {
            Assert.Ignore("This machine has a single processor, where one thread is the only answer.");
        }

        string path = VideoFixtureFile.CreateAv1();
        try
        {
            FFmpegSourceProviderFactory factory = new();
            Assert.That(factory.TryOpen(MediaLocator.Parse(path), out IMediaSource? opened).IsSuccessful, Is.True);

            using IMediaSource source = opened!;
            VideoStreamInfo stream = source.Info.VideoStreams[0];
            Assert.That(stream.CodecId, Is.EqualTo("av1"));

            FFmpegVideoDecoderFactory decoderFactory = new();
            DecoderOptions options = new() { HardwareDecoding = false };
            OperationResult offered = decoderFactory.TryCreate(source, stream, options, out IComponentCreator<IVideoDecoder>? creator);
            Assert.That(offered.IsSuccessful, Is.True, offered.Message);

            OperationResult created = creator!.Create(out IVideoDecoder? decoder);
            Assert.That(created.IsSuccessful, Is.True, created.Message);

            using IVideoDecoder videoDecoder = decoder!;
            Assert.That(((FFmpegVideoDecoder)videoDecoder).ThreadCount, Is.GreaterThan(1), "the decoder has to be allowed more than one thread");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void FrameBufferExposesReadablePlanes()
    {
        using FFmpegSource source = OpenImage();
        using IVideoDecoder decoder = CreateDecoder(source);

        List<RawVideoFrame> frames = DecodeAll(source, decoder);
        Assert.That(frames, Is.Not.Empty);

        RawVideoFrame frame = frames[0];
        Assert.That(frame.Buffer, Is.InstanceOf<IPlanarVideoBuffer>());

        var buffer = (IPlanarVideoBuffer)frame.Buffer;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.PlaneCount, Is.EqualTo(1));
            Assert.That(buffer.GetPlanePointer(0), Is.Not.EqualTo(0));
            Assert.That(buffer.GetPlaneStride(0), Is.GreaterThanOrEqualTo(Width * 4));
        }

        DisposeFrames(frames);
    }

    [Test]
    public void FallsBackToSoftwareWhenTheCodecHasNoHardwareDecoder()
    {
        // A bitmap is a codec no GPU decodes, which is the shape of every machine that is asked for
        // hardware it does not have: the stream still decodes, and nothing reports an error.
        using FFmpegSource source = OpenImage();
        using IVideoDecoder decoder = CreateDecoder(source, hardwareDecoding: true);

        List<RawVideoFrame> frames = DecodeAll(source, decoder);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(frames, Is.Not.Empty);
            Assert.That(decoder.IsHardwareAccelerated, Is.False);
        }

        DisposeFrames(frames);
    }

    [Test]
    public void DecodesOnTheGpuWithTheSamePictureTheCpuProduces()
    {
        // H.264 is decoded by one decoder either way, so this is the codec where asking for hardware
        // either engages or quietly falls back to the same decoder's software path.
        string path = VideoFixtureFile.CreateH264();
        try
        {
            using RawVideoFrame accelerated = DecodeFirstFrame(path, hardwareDecoding: true, out bool hardware);
            Assert.That(accelerated.IsHardwareBacked, Is.False, "the frame has to be in memory that the pipeline can read");

            if (!hardware)
            {
                Assert.Ignore("This machine has no H.264 hardware decoder, so there is nothing to compare against.");
            }

            using RawVideoFrame software = DecodeFirstFrame(path, hardwareDecoding: false, out _);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(accelerated.Format, Is.EqualTo(PixelFormat.Nv12).Or.EqualTo(PixelFormat.P010), "a hardware frame arrives in a format the renderer can sample");
                Assert.That(accelerated.Width, Is.EqualTo(VideoFixtureFile.Width));
                Assert.That(accelerated.Height, Is.EqualTo(VideoFixtureFile.Height));
                Assert.That(IsSamePicture(accelerated, software), Is.True, "the GPU and the CPU have to produce the same picture");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void KeepsTheTimeOfEveryFrameItCopiesOutOfTheGpu()
    {
        // av_hwframe_transfer_data moves pixels and nothing else, so a copy-back frame carries no timestamp unless the
        // decoder puts it there. Without it every frame is placed by whichever packet happened to be submitted, the
        // first of them is stamped seconds into the file, and the clock anchors to that — so playback starts late and
        // the frames are paced against a moment that never happened. Nothing counts these as lost, which is why a
        // comparison of the times is the only thing that finds them.
        string path = VideoFixtureFile.CreateH264();
        try
        {
            List<TimeSpan> software = DecodeTimestamps(path, hardwareDecoding: false, out _);
            List<TimeSpan> accelerated = DecodeTimestamps(path, hardwareDecoding: true, out bool hardware);

            if (!hardware)
            {
                Assert.Ignore("This machine has no H.264 hardware decoder, so there is nothing to compare against.");
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(accelerated, Is.EqualTo(software), "a frame copied out of the GPU keeps its own moment");
                Assert.That(accelerated.Count, Is.EqualTo(software.Count), "and no frame is lost to the copy");
                Assert.That(accelerated[0], Is.EqualTo(TimeSpan.Zero), "the first frame starts where the file does");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The moment of every frame a stream produces, in the order it produced them.</summary>
    private static List<TimeSpan> DecodeTimestamps(string path, bool hardwareDecoding, out bool accelerated)
    {
        FFmpegSourceProviderFactory factory = new();
        Assert.That(factory.TryOpen(MediaLocator.Parse(path), out IMediaSource? opened).IsSuccessful, Is.True);

        using var source = (FFmpegSource)opened!;
        using IVideoDecoder decoder = CreateDecoder(source, hardwareDecoding);
        List<RawVideoFrame> frames = DecodeAll(source, decoder);
        accelerated = decoder.IsHardwareAccelerated;

        List<TimeSpan> timestamps = [.. frames.Select(frame => frame.Timestamp)];
        DisposeFrames(frames);
        return timestamps;
    }

    /// <summary>
    /// A stream the device cannot draw is refused before anything is opened, so the caller can still ask for a
    /// decoder that does not keep frames on the GPU. The layout is derived from the stream rather than from a
    /// decoded frame, which is what makes it knowable this early — and it is why this needs no hardware at all.
    /// </summary>
    [Test]
    public void RefusesAStreamTheDeviceCannotDraw()
    {
        OperationResult<D3D11Device> created = D3D11Device.TryCreate();
        Assert.That(created.IsSuccessful, Is.True, created.Message);

        using D3D11Device device = created.Result;

        string path = VideoFixtureFile.CreateH264();
        try
        {
            using FFmpegSource source = OpenSource(path);
            var gpuDevice = new TestGpuDevice(device.NativeDevice, canDisplay: false);
            OperationResult<FFmpegVideoDecoder> decoderCreated = FFmpegVideoDecoder.TryCreate(
                source,
                source.Info.VideoStreams[0],
                hardwareDecoding: true,
                gpuFrames: true,
                gpuDevice,
                bufferedFrames: 0);

            if (decoderCreated.IsSuccessful)
            {
                decoderCreated.Result.Dispose();
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(decoderCreated.IsSuccessful, Is.False, "a layout nothing can draw has to fail at open");
                Assert.That(decoderCreated.Message, Does.Contain("Nv12"), "the refusal names the layout the stream is decoded into");
                Assert.That(decoderCreated.Message, Does.Contain("cannot draw"), decoderCreated.Message);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The same stream is accepted by a device that can draw it, and asking the question costs nothing: a stream whose
    /// layout the renderer can sample still opens, on the GPU, exactly as it did before.
    /// </summary>
    [Test]
    public void LeavesAStreamTheDeviceCanDrawAlone()
    {
        OperationResult<D3D11Device> created = D3D11Device.TryCreate();
        Assert.That(created.IsSuccessful, Is.True, created.Message);

        using D3D11Device device = created.Result;

        string path = VideoFixtureFile.CreateH264();
        try
        {
            using FFmpegSource source = OpenSource(path);
            var gpuDevice = new TestGpuDevice(device.NativeDevice);
            OperationResult<FFmpegVideoDecoder> decoderCreated = FFmpegVideoDecoder.TryCreate(
                source,
                source.Info.VideoStreams[0],
                hardwareDecoding: true,
                gpuFrames: true,
                gpuDevice,
                bufferedFrames: 0);

            Assert.That(decoderCreated.IsSuccessful, Is.True, decoderCreated.Message);
            using FFmpegVideoDecoder owned = decoderCreated.Result;
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A device of an API this backend cannot decode on is refused, and the request fails rather than the decode
    /// producing frames nothing can read. That the device is not a D3D11 one is what says so — there is no pointer to
    /// mistake for an <c>ID3D11Device</c>.
    /// </summary>
    [Test]
    public void RefusesADeviceItCannotDecodeOn()
    {
        string path = VideoFixtureFile.CreateH264();
        try
        {
            using FFmpegSource source = OpenSource(path);
            var device = new ForeignGpuDevice();
            OperationResult<FFmpegVideoDecoder> decoderCreated = FFmpegVideoDecoder.TryCreate(
                source,
                source.Info.VideoStreams[0],
                hardwareDecoding: true,
                gpuFrames: true,
                device,
                bufferedFrames: 0);

            if (decoderCreated.IsSuccessful)
            {
                decoderCreated.Result.Dispose();
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(decoderCreated.IsSuccessful, Is.False, "a device of another API cannot be decoded on");
                Assert.That(decoderCreated.Message, Does.Contain("has no decoder for the device a renderer offered"), decoderCreated.Message);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A device of another API: it can be drawn on, which is all the pipeline asks of it, and it says nothing about a
    /// Direct3D handle because it has none to offer.
    /// </summary>
    private sealed class ForeignGpuDevice : IGpuDevice
    {
        public OperationResult CanDisplayGpuFrame(PixelFormat format)
        {
            return OperationResult.Success();
        }
    }

    private static FFmpegSource OpenSource(string path)
    {
        FFmpegSourceProviderFactory factory = new();
        Assert.That(factory.TryOpen(MediaLocator.Parse(path), out IMediaSource? opened).IsSuccessful, Is.True);
        return (FFmpegSource)opened!;
    }

    [Test]
    public void KeepsTheFrameOnTheDeviceItWasGiven()
    {
        // The renderer owns the pipeline's device, so a decoder has to be able to decode on a device it was handed.
        // Reading the texture back through that same device is what proves it — no other device can read it — and it is
        // the proof rather than a comparison of handles: what the assertion is about is the frame, not the plumbing.
        OperationResult<D3D11Device> created = D3D11Device.TryCreate();
        Assert.That(created.IsSuccessful, Is.True, created.Message);

        using D3D11Device device = created.Result;

        string path = VideoFixtureFile.CreateH264();
        try
        {
            FFmpegSourceProviderFactory factory = new();
            Assert.That(factory.TryOpen(MediaLocator.Parse(path), out IMediaSource? opened).IsSuccessful, Is.True);

            using var source = (FFmpegSource)opened!;
            VideoStreamInfo stream = source.Info.VideoStreams[0];
            var gpuDevice = new TestGpuDevice(device.NativeDevice);
            OperationResult<FFmpegVideoDecoder> decoderCreated = FFmpegVideoDecoder.TryCreate(
                source,
                stream,
                hardwareDecoding: true,
                gpuFrames: true,
                gpuDevice,
                bufferedFrames: 0);
            Assert.That(decoderCreated.IsSuccessful, Is.True, decoderCreated.Message);

            using FFmpegVideoDecoder owned = decoderCreated.Result;
            using RawVideoFrame? accelerated = DecodeFrame(source, owned);
            Assert.That(accelerated, Is.Not.Null, "the stream has to produce a frame");

            if (!owned.IsHardwareAccelerated)
            {
                Assert.Ignore("This machine has no H.264 hardware decoder, so there is nothing to look at.");
            }

            Assert.That(accelerated!.IsHardwareBacked, Is.True, "the frame has to be the texture the decoder wrote");
            Assert.That(accelerated.Format, Is.EqualTo(PixelFormat.Nv12), "the texture holds the pixels the software decoder would have produced");
            Assert.That(accelerated.Buffer, Is.InstanceOf<IGpuVideoBuffer>());

            var buffer = (IGpuVideoBuffer)accelerated.Buffer;
            Assert.That(buffer.NativeTexture, Is.Not.EqualTo(0));

            byte[] acceleratedLuma = ReadLuma(device, accelerated, buffer);

            using RawVideoFrame software = DecodeFirstFrame(path, hardwareDecoding: false, out _);
            byte[] softwareLuma = CopyLuma((IPlanarVideoBuffer)software.Buffer, software.Width, software.Height);

            Assert.That(IsSameLuma(acceleratedLuma, softwareLuma, software.Width, software.Height), Is.True, "the texture has to hold the picture the CPU produced");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Reads packets until the decoder has produced a frame, which is one frame's worth of work rather than
    /// a whole file's — a hardware pool is what a caller holds frames from.
    /// </summary>
    private static RawVideoFrame? DecodeFrame(FFmpegSource source, IVideoDecoder decoder)
    {
        while (true)
        {
            OperationResult readResult = source.TryReadPacket(out MediaPacket? packet);
            Assert.That(readResult.IsSuccessful, Is.True, readResult.Message);
            if (packet is null)
            {
                return null;
            }

            using (packet)
            {
                OperationResult decodeResult = decoder.TryDecode(packet, out IReadOnlyList<RawVideoFrame> decoded);
                Assert.That(decodeResult.IsSuccessful, Is.True, decodeResult.Message);
                if (decoded.Count == 0)
                {
                    continue;
                }

                for (int index = 1; index < decoded.Count; index++)
                {
                    decoded[index].Dispose();
                }

                return decoded[0];
            }
        }
    }

    [Test]
    public void DrawsAHardwareFrameWithoutCopyingItBack()
    {
        // The whole point of the GPU path: the frame the renderer draws is the texture the decoder wrote, on
        // the device the renderer draws with. What it must produce is the picture the CPU path produces.
        OperationResult<D3D11Device> created = D3D11Device.TryCreate();
        Assert.That(created.IsSuccessful, Is.True, created.Message);
        using D3D11Device device = created.Result;

        OperationResult<D3D11VideoRenderer> createdRenderer = D3D11VideoRenderer.TryCreate(device);
        Assert.That(createdRenderer.IsSuccessful, Is.True, createdRenderer.Message);
        using D3D11VideoRenderer renderer = createdRenderer.Result;

        string path = VideoFixtureFile.CreateH264();
        try
        {
            FFmpegSourceProviderFactory factory = new();
            Assert.That(factory.TryOpen(MediaLocator.Parse(path), out IMediaSource? opened).IsSuccessful, Is.True);

            using var source = (FFmpegSource)opened!;
            VideoStreamInfo stream = source.Info.VideoStreams[0];
            OperationResult<FFmpegVideoDecoder> createdDecoder = FFmpegVideoDecoder.TryCreate(source, stream, hardwareDecoding: true, gpuFrames: true, renderer, bufferedFrames: 0);
            Assert.That(createdDecoder.IsSuccessful, Is.True, createdDecoder.Message);

            using FFmpegVideoDecoder owned = createdDecoder.Result;
            using RawVideoFrame? frame = DecodeFrame(source, owned);
            Assert.That(frame, Is.Not.Null, "the stream has to produce a frame");

            if (!owned.IsHardwareAccelerated)
            {
                Assert.Ignore("This machine has no H.264 hardware decoder, so there is no texture to draw.");
            }

            Assert.That(frame!.IsHardwareBacked, Is.True);
            Assert.That(frame.Buffer, Is.Not.InstanceOf<IPlanarVideoBuffer>(), "the frame must not have been brought back to memory");

            using var target = D3D11RenderTarget.CreateOffscreen(device, VideoFixtureFile.Width, VideoFixtureFile.Height);
            byte[] hardwarePixels = RenderFrame(renderer, device, target, frame);

            using RawVideoFrame software = DecodeFirstFrame(path, hardwareDecoding: false, out _);
            byte[] softwarePixels = RenderFrame(renderer, device, target, software);

            Assert.That(IsSamePixels(hardwarePixels, softwarePixels), Is.True, "the texture has to be drawn as the picture the CPU produced");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A decoder built on a renderer's device has to keep that device alive by itself. Replacing a renderer
    /// disposes the one it replaces, and a decoder that was decoding on that device must not go down with it —
    /// which is what makes a renderer swap a piece of bookkeeping rather than a two-thread handshake: the
    /// decoder holds a reference of its own to the device, and libavcodec holds one to the immediate context
    /// it derives from it, so disposing the renderer is one release among several.
    /// </summary>
    [Test]
    public void KeepsDecodingAfterTheRendererItWasBuiltOnIsGone()
    {
        OperationResult<D3D11Device> created = D3D11Device.TryCreate();
        Assert.That(created.IsSuccessful, Is.True, created.Message);
        using D3D11Device device = created.Result;

        OperationResult<D3D11VideoRenderer> createdRenderer = D3D11VideoRenderer.TryCreate(device);
        Assert.That(createdRenderer.IsSuccessful, Is.True, createdRenderer.Message);
        D3D11VideoRenderer renderer = createdRenderer.Result;

        string path = VideoFixtureFile.CreateH264();
        try
        {
            FFmpegSourceProviderFactory factory = new();
            Assert.That(factory.TryOpen(MediaLocator.Parse(path), out IMediaSource? opened).IsSuccessful, Is.True);

            using var source = (FFmpegSource)opened!;
            VideoStreamInfo stream = source.Info.VideoStreams[0];
            OperationResult<FFmpegVideoDecoder> createdDecoder = FFmpegVideoDecoder.TryCreate(source, stream, hardwareDecoding: true, gpuFrames: true, renderer, bufferedFrames: 0);
            Assert.That(createdDecoder.IsSuccessful, Is.True, createdDecoder.Message);

            using FFmpegVideoDecoder owned = createdDecoder.Result;
            using RawVideoFrame? first = DecodeFrame(source, owned);
            Assert.That(first, Is.Not.Null, "the stream has to produce a frame");

            if (!owned.IsHardwareAccelerated)
            {
                renderer.Dispose();
                Assert.Ignore("This machine has no H.264 hardware decoder, so no device is in use.");
            }

            renderer.Dispose();

            using RawVideoFrame? second = DecodeFrame(source, owned);
            Assert.That(second, Is.Not.Null, "the decoder has to decode on a device the renderer no longer holds");
            Assert.That(second!.IsHardwareBacked, Is.True, "and it has to still be on the GPU");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A hardware decoder decodes into a texture it sized for itself, so a 1080-high picture can live in a
    /// 1088- or 1152-high texture. The rows below the picture were never written, and drawing them puts a band
    /// of nothing — which converts to green — along the bottom. The picture has to be drawn from the part of
    /// the texture it occupies, which is what makes it match the CPU path at this size and not at 64x64.
    /// </summary>
    [Test]
    public void DrawsAPaddedHardwareTextureWithoutShowingThePadding()
    {
        const int width = 1920;
        const int height = 1080;

        OperationResult<D3D11Device> created = D3D11Device.TryCreate();
        Assert.That(created.IsSuccessful, Is.True, created.Message);
        using D3D11Device device = created.Result;

        OperationResult<D3D11VideoRenderer> createdRenderer = D3D11VideoRenderer.TryCreate(device);
        Assert.That(createdRenderer.IsSuccessful, Is.True, createdRenderer.Message);
        using D3D11VideoRenderer renderer = createdRenderer.Result;

        string path = VideoFixtureFile.CreateH264(frameCount: 8, width: width, height: height);
        try
        {
            FFmpegSourceProviderFactory factory = new();
            Assert.That(factory.TryOpen(MediaLocator.Parse(path), out IMediaSource? opened).IsSuccessful, Is.True);

            using var source = (FFmpegSource)opened!;
            VideoStreamInfo stream = source.Info.VideoStreams[0];
            OperationResult<FFmpegVideoDecoder> createdDecoder = FFmpegVideoDecoder.TryCreate(source, stream, hardwareDecoding: true, gpuFrames: true, renderer, bufferedFrames: 0);
            Assert.That(createdDecoder.IsSuccessful, Is.True, createdDecoder.Message);

            using FFmpegVideoDecoder owned = createdDecoder.Result;
            using RawVideoFrame? frame = DecodeFrame(source, owned);
            Assert.That(frame, Is.Not.Null, "the stream has to produce a frame");

            if (!owned.IsHardwareAccelerated)
            {
                Assert.Ignore("This machine has no H.264 hardware decoder, so there is no texture to draw.");
            }

            using var target = D3D11RenderTarget.CreateOffscreen(device, width, height);
            byte[] hardwarePixels = RenderFrame(renderer, device, target, frame!);

            using RawVideoFrame software = DecodeFirstFrame(path, hardwareDecoding: false, out _);
            byte[] softwarePixels = RenderFrame(renderer, device, target, software);

            Assert.That(IsSamePixels(hardwarePixels, softwarePixels), Is.True, "the padding rows must not be drawn");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Draws a frame into the target and reads the result back.</summary>
    private static byte[] RenderFrame(D3D11VideoRenderer renderer, D3D11Device device, D3D11RenderTarget target, RawVideoFrame frame)
    {
        renderer.SetOutput(target.View, frame.Width, frame.Height);

        OperationResult result = renderer.Render(frame, VideoTransform.Identity);
        Assert.That(result.IsSuccessful, Is.True, result.Message);

        return target.ReadPixels(device);
    }

    /// <summary>
    /// Whether two drawings of the same picture are the same. The GPU and the CPU do the same arithmetic on
    /// the same samples, so a difference of more than one in a channel means the planes were read wrong.
    /// </summary>
    private static bool IsSamePixels(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int index = 0; index < left.Length; index++)
        {
            if (Math.Abs(left[index] - right[index]) > 1)
            {
                return false;
            }
        }

        return true;
    }

    [Test]
    public void FallsBackToTheNextDecoderWhenTheFirstProducesNothing()
    {
        // libavcodec's own AV1 decoder can only decode on a GPU: given no device it opens and then fails on
        // the first packet, having produced nothing. That is the one failure a codec cannot recover from
        // inside the same decoder, and the reason the candidate list exists.
        string path = VideoFixtureFile.CreateAv1();
        try
        {
            FFmpegSourceProviderFactory factory = new();
            Assert.That(factory.TryOpen(MediaLocator.Parse(path), out IMediaSource? opened).IsSuccessful, Is.True);

            using var source = (FFmpegSource)opened!;
            VideoStreamInfo stream = source.Info.VideoStreams[0];
            Assert.That(stream.CodecId, Is.EqualTo("av1"));

            OperationResult<FFmpegVideoDecoder> createdDecoder = FFmpegVideoDecoder.TryCreate(source, stream, hardwareDecoding: false, gpuFrames: false, gpuDevice: null, bufferedFrames: 0, candidates: ["av1", "libdav1d"]);
            Assert.That(createdDecoder.IsSuccessful, Is.True, createdDecoder.Message);

            using FFmpegVideoDecoder owned = createdDecoder.Result;
            Assert.That(owned.Name, Is.EqualTo("FFmpeg av1"), "the first candidate is what gets opened");

            List<RawVideoFrame> frames = DecodeAll(source, owned);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(frames, Is.Not.Empty, "the fallback has to produce the stream the first decoder could not");
                Assert.That(owned.Name, Is.EqualTo("FFmpeg libdav1d"), "the decoder that failed has to be the one that was replaced");
            }

            DisposeFrames(frames);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void LeavesADecoderThatProducedFramesAlone()
    {
        // The fallback must not paper over a stream that decodes and then breaks: by then the position, the
        // reference frames and the queue are all in play, and a different decoder could not pick them up.
        string path = VideoFixtureFile.CreateH264();
        try
        {
            FFmpegSourceProviderFactory factory = new();
            Assert.That(factory.TryOpen(MediaLocator.Parse(path), out IMediaSource? opened).IsSuccessful, Is.True);

            using var source = (FFmpegSource)opened!;
            VideoStreamInfo stream = source.Info.VideoStreams[0];
            OperationResult<FFmpegVideoDecoder> createdDecoder = FFmpegVideoDecoder.TryCreate(source, stream, hardwareDecoding: false, gpuFrames: false, gpuDevice: null, bufferedFrames: 0, candidates: ["h264", "libdav1d"]);
            Assert.That(createdDecoder.IsSuccessful, Is.True, createdDecoder.Message);

            using FFmpegVideoDecoder owned = createdDecoder.Result;

            OperationResult read = source.TryReadPacket(out MediaPacket? packet);
            Assert.That(read.IsSuccessful, Is.True, read.Message);
            Assert.That(packet, Is.Not.Null);

            using (packet!)
            {
                OperationResult decode = owned.TryDecode(packet, out IReadOnlyList<RawVideoFrame> frames);
                Assert.That(decode.IsSuccessful, Is.True, decode.Message);
                DisposeFrames([.. frames]);
            }

            using MediaPacket broken = new()
            {
                StreamIndex = stream.Index,
                Timestamp = TimeSpan.Zero,
                Duration = TimeSpan.Zero,
                IsKeyFrame = true,
                Buffer = new ForeignBuffer(),
            };

            OperationResult failed = owned.TryDecode(broken, out _);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(failed.IsSuccessful, Is.False, "a packet from another backend has to be refused");
                Assert.That(owned.Name, Is.EqualTo("FFmpeg h264"), "a decoder that already produced a frame is not replaced");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void DecodesAv1OnTheGpuWhenTheMachineHasTheDecoderForIt()
    {
        // The codec that motivated the engine, and the one whose only hardware path is libavcodec's own
        // decoder. Whether this machine can decode it that way is what the test reports.
        string path = VideoFixtureFile.CreateAv1();
        try
        {
            FFmpegSourceProviderFactory factory = new();
            Assert.That(factory.TryOpen(MediaLocator.Parse(path), out IMediaSource? opened).IsSuccessful, Is.True);

            using var source = (FFmpegSource)opened!;
            VideoStreamInfo stream = source.Info.VideoStreams[0];
            OperationResult<FFmpegVideoDecoder> decoderCreated = FFmpegVideoDecoder.TryCreate(source, stream, hardwareDecoding: true, gpuFrames: false, gpuDevice: null, bufferedFrames: 0);
            Assert.That(decoderCreated.IsSuccessful, Is.True, decoderCreated.Message);

            using FFmpegVideoDecoder owned = decoderCreated.Result;
            List<RawVideoFrame> frames = DecodeAll(source, owned);
            bool accelerated = owned.IsHardwareAccelerated;
            string name = owned.Name;
            DisposeFrames(frames);

            Assert.That(frames, Is.Not.Empty);

            if (!accelerated)
            {
                Assert.Ignore("This machine has no AV1 hardware decoder, so the stream decoded in software.");
            }

            Assert.That(name, Is.EqualTo("FFmpeg av1"), "the hardware path is libavcodec's own AV1 decoder");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void RejectsPacketsFromAnotherBackend()
    {
        using FFmpegSource source = OpenImage();
        using IVideoDecoder decoder = CreateDecoder(source);

        using MediaPacket packet = new()
        {
            StreamIndex = 0,
            Timestamp = TimeSpan.Zero,
            Duration = TimeSpan.Zero,
            IsKeyFrame = true,
            Buffer = new ForeignBuffer(),
        };

        OperationResult result = decoder.TryDecode(packet, out IReadOnlyList<RawVideoFrame> frames);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccessful, Is.False);
            Assert.That(frames, Is.Empty);
        }
    }

    private static void DisposeFrames(List<RawVideoFrame> frames)
    {
        foreach (RawVideoFrame frame in frames)
        {
            frame.Dispose();
        }
    }

    /// <summary>
    /// Decodes the whole fixture and keeps the first frame, so that what comes out of a hardware decoder
    /// and what comes out of a software one can be held side by side.
    /// </summary>
    private static RawVideoFrame DecodeFirstFrame(string path, bool hardwareDecoding, out bool accelerated)
    {
        FFmpegSourceProviderFactory factory = new();
        Assert.That(factory.TryOpen(MediaLocator.Parse(path), out IMediaSource? opened).IsSuccessful, Is.True);

        using var source = (FFmpegSource)opened!;
        using IVideoDecoder decoder = CreateDecoder(source, hardwareDecoding);

        List<RawVideoFrame> frames = DecodeAll(source, decoder);
        Assert.That(frames, Is.Not.Empty, "a stream has to decode whether or not the GPU is used");
        accelerated = decoder.IsHardwareAccelerated;

        RawVideoFrame first = frames[0];
        for (int index = 1; index < frames.Count; index++)
        {
            frames[index].Dispose();
        }

        return first;
    }

    /// <summary>
    /// Whether two frames of the same stream carry the same luma. Hardware decoders are not required to be
    /// bit exact, so a sample may be off by one; a frame that came back from the wrong place would be off
    /// by far more than that.
    /// </summary>
    private static bool IsSamePicture(RawVideoFrame left, RawVideoFrame right)
    {
        var leftPlanes = (IPlanarVideoBuffer)left.Buffer;
        var rightPlanes = (IPlanarVideoBuffer)right.Buffer;

        byte[] leftLuma = CopyLuma(leftPlanes, left.Width, left.Height);
        byte[] rightLuma = CopyLuma(rightPlanes, right.Width, right.Height);

        return IsSameLuma(leftLuma, rightLuma, right.Width, right.Height);
    }

    private static byte[] CopyLuma(IPlanarVideoBuffer buffer, int width, int height)
    {
        nint plane = buffer.GetPlanePointer(0);
        int stride = buffer.GetPlaneStride(0);

        byte[] luma = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(plane + (y * stride), luma, y * width, width);
        }

        return luma;
    }

    private static bool IsSameLuma(byte[] left, byte[] right, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * width) + x;
                if (Math.Abs(left[index] - right[index]) > 1)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Copies the luma out of the texture a hardware frame lives in. The texture belongs to the device that
    /// decoded it, which is the device this reads through.
    /// </summary>
    private static byte[] ReadLuma(D3D11Device device, RawVideoFrame frame, IGpuVideoBuffer buffer)
    {
        var texture = (ID3D11Texture2D)Marshal.GetObjectForIUnknown(buffer.NativeTexture);
        try
        {
            D3D11_TEXTURE2D_DESC description = new()
            {
                Width = (uint)frame.Width,
                Height = (uint)frame.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = frame.Format == PixelFormat.P010 ? DXGI_FORMAT.DXGI_FORMAT_P010 : DXGI_FORMAT.DXGI_FORMAT_NV12,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
                CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            };

            ID3D11Texture2D staging = D3D11Interop.CreateTexture2D(device.Device, description);
            try
            {
                D3D11Interop.CopySubresource(device.Context, staging, 0, texture, (uint)buffer.SubresourceIndex);

                byte[] luma = new byte[frame.Width * frame.Height];
                D3D11Interop.ReadRows(device.Context, staging, luma, frame.Width, frame.Height, frame.Width);
                return luma;
            }
            finally
            {
                D3D11Interop.Release(staging);
            }
        }
        finally
        {
            D3D11Interop.Release(texture);
        }
    }

    private FFmpegSource OpenImage()
    {
        FFmpegSourceProviderFactory factory = new();
        OperationResult result = factory.TryOpen(MediaLocator.Parse(_imagePath), out IMediaSource? source);
        Assert.That(result.IsSuccessful, Is.True, result.Message);
        Assert.That(source, Is.InstanceOf<FFmpegSource>());

        return (FFmpegSource)source!;
    }

    private static IVideoDecoder CreateDecoder(FFmpegSource source, bool hardwareDecoding = true)
    {
        Assert.That(source.Info.VideoStreams, Is.Not.Empty);
        VideoStreamInfo stream = source.Info.VideoStreams[0];

        FFmpegVideoDecoderFactory factory = new();
        DecoderOptions options = new() { HardwareDecoding = hardwareDecoding };
        OperationResult result = factory.TryCreate(source, stream, options, out IComponentCreator<IVideoDecoder>? creator);

        Assert.That(result.IsSuccessful, Is.True, result.Message);
        Assert.That(creator, Is.Not.Null);

        OperationResult created = creator!.Create(out IVideoDecoder? decoder);
        Assert.That(created.IsSuccessful, Is.True, created.Message);
        Assert.That(decoder, Is.Not.Null);

        return decoder!;
    }

    private static List<RawVideoFrame> DecodeAll(FFmpegSource source, IVideoDecoder decoder)
    {
        List<RawVideoFrame> frames = [];
        while (true)
        {
            OperationResult readResult = source.TryReadPacket(out MediaPacket? packet);
            Assert.That(readResult.IsSuccessful, Is.True, readResult.Message);
            if (packet is null)
            {
                break;
            }

            using (packet)
            {
                OperationResult decodeResult = decoder.TryDecode(packet, out IReadOnlyList<RawVideoFrame> decoded);
                Assert.That(decodeResult.IsSuccessful, Is.True, decodeResult.Message);
                frames.AddRange(decoded);
            }
        }

        OperationResult drainResult = decoder.Drain(out IReadOnlyList<RawVideoFrame> remaining);
        Assert.That(drainResult.IsSuccessful, Is.True, drainResult.Message);
        frames.AddRange(remaining);

        return frames;
    }

    /// <summary>
    /// Decodes the rest of the stream without keeping the frames. A hardware frame pins a slice of the
    /// decoder's pool for as long as it is alive, so collecting them all would run the pool dry and make the
    /// decoder fail for reasons that have nothing to do with what is being tested.
    /// </summary>
    private static int CountFrames(FFmpegSource source, IVideoDecoder decoder)
    {
        int count = 0;
        while (true)
        {
            OperationResult readResult = source.TryReadPacket(out MediaPacket? packet);
            Assert.That(readResult.IsSuccessful, Is.True, readResult.Message);
            if (packet is null)
            {
                break;
            }

            using (packet)
            {
                OperationResult decodeResult = decoder.TryDecode(packet, out IReadOnlyList<RawVideoFrame> decoded);
                Assert.That(decodeResult.IsSuccessful, Is.True, decodeResult.Message);
                count += decoded.Count;
                foreach (RawVideoFrame frame in decoded)
                {
                    frame.Dispose();
                }
            }
        }

        OperationResult drainResult = decoder.Drain(out IReadOnlyList<RawVideoFrame> remaining);
        Assert.That(drainResult.IsSuccessful, Is.True, drainResult.Message);
        count += remaining.Count;
        foreach (RawVideoFrame frame in remaining)
        {
            frame.Dispose();
        }

        return count;
    }

    [Test]
    public void DecodingWaitsForTheDeviceLock()
    {
        // libavcodec is given the lock with the device and takes it around every use of the immediate context.
        // Without that, the decode thread could build a sequence of calls on the context while the drawing
        // thread was building one.
        OperationResult<D3D11Device> created = D3D11Device.TryCreate();
        Assert.That(created.IsSuccessful, Is.True, created.Message);

        using D3D11Device device = created.Result;

        string path = VideoFixtureFile.CreateH264();
        try
        {
            FFmpegSourceProviderFactory factory = new();
            Assert.That(factory.TryOpen(MediaLocator.Parse(path), out IMediaSource? opened).IsSuccessful, Is.True);

            using var source = (FFmpegSource)opened!;
            VideoStreamInfo stream = source.Info.VideoStreams[0];
            var gpuDevice = new TestGpuDevice(device.NativeDevice);
            OperationResult<FFmpegVideoDecoder> decoderCreated = FFmpegVideoDecoder.TryCreate(source, stream, hardwareDecoding: true, gpuFrames: true, gpuDevice, bufferedFrames: 0);
            Assert.That(decoderCreated.IsSuccessful, Is.True, decoderCreated.Message);

            using FFmpegVideoDecoder owned = decoderCreated.Result;

            // One frame first, because whether this machine decodes on the GPU at all is only known afterwards.
            using RawVideoFrame? first = DecodeFrame(source, owned);
            Assert.That(first, Is.Not.Null, "the stream has to produce a frame");

            if (!owned.IsHardwareAccelerated)
            {
                Assert.Ignore("This machine has no H.264 hardware decoder, so it never takes the lock.");
            }

            // The rest of the file, because one frame is not enough to say the decoder needs the lock at all:
            // libavcodec takes it around the calls that touch the device context, and a frame that happens to
            // need none of them would let a per-frame test pass without proving anything.
            Task<int> decode;
            bool blocked;
            Monitor.Enter(gpuDevice.DeviceLock);
            try
            {
                decode = Task.Run(() => CountFrames(source, owned));
                blocked = !decode.Wait(TimeSpan.FromMilliseconds(250));
            }
            finally
            {
                Monitor.Exit(gpuDevice.DeviceLock);
            }

            Assert.That(blocked, Is.True, "decoding has to wait while another thread holds the device lock");

            Assert.That(decode.Wait(TimeSpan.FromSeconds(10)), Is.True, "decoding has to finish once the lock is free");
            Assert.That(decode.Result, Is.GreaterThan(0), "the rest of the file has to decode");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A hardware decoder allocates its frames from a pool it owns, and a frame its caller is still holding is
    /// one it cannot reuse. Told how many that is, it asks for a pool with room for them; not told, the pool
    /// runs dry and the next decode fails with "cannot allocate memory" while the caller is doing nothing wrong.
    /// </summary>
    [Test]
    public void LeavesRoomInThePoolForTheFramesTheCallerHolds()
    {
        OperationResult<D3D11Device> created = D3D11Device.TryCreate();
        Assert.That(created.IsSuccessful, Is.True, created.Message);

        using D3D11Device device = created.Result;

        string path = VideoFixtureFile.CreateAv1();
        try
        {
            FFmpegSourceProviderFactory factory = new();
            Assert.That(factory.TryOpen(MediaLocator.Parse(path), out IMediaSource? opened).IsSuccessful, Is.True);

            using var source = (FFmpegSource)opened!;
            VideoStreamInfo stream = source.Info.VideoStreams[0];
            var gpuDevice = new TestGpuDevice(device.NativeDevice);

            // Twelve is what libavcodec sizes a D3D11 pool to when it is left to decide, so holding twelve
            // frames is exactly what the pool cannot do without being told about it.
            const int heldFrames = 12;
            OperationResult<FFmpegVideoDecoder> decoderCreated = FFmpegVideoDecoder.TryCreate(source, stream, hardwareDecoding: true, gpuFrames: true, gpuDevice, bufferedFrames: heldFrames);
            Assert.That(decoderCreated.IsSuccessful, Is.True, decoderCreated.Message);

            using FFmpegVideoDecoder owned = decoderCreated.Result;
            List<RawVideoFrame> held = [];
            try
            {
                while (held.Count < heldFrames)
                {
                    if (!TryDecodeOne(source, owned, out IReadOnlyList<RawVideoFrame> decoded))
                    {
                        break;
                    }

                    held.AddRange(decoded);
                }

                Assert.That(held.Count, Is.GreaterThanOrEqualTo(heldFrames), "the stream has to produce enough frames to hold");

                if (!owned.IsHardwareAccelerated)
                {
                    Assert.Ignore("This machine has no AV1 hardware decoder, so the pool is not used at all.");
                }

                // Everything above is still alive: these decodes need surfaces the held frames are not using.
                for (int index = 0; index < 4; index++)
                {
                    Assert.That(TryDecodeOne(source, owned, out IReadOnlyList<RawVideoFrame> decoded), Is.True, "a held frame must not stop the decoder from producing more");
                    held.AddRange(decoded);
                }
            }
            finally
            {
                foreach (RawVideoFrame frame in held)
                {
                    frame.Dispose();
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Decodes packets until one produces a frame. False when the stream ran out first, which is how a caller
    /// asks "is there another frame" without caring how many packets it takes.
    /// </summary>
    private static bool TryDecodeOne(FFmpegSource source, IVideoDecoder decoder, out IReadOnlyList<RawVideoFrame> frames)
    {
        while (true)
        {
            OperationResult readResult = source.TryReadPacket(out MediaPacket? packet);
            Assert.That(readResult.IsSuccessful, Is.True, readResult.Message);
            if (packet is null)
            {
                frames = [];
                return false;
            }

            using (packet)
            {
                OperationResult decodeResult = decoder.TryDecode(packet, out frames);
                Assert.That(decodeResult.IsSuccessful, Is.True, decodeResult.Message);
                if (frames.Count > 0)
                {
                    return true;
                }
            }
        }
    }

    /// <summary>A GPU device with nothing behind it: the tests that use it are about the lock, not the device.</summary>
    private sealed class TestGpuDevice : ID3D11GpuDevice
    {
        internal TestGpuDevice(nint device, bool canDisplay = true)
        {
            NativeDevice = device;
            CanDisplay = canDisplay;
        }

        /// <summary>Whether the renderer behind this device claims it can draw the layout it is asked about.</summary>
        internal bool CanDisplay { get; }

        public nint NativeDevice { get; }

        public object DeviceLock { get; } = new();

        public OperationResult CanDisplayGpuFrame(PixelFormat format)
        {
            return CanDisplay
                ? OperationResult.Success()
                : OperationResult.Failure($"This stand-in cannot draw {format} frames.");
        }
    }

    private sealed class ForeignBuffer : IMediaBuffer
    {
        public long Length => 0;

        public void Dispose()
        {
        }
    }

    /// <summary>Writes an uncompressed 32 bit bitmap, which needs no encoder to produce.</summary>
    private static string CreateBitmapFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sineplayer-ffmpeg-{Guid.NewGuid():N}.bmp");
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
                    // Opaque blue-to-green gradient, stored bottom up as BMP requires.
                    writer.Write((byte)(x * 8));
                    writer.Write((byte)(y * 10));
                    writer.Write((byte)200);
                    writer.Write((byte)255);
                }
            }
        }

        return path;
    }
}
