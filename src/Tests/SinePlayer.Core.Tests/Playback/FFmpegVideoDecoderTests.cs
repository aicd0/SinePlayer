// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.FFmpeg;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Exercises the software decoder end to end against a real bitstream. The fixture is a bitmap
/// written by the test, which FFmpeg demuxes as a single frame video stream and decodes through the
/// same codec plumbing a video stream uses.
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
        if (Environment.ProcessorCount < 2)
        {
            Assert.Ignore("This machine has a single processor, where one thread is the only answer.");
        }

        string path = Av1FixtureFile.Create();
        try
        {
            FFmpegSourceProviderFactory factory = new();
            Assert.That(factory.TryOpen(MediaLocator.Parse(path), out IMediaSource? opened).IsSuccessful, Is.True);

            using IMediaSource source = opened!;
            VideoStreamInfo stream = source.Info.VideoStreams[0];
            Assert.That(stream.CodecId, Is.EqualTo("av1"));

            FFmpegVideoDecoderFactory decoderFactory = new();
            Assert.That(decoderFactory.TryCreate(source, stream, DecoderOptions.Default, out IVideoDecoder? decoder).IsSuccessful, Is.True);

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

        PlaybackOperationResult result = decoder.TryDecode(packet, out IReadOnlyList<RawVideoFrame> frames);

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

    private FFmpegSource OpenImage()
    {
        FFmpegSourceProviderFactory factory = new();
        PlaybackOperationResult result = factory.TryOpen(MediaLocator.Parse(_imagePath), out IMediaSource? source);
        Assert.That(result.IsSuccessful, Is.True, result.Message);
        Assert.That(source, Is.InstanceOf<FFmpegSource>());

        return (FFmpegSource)source!;
    }

    private static IVideoDecoder CreateDecoder(FFmpegSource source)
    {
        Assert.That(source.Info.VideoStreams, Is.Not.Empty);
        VideoStreamInfo stream = source.Info.VideoStreams[0];

        FFmpegVideoDecoderFactory factory = new();
        PlaybackOperationResult result = factory.TryCreate(source, stream, DecoderOptions.Default, out IVideoDecoder? decoder);

        Assert.That(result.IsSuccessful, Is.True, result.Message);
        Assert.That(decoder, Is.Not.Null);

        return decoder!;
    }

    private static List<RawVideoFrame> DecodeAll(FFmpegSource source, IVideoDecoder decoder)
    {
        List<RawVideoFrame> frames = [];
        while (true)
        {
            PlaybackOperationResult readResult = source.TryReadPacket(out MediaPacket? packet);
            Assert.That(readResult.IsSuccessful, Is.True, readResult.Message);
            if (packet is null)
            {
                break;
            }

            using (packet)
            {
                PlaybackOperationResult decodeResult = decoder.TryDecode(packet, out IReadOnlyList<RawVideoFrame> decoded);
                Assert.That(decodeResult.IsSuccessful, Is.True, decodeResult.Message);
                frames.AddRange(decoded);
            }
        }

        PlaybackOperationResult drainResult = decoder.Drain(out IReadOnlyList<RawVideoFrame> remaining);
        Assert.That(drainResult.IsSuccessful, Is.True, drainResult.Message);
        frames.AddRange(remaining);

        return frames;
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
