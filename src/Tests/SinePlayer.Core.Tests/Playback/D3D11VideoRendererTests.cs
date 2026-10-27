// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Backends.Direct3D11;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Renders synthetic frames offscreen and reads the result back, which verifies the whole GPU path -
/// texture upload, shader draw and presentation into the target - without needing a window.
/// </summary>
[TestFixture]
public class D3D11VideoRendererTests
{
    private D3D11Device _device = null!;
    private D3D11VideoRenderer _renderer = null!;

    [SetUp]
    public void SetUp()
    {
        OperationResult<D3D11Device> deviceCreated = D3D11Device.TryCreate();
        Assert.That(deviceCreated.IsSuccessful, Is.True, deviceCreated.Message);
        _device = deviceCreated.Result;

        OperationResult<D3D11VideoRenderer> rendererCreated = D3D11VideoRenderer.TryCreate(_device);
        Assert.That(rendererCreated.IsSuccessful, Is.True, rendererCreated.Message);
        _renderer = rendererCreated.Result;
    }

    [TearDown]
    public void TearDown()
    {
        _renderer.Dispose();
        _device.Dispose();
    }

    [Test]
    public void RendersEveryPixelOfASolidFrame()
    {
        const int width = 4;
        const int height = 4;
        const byte blue = 0x20;
        const byte green = 0x40;
        const byte red = 0x80;
        const byte alpha = 0xFF;

        using FrameBuffer buffer = new(width, height, (_, _) => (blue, green, red, alpha));
        using RawVideoFrame frame = CreateFrame(width, height, buffer);
        using var target = D3D11RenderTarget.CreateOffscreen(_device, width, height);

        _renderer.SetOutput(target.View, width, height);
        OperationResult result = _renderer.Render(frame, VideoTransform.Identity);
        Assert.That(result.IsSuccessful, Is.True, result.Message);

        byte[] pixels = target.ReadPixels(_device);
        Assert.That(pixels, Has.Length.EqualTo(width * height * 4));

        for (int index = 0; index < pixels.Length; index += 4)
        {
            int pixel = index / 4;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pixels[index], Is.EqualTo(blue), $"blue of pixel {pixel}");
                Assert.That(pixels[index + 1], Is.EqualTo(green), $"green of pixel {pixel}");
                Assert.That(pixels[index + 2], Is.EqualTo(red), $"red of pixel {pixel}");
                Assert.That(pixels[index + 3], Is.EqualTo(alpha), $"alpha of pixel {pixel}");
            }
        }
    }

    [Test]
    public void KeepsRowOrderTopDown()
    {
        const int width = 4;
        const int height = 8;

        // Rows 0 to 3 are one colour, rows 4 to 7 another, so a vertical flip cannot go unnoticed.
        using FrameBuffer buffer = new(width, height, (_, y) => y < height / 2 ? ((byte)0x10, (byte)0x20, (byte)0x30, (byte)0xFF) : ((byte)0x40, (byte)0x50, (byte)0x60, (byte)0xFF));
        using RawVideoFrame frame = CreateFrame(width, height, buffer);
        using var target = D3D11RenderTarget.CreateOffscreen(_device, width, height);

        _renderer.SetOutput(target.View, width, height);
        OperationResult result = _renderer.Render(frame, VideoTransform.Identity);
        Assert.That(result.IsSuccessful, Is.True, result.Message);

        byte[] pixels = target.ReadPixels(_device);

        int topRow = 0;
        int bottomRow = (height - 1) * width * 4;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pixels[topRow], Is.EqualTo(0x10), "top row must come from the first source row");
            Assert.That(pixels[topRow + 2], Is.EqualTo(0x30), "top row must come from the first source row");
            Assert.That(pixels[bottomRow], Is.EqualTo(0x40), "bottom row must come from the last source row");
            Assert.That(pixels[bottomRow + 2], Is.EqualTo(0x60), "bottom row must come from the last source row");
        }
    }

    [Test]
    public void TurnsThePictureAQuarterClockwise()
    {
        const int width = 4;
        const int height = 2;
        const byte leftRed = 0x10;
        const byte rightRed = 0x40;

        // The source's first column is one colour and the rest another, so which way the picture was turned is
        // visible in the pixels rather than inferred from the geometry.
        using FrameBuffer buffer = new(width, height, (x, _) => x < width / 2 ? (leftRed, (byte)0x20, (byte)0x30, (byte)0xFF) : (rightRed, (byte)0x50, (byte)0x60, (byte)0xFF));
        using RawVideoFrame frame = CreateFrame(width, height, buffer);

        // A turned 4x2 picture is 2x4, so the surface is given that shape and the picture fills it exactly.
        using var target = D3D11RenderTarget.CreateOffscreen(_device, height, width);
        _renderer.SetOutput(target.View, height, width);

        VideoTransform turned = VideoTransform.Identity with { Orientation = new VideoOrientation(1, false) };
        OperationResult result = _renderer.Render(frame, turned);
        Assert.That(result.IsSuccessful, Is.True, result.Message);

        byte[] pixels = target.ReadPixels(_device);
        int firstRow = 0;
        int thirdRow = 2 * height * 4;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pixels[firstRow], Is.EqualTo(leftRed), "a quarter clockwise puts the source's first column along the top");
            Assert.That(pixels[thirdRow], Is.EqualTo(rightRed), "and its last column along the bottom");
        }
    }

    [Test]
    public void MirrorsThePictureLeftToRight()
    {
        const int width = 4;
        const int height = 2;
        const byte leftRed = 0x10;
        const byte rightRed = 0x40;

        using FrameBuffer buffer = new(width, height, (x, _) => x < width / 2 ? (leftRed, (byte)0x20, (byte)0x30, (byte)0xFF) : (rightRed, (byte)0x50, (byte)0x60, (byte)0xFF));
        using RawVideoFrame frame = CreateFrame(width, height, buffer);
        using var target = D3D11RenderTarget.CreateOffscreen(_device, width, height);

        _renderer.SetOutput(target.View, width, height);

        VideoTransform mirrored = VideoTransform.Identity with { Orientation = new VideoOrientation(0, true) };
        OperationResult result = _renderer.Render(frame, mirrored);
        Assert.That(result.IsSuccessful, Is.True, result.Message);

        byte[] pixels = target.ReadPixels(_device);
        int firstColumn = 0;
        int thirdColumn = 2 * 4;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pixels[firstColumn], Is.EqualTo(rightRed), "the source's last column is drawn on the left");
            Assert.That(pixels[thirdColumn], Is.EqualTo(leftRed), "and its first column on the right");
        }
    }

    [Test]
    public void LeavesTheBarsAroundThePictureUntouched()
    {
        const int width = 2;
        const int height = 2;
        const byte red = 0x10;

        using FrameBuffer buffer = new(width, height, (_, _) => (red, (byte)0x20, (byte)0x30, (byte)0xFF));
        using RawVideoFrame frame = CreateFrame(width, height, buffer);

        // A 2x2 picture in a 4x2 surface is centred, so the picture is between two bars. Nothing outside it is
        // drawn: a picture that hung past its own edge would repeat its last column there, and in the chroma
        // plane of a YUV frame that reads as a band of the wrong colour rather than as a repeat.
        using (var wide = D3D11RenderTarget.CreateOffscreen(_device, 4, 2))
        {
            _renderer.SetOutput(wide.View, 4, 2);
            Assert.That(_renderer.Render(frame, VideoTransform.Identity).IsSuccessful, Is.True);

            byte[] pixels = wide.ReadPixels(_device);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pixels[0], Is.Zero, "the left bar is the cleared surface, not the picture");
                Assert.That(pixels[3 * 4], Is.Zero, "and so is the right bar");
                Assert.That(pixels[1 * 4], Is.EqualTo(red), "the picture sits between them");
            }
        }

        // The same picture in a 2x4 surface, which leaves bars above and below.
        using (var tall = D3D11RenderTarget.CreateOffscreen(_device, 2, 4))
        {
            _renderer.SetOutput(tall.View, 2, 4);
            Assert.That(_renderer.Render(frame, VideoTransform.Identity).IsSuccessful, Is.True);

            byte[] pixels = tall.ReadPixels(_device);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pixels[0], Is.Zero, "the top bar is the cleared surface as well");
                Assert.That(pixels[3 * 8], Is.Zero, "and so is the bottom bar");
                Assert.That(pixels[1 * 8], Is.EqualTo(red), "the picture sits between them");
            }
        }
    }

    [Test]
    public void RedrawsAPausedPictureWithTheTransformItIsGiven()
    {
        const int width = 4;
        const int height = 2;

        using FrameBuffer buffer = new(width, height, (x, _) => x < width / 2 ? ((byte)0x10, (byte)0x20, (byte)0x30, (byte)0xFF) : ((byte)0x40, (byte)0x50, (byte)0x60, (byte)0xFF));
        using RawVideoFrame frame = CreateFrame(width, height, buffer);
        using var target = D3D11RenderTarget.CreateOffscreen(_device, width, height);

        _renderer.SetOutput(target.View, width, height);
        Assert.That(_renderer.Render(frame, VideoTransform.Identity).IsSuccessful, Is.True);
        Assert.That(target.ReadPixels(_device)[0], Is.EqualTo(0x10), "the picture starts unmirrored");

        // No new frame arrives: the redraw is what turns the picture that is already there.
        VideoTransform mirrored = VideoTransform.Identity with { Orientation = new VideoOrientation(0, true) };
        OperationResult refreshed = _renderer.RefreshSurface(mirrored);
        Assert.That(refreshed.IsSuccessful, Is.True, refreshed.Message);
        Assert.That(target.ReadPixels(_device)[0], Is.EqualTo(0x40), "the same picture is drawn the other way round");
    }

    /// <summary>
    /// A surface the picture is taken off has to be emptied, not left with the last frame in it: it belongs to the
    /// host, and a renderer that is let go of cannot paint over it again. Emptying also forgets the frame a redraw
    /// would use, or a resize or a pause would put it back.
    /// </summary>
    [Test]
    public void EmptiesTheSurfaceAndForgetsTheFrameItWasShowing()
    {
        const int width = 4;
        const int height = 4;
        const byte red = 0x80;

        using FrameBuffer buffer = new(width, height, (_, _) => ((byte)0x20, (byte)0x40, red, (byte)0xFF));
        using RawVideoFrame frame = CreateFrame(width, height, buffer);
        using var target = D3D11RenderTarget.CreateOffscreen(_device, width, height);

        _renderer.SetOutput(target.View, width, height);
        Assert.That(_renderer.Render(frame, VideoTransform.Identity).IsSuccessful, Is.True);
        Assert.That(target.ReadPixels(_device)[2], Is.EqualTo(red), "the picture is on the surface");

        OperationResult cleared = _renderer.ClearSurface();
        Assert.That(cleared.IsSuccessful, Is.True, cleared.Message);
        Assert.That(BlackChannels(target), Has.All.Zero, "the surface is empty");

        // The frame the redraw would draw is gone with it: a refresh after the clear has nothing to show.
        OperationResult refreshed = _renderer.RefreshSurface(VideoTransform.Identity);
        Assert.That(refreshed.IsSuccessful, Is.True, refreshed.Message);
        Assert.That(BlackChannels(target), Has.All.Zero, "what was cleared does not come back on a redraw");
    }

    /// <summary>The colour of every pixel, with the alpha the clear writes left out of the question.</summary>
    private IEnumerable<byte> BlackChannels(D3D11RenderTarget target)
    {
        byte[] pixels = target.ReadPixels(_device);
        for (int index = 0; index < pixels.Length; index += 4)
        {
            yield return pixels[index];
            yield return pixels[index + 1];
            yield return pixels[index + 2];
        }
    }

    [Test]
    public void ConfiguresSwapChainAndAttachesItToTheTarget()
    {
        FakeRenderTarget target = new() { SurfaceWidth = 16, SurfaceHeight = 8, RasterizationScale = 1.0 };
        VideoStreamInfo stream = CreateStreamInfo(16, 8);

        OperationResult result = _renderer.Configure(stream, target);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccessful, Is.True, result.Message);
            Assert.That(target.AttachedSwapChain, Is.Not.EqualTo(0), "the host must receive the swap chain");
        }
    }

    [Test]
    public void DrawsAndPresentsThroughTheSwapChain()
    {
        FakeRenderTarget target = new() { SurfaceWidth = 8, SurfaceHeight = 4, RasterizationScale = 1.0 };
        VideoStreamInfo stream = CreateStreamInfo(8, 4);

        OperationResult<D3D11SwapChain> createdSwapChain = D3D11SwapChain.TryCreate(_device, 8, 4, 1.0);
        Assert.That(createdSwapChain.IsSuccessful, Is.True, createdSwapChain.Message);
        using D3D11SwapChain ownedSwapChain = createdSwapChain.Result;

        // Draw into the swap chain back buffer without presenting, so the pixels can be verified.
        _renderer.SetOutput(ownedSwapChain.View, ownedSwapChain.Width, ownedSwapChain.Height);

        using FrameBuffer buffer = new(8, 4, (_, _) => (0x11, 0x22, 0x33, 0xFF));
        using RawVideoFrame frame = CreateFrame(8, 4, buffer);
        OperationResult renderResult = _renderer.Render(frame, VideoTransform.Identity);
        Assert.That(renderResult.IsSuccessful, Is.True, renderResult.Message);

        byte[] pixels = ownedSwapChain.ReadPixels();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pixels[0], Is.EqualTo(0x11));
            Assert.That(pixels[1], Is.EqualTo(0x22));
            Assert.That(pixels[2], Is.EqualTo(0x33));
        }

        OperationResult presentResult = ownedSwapChain.Present();
        Assert.That(presentResult.IsSuccessful, Is.True, presentResult.Message);
    }

    [Test]
    public void RejectsFrameWithoutReadablePlanes()
    {
        using RawVideoFrame frame = new()
        {
            Timestamp = TimeSpan.Zero,
            Duration = TimeSpan.Zero,
            Width = 4,
            Height = 4,
            Format = PixelFormat.Bgra8,
            Buffer = new OpaqueBuffer(),
        };

        _renderer.SetOutput(null, 0, 0);
        OperationResult result = _renderer.Render(frame, VideoTransform.Identity);

        Assert.That(result.IsSuccessful, Is.False);
    }

    [Test]
    public void SizesTheSwapChainInPhysicalPixelsAndCancelsTheDisplayScale()
    {
        // A 1280x720 logical panel at 150%: the buffer has to be 1920x1080, and the panel's own scale of
        // 1.5 has to be cancelled, or the display enlarges the picture a second time.
        FakeRenderTarget target = new() { SurfaceWidth = 1280, SurfaceHeight = 720, RasterizationScale = 1.5 };
        VideoStreamInfo stream = CreateStreamInfo(1280, 720);

        OperationResult result = _renderer.Configure(stream, target);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccessful, Is.True, result.Message);
            Assert.That(_renderer.OutputPixelWidth, Is.EqualTo(1920), "the buffer is the panel's logical size at the display's scale");
            Assert.That(_renderer.OutputPixelHeight, Is.EqualTo(1080));
            Assert.That(_renderer.TryGetCompositionTransform(out double scaleX, out double scaleY), Is.True, "the swap chain has to carry the transform");
            Assert.That(scaleX, Is.EqualTo(1.0 / 1.5).Within(0.0001), "and it has to cancel exactly the panel's scale");
            Assert.That(scaleY, Is.EqualTo(1.0 / 1.5).Within(0.0001));
        }
    }

    [Test]
    public void KeepsTheBufferSizeAndTransformInStepWhenTheScaleChanges()
    {
        FakeRenderTarget target = new() { SurfaceWidth = 200, SurfaceHeight = 100, RasterizationScale = 1.0 };
        VideoStreamInfo stream = CreateStreamInfo(200, 100);
        Assert.That(_renderer.Configure(stream, target).IsSuccessful, Is.True);

        using (FrameBuffer buffer = new(200, 100, (_, _) => (0x10, 0x20, 0x30, 0xFF)))
        using (RawVideoFrame frame = CreateFrame(200, 100, buffer))
        {
            Assert.That(_renderer.Render(frame, VideoTransform.Identity).IsSuccessful, Is.True);
        }

        Assert.That(_renderer.TryGetCompositionTransform(out double before, out double _), Is.True);
        Assert.That(before, Is.EqualTo(1.0).Within(0.0001), "a 100% display needs no correction at all");

        // The window moved to a display at 200%, which is what the panel reports as a composition scale
        // change. Resizing the buffers resets the transform in DXGI, so both have to be redone together —
        // and a resize that forgot the transform would leave the picture at half size.
        target.RasterizationScale = 2.0;
        target.RaiseSizeChanged();

        using (FrameBuffer buffer = new(200, 100, (_, _) => (0x10, 0x20, 0x30, 0xFF)))
        using (RawVideoFrame frame = CreateFrame(200, 100, buffer))
        {
            Assert.That(_renderer.Render(frame, VideoTransform.Identity).IsSuccessful, Is.True);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_renderer.OutputPixelWidth, Is.EqualTo(400));
            Assert.That(_renderer.OutputPixelHeight, Is.EqualTo(200));
            Assert.That(_renderer.TryGetCompositionTransform(out double scaleX, out double scaleY), Is.True);
            Assert.That(scaleX, Is.EqualTo(0.5).Within(0.0001), "the transform has to follow the new scale");
            Assert.That(scaleY, Is.EqualTo(0.5).Within(0.0001));
        }
    }

    [Test]
    public void TakesUpASurfaceChangeWhilePaused()
    {
        FakeRenderTarget target = new() { SurfaceWidth = 100, SurfaceHeight = 50, RasterizationScale = 1.0 };
        VideoStreamInfo stream = CreateStreamInfo(100, 50);
        Assert.That(_renderer.Configure(stream, target).IsSuccessful, Is.True);

        using (FrameBuffer buffer = new(100, 50, (_, _) => (0x11, 0x22, 0x33, 0xFF)))
        using (RawVideoFrame frame = CreateFrame(100, 50, buffer))
        {
            Assert.That(_renderer.Render(frame, VideoTransform.Identity).IsSuccessful, Is.True);
        }

        // Playback is paused, so nothing is presented: only the host's report of a new surface arrives.
        target.SurfaceWidth = 200;
        target.SurfaceHeight = 100;
        target.RasterizationScale = 2.0;
        target.RaiseSizeChanged();

        OperationResult refreshed = _renderer.RefreshSurface(VideoTransform.Identity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refreshed.IsSuccessful, Is.True, refreshed.Message);
            Assert.That(_renderer.OutputPixelWidth, Is.EqualTo(400), "the surface has to follow the window");
            Assert.That(_renderer.OutputPixelHeight, Is.EqualTo(200));
            Assert.That(_renderer.TryGetCompositionTransform(out double scaleX, out double _), Is.True);
            Assert.That(scaleX, Is.EqualTo(0.5).Within(0.0001));
        }
    }

    [Test]
    public void RedrawsTheLastFrameWhenTheSurfaceChangesWithoutANewFrame()
    {
        const int width = 4;
        const int height = 4;
        const byte blue = 0x40;
        const byte green = 0x50;
        const byte red = 0x60;

        using var target = D3D11RenderTarget.CreateOffscreen(_device, width, height);
        _renderer.SetOutput(target.View, width, height);

        // The frame is released before the redraw, so what appears afterwards can only come from the
        // planes the renderer kept — which is the whole point: a paused picture has no frame to offer.
        using (FrameBuffer buffer = new(width, height, (_, _) => (blue, green, red, 0xFF)))
        using (RawVideoFrame frame = CreateFrame(width, height, buffer))
        {
            Assert.That(_renderer.Render(frame, VideoTransform.Identity).IsSuccessful, Is.True);
        }

        _device.Context.ClearRenderTargetView(target.View, [0.0f, 0.0f, 0.0f, 1.0f]);
        Assert.That(target.ReadPixels(_device)[0], Is.Zero, "the surface is emptied first, so what follows is drawn rather than left behind");

        Assert.That(_renderer.RefreshSurface(VideoTransform.Identity).IsSuccessful, Is.True);

        byte[] pixels = target.ReadPixels(_device);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pixels[0], Is.EqualTo(blue), "the last picture has to be drawn again");
            Assert.That(pixels[1], Is.EqualTo(green));
            Assert.That(pixels[2], Is.EqualTo(red));
        }
    }

    [Test]
    public void RefreshingWithoutAnyFrameIsHarmless()
    {
        FakeRenderTarget target = new() { SurfaceWidth = 16, SurfaceHeight = 8, RasterizationScale = 1.0 };
        VideoStreamInfo stream = CreateStreamInfo(16, 8);
        Assert.That(_renderer.Configure(stream, target).IsSuccessful, Is.True);

        // Opened and paused before the first frame: there is nothing to draw again, and the resize the
        // surface reported still has to be taken up.
        target.SurfaceWidth = 32;
        target.SurfaceHeight = 16;
        target.RaiseSizeChanged();

        OperationResult refreshed = _renderer.RefreshSurface(VideoTransform.Identity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refreshed.IsSuccessful, Is.True, refreshed.Message);
            Assert.That(_renderer.OutputPixelWidth, Is.EqualTo(32));
            Assert.That(_renderer.OutputPixelHeight, Is.EqualTo(16));
        }
    }

    [Test]
    public void ReportsMissingOutputSurface()
    {
        using FrameBuffer buffer = new(4, 4, (_, _) => (1, 2, 3, 4));
        using RawVideoFrame frame = CreateFrame(4, 4, buffer);

        _renderer.SetOutput(null, 0, 0);
        OperationResult result = _renderer.Render(frame, VideoTransform.Identity);

        Assert.That(result.IsSuccessful, Is.False);
    }

    private static VideoStreamInfo CreateStreamInfo(int width, int height)
    {
        return new VideoStreamInfo
        {
            Index = 0,
            CodecId = "bmp",
            Width = width,
            Height = height,
            FrameRate = 25,
            PixelFormat = PixelFormat.Bgra8,
        };
    }

    private static RawVideoFrame CreateFrame(int width, int height, IMediaBuffer buffer)
    {
        return new RawVideoFrame
        {
            Timestamp = TimeSpan.Zero,
            Duration = TimeSpan.FromMilliseconds(40),
            Width = width,
            Height = height,
            Format = PixelFormat.Bgra8,
            Buffer = buffer,
        };
    }

    /// <summary>Stands in for the host application's presentation element.</summary>
    private sealed class FakeRenderTarget : IVideoRenderTarget
    {
        public int SurfaceWidth { get; set; }

        public int SurfaceHeight { get; set; }

        public double RasterizationScale { get; set; }

        public nint AttachedSwapChain { get; private set; }

        public event Action? SurfaceSizeChanged;

        public void AttachPresentationSurface(nint swapChain)
        {
            AttachedSwapChain = swapChain;
        }

        public void RaiseSizeChanged()
        {
            SurfaceSizeChanged?.Invoke();
        }
    }

    /// <summary>Packed BGRA pixels in unmanaged memory, with padding between rows.</summary>
    private sealed class FrameBuffer : IPlanarVideoBuffer
    {
        private const int RowPadding = 8;

        private nint _memory;
        private readonly int _stride;
        private readonly int _height;

        internal FrameBuffer(int width, int height, Func<int, int, (byte Blue, byte Green, byte Red, byte Alpha)> pixel)
        {
            _stride = (width * 4) + RowPadding;
            _height = height;
            _memory = Marshal.AllocHGlobal(_stride * height);

            byte[] row = new byte[_stride];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    (byte blue, byte green, byte red, byte alpha) = pixel(x, y);
                    int offset = x * 4;
                    row[offset] = blue;
                    row[offset + 1] = green;
                    row[offset + 2] = red;
                    row[offset + 3] = alpha;
                }

                Marshal.Copy(row, 0, _memory + (y * _stride), _stride);
            }
        }

        public int PlaneCount => 1;

        public long Length => (long)_stride * _height;

        public nint GetPlanePointer(int plane)
        {
            return plane == 0 ? _memory : 0;
        }

        public int GetPlaneStride(int plane)
        {
            return plane == 0 ? _stride : 0;
        }

        public void Dispose()
        {
            nint memory = _memory;
            if (memory == 0)
            {
                return;
            }

            _memory = 0;
            Marshal.FreeHGlobal(memory);
        }
    }

    private sealed class OpaqueBuffer : IMediaBuffer
    {
        public long Length => 0;

        public void Dispose()
        {
        }
    }
}