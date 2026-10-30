// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Thumbnails;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The step between a decoded frame and a picture: which plane holds which channel, what a luma value comes out as,
/// and which way round the picture ends up. Frames are built by hand, because a fixture file cannot be cut into the
/// pixel layouts and the display matrix this has to answer for.
/// </summary>
[TestFixture]
public class BgraFrameConverterTests
{
    [Test]
    public void KeepsPackedPixelsInTheOrderBgraExpects()
    {
        byte[] pixels = [10, 20, 30, 255, 40, 50, 60, 255, 70, 80, 90, 255, 100, 110, 120, 255];
        var buffer = FrameBuffer.CreatePacked(4, 1, pixels);
        using RawVideoFrame frame = Frame(buffer, 4, 1, PixelFormat.Bgra8);

        MediaThumbnail? thumbnail = BgraFrameConverter.TryConvert(frame, maxEdge: 8, new VideoOrientation(0, false));

        Assert.That(thumbnail, Is.Not.Null);
        byte[] converted = thumbnail.Pixels;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(converted[0], Is.EqualTo(10), "blue stays blue");
            Assert.That(converted[1], Is.EqualTo(20));
            Assert.That(converted[2], Is.EqualTo(30), "red stays red");
            Assert.That(converted[3], Is.EqualTo(255), "the picture is opaque");
        }
    }

    [Test]
    public void TurnsAQuarterTurnIntoTheShapeAndTheConcernsOfTheTurn()
    {
        // A 4x2 picture whose left half is red: a quarter turn shows in the shape, and in which edge is red.
        byte[] pixels = new byte[4 * 2 * 4];
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int offset = ((y * 4) + x) * 4;
                pixels[offset + 2] = x < 2 ? (byte)255 : (byte)0;
                pixels[offset + 3] = 255;
            }
        }

        var buffer = FrameBuffer.CreatePacked(4, 2, pixels);
        using RawVideoFrame frame = Frame(buffer, 4, 2, PixelFormat.Bgra8);

        MediaThumbnail? thumbnail = BgraFrameConverter.TryConvert(frame, maxEdge: 8, new VideoOrientation(1, false));

        Assert.That(thumbnail, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(thumbnail.Width, Is.EqualTo(2), "a quarter turn swaps the sides");
            Assert.That(thumbnail.Height, Is.EqualTo(4));

            // Turned clockwise, the left half of the picture becomes its top half.
            Assert.That(Red(thumbnail, 0, 0), Is.EqualTo(255), "the red half runs along one edge");
            Assert.That(Red(thumbnail, 1, 1), Is.EqualTo(255));
            Assert.That(Red(thumbnail, 0, 3), Is.EqualTo(0), "and the other edge is not red");
            Assert.That(Red(thumbnail, 1, 2), Is.EqualTo(0));
        }
    }

    [Test]
    public void TurnsTwoQuarterTurnsIntoTheSameShapeUpsideDown()
    {
        byte[] pixels = new byte[4 * 2 * 4];
        pixels[2] = 255; // the top left pixel is red
        pixels[3] = 255;

        var buffer = FrameBuffer.CreatePacked(4, 2, pixels);
        using RawVideoFrame frame = Frame(buffer, 4, 2, PixelFormat.Bgra8);

        MediaThumbnail? thumbnail = BgraFrameConverter.TryConvert(frame, maxEdge: 8, new VideoOrientation(2, false));

        Assert.That(thumbnail, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(thumbnail.Width, Is.EqualTo(4));
            Assert.That(thumbnail.Height, Is.EqualTo(2));
            Assert.That(Red(thumbnail, 0, 0), Is.EqualTo(0), "the red corner moves to the other end");
            Assert.That(Red(thumbnail, 3, 1), Is.EqualTo(255));
        }
    }

    /// <summary>
    /// A selfie camera records the picture a mirror would show, so the tag has to be undone as well as the turn — and it
    /// is undone first, because a mirror turns the other way round.
    /// </summary>
    [Test]
    public void MirrorsThePictureAlongTheEdgeTheTagAsksFor()
    {
        byte[] pixels = new byte[4 * 2 * 4];
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int offset = ((y * 4) + x) * 4;
                pixels[offset + 2] = x < 2 ? (byte)255 : (byte)0;
                pixels[offset + 3] = 255;
            }
        }

        var buffer = FrameBuffer.CreatePacked(4, 2, pixels);
        using RawVideoFrame frame = Frame(buffer, 4, 2, PixelFormat.Bgra8);

        MediaThumbnail? mirror = BgraFrameConverter.TryConvert(frame, maxEdge: 8, new VideoOrientation(0, true));
        MediaThumbnail? turnedAndMirrored = BgraFrameConverter.TryConvert(frame, maxEdge: 8, new VideoOrientation(1, true));

        Assert.That(mirror, Is.Not.Null);
        Assert.That(turnedAndMirrored, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Red(mirror, 0, 0), Is.EqualTo(0), "the red half changes sides");
            Assert.That(Red(mirror, 3, 0), Is.EqualTo(255));

            Assert.That(turnedAndMirrored.Width, Is.EqualTo(2), "and a turn still swaps the sides");
            Assert.That(turnedAndMirrored.Height, Is.EqualTo(4));
            Assert.That(Red(turnedAndMirrored, 0, 0), Is.EqualTo(255), "the red half runs along the other edge");
            Assert.That(Red(turnedAndMirrored, 1, 3), Is.EqualTo(0));
        }
    }

    [Test]
    public void ReadsLumaOutOfAPlanarFrame()
    {
        // A full white picture in limited range: luma 235 has to come out close to white, not as a dim grey.
        var buffer = FrameBuffer.CreatePlanarYuv420(2, 2);
        buffer.FillPlane(0, 235);
        buffer.FillPlane(1, 128);
        buffer.FillPlane(2, 128);
        using RawVideoFrame frame = Frame(buffer, 2, 2, PixelFormat.Yuv420P);

        MediaThumbnail? thumbnail = BgraFrameConverter.TryConvert(frame, maxEdge: 8, new VideoOrientation(0, false));

        Assert.That(thumbnail, Is.Not.Null);
        Assert.That(Red(thumbnail, 0, 0), Is.EqualTo(255).Within(2));
    }

    [Test]
    public void ReportsALayoutItCannotRead()
    {
        var buffer = FrameBuffer.CreatePacked(4, 1, new byte[16]);
        using RawVideoFrame frame = Frame(buffer, 4, 1, PixelFormat.Unknown);

        Assert.That(BgraFrameConverter.TryConvert(frame, maxEdge: 8, new VideoOrientation(0, false)), Is.Null);
    }

    private static byte Red(MediaThumbnail thumbnail, int x, int y)
    {
        return thumbnail.Pixels[(y * thumbnail.Stride) + (x * 4) + 2];
    }

    private static RawVideoFrame Frame(FrameBuffer buffer, int width, int height, PixelFormat format)
    {
        return new RawVideoFrame
        {
            Timestamp = TimeSpan.Zero,
            Duration = TimeSpan.FromMilliseconds(33),
            Width = width,
            Height = height,
            Format = format,
            Buffer = buffer,
            Colour = new ColourInfo(ColourPrimaries.Bt709, ColourTransfer.Bt709, ColourRange.Limited),
        };
    }

    /// <summary>Planes in system memory, laid out the way a software decoder hands them out.</summary>
    private sealed unsafe class FrameBuffer : IPlanarVideoBuffer
    {
        private readonly List<nint> _pointers = [];
        private readonly List<int> _lengths = [];
        private readonly List<int> _strides = [];

        private FrameBuffer() { }

        public int PlaneCount => _pointers.Count;

        public long Length => _lengths.Sum(length => (long)length);

        internal static FrameBuffer CreatePacked(int width, int height, byte[] pixels)
        {
            FrameBuffer buffer = new();
            buffer.AddPlane(pixels, width * 4);
            return buffer;
        }

        internal static FrameBuffer CreatePlanarYuv420(int width, int height)
        {
            FrameBuffer buffer = new();
            buffer.AddPlane(new byte[width * height], width);
            buffer.AddPlane(new byte[width / 2 * (height / 2)], width / 2);
            buffer.AddPlane(new byte[width / 2 * (height / 2)], width / 2);
            return buffer;
        }

        internal void FillPlane(int plane, byte value)
        {
            byte* pointer = (byte*)_pointers[plane];
            for (int i = 0; i < _lengths[plane]; i++)
            {
                pointer[i] = value;
            }
        }

        public nint GetPlanePointer(int plane) => _pointers[plane];

        public int GetPlaneStride(int plane) => _strides[plane];

        public void Dispose()
        {
            foreach (nint pointer in _pointers)
            {
                Marshal.FreeHGlobal(pointer);
            }

            _pointers.Clear();
            _lengths.Clear();
            _strides.Clear();
        }

        private void AddPlane(byte[] pixels, int stride)
        {
            nint pointer = Marshal.AllocHGlobal(pixels.Length);
            Marshal.Copy(pixels, 0, pointer, pixels.Length);
            _pointers.Add(pointer);
            _lengths.Add(pixels.Length);
            _strides.Add(stride);
        }
    }
}
