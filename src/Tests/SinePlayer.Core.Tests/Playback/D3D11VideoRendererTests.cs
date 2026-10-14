// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;

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
        bool deviceCreated = D3D11Device.TryCreate(out D3D11Device? device, out string deviceError);
        Assert.That(deviceCreated, Is.True, deviceError);
        _device = device!;

        bool rendererCreated = D3D11VideoRenderer.TryCreate(_device, out D3D11VideoRenderer? renderer, out string rendererError);
        Assert.That(rendererCreated, Is.True, rendererError);
        _renderer = renderer!;
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
        PlaybackResult result = _renderer.Render(frame);
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
        PlaybackResult result = _renderer.Render(frame);
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
    public void ConfiguresSwapChainAndAttachesItToTheTarget()
    {
        FakeRenderTarget target = new() { SurfaceWidth = 16, SurfaceHeight = 8, RasterizationScale = 1.0 };
        VideoStreamInfo stream = CreateStreamInfo(16, 8);

        PlaybackResult result = _renderer.Configure(stream, target);

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

        Assert.That(D3D11SwapChain.TryCreate(_device, 8, 4, out D3D11SwapChain? swapChain, out string error), Is.True, error);
        using D3D11SwapChain ownedSwapChain = swapChain!;

        // Draw into the swap chain back buffer without presenting, so the pixels can be verified.
        _renderer.SetOutput(ownedSwapChain.View, ownedSwapChain.Width, ownedSwapChain.Height);

        using FrameBuffer buffer = new(8, 4, (_, _) => (0x11, 0x22, 0x33, 0xFF));
        using RawVideoFrame frame = CreateFrame(8, 4, buffer);
        PlaybackResult renderResult = _renderer.Render(frame);
        Assert.That(renderResult.IsSuccessful, Is.True, renderResult.Message);

        byte[] pixels = ownedSwapChain.ReadPixels();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pixels[0], Is.EqualTo(0x11));
            Assert.That(pixels[1], Is.EqualTo(0x22));
            Assert.That(pixels[2], Is.EqualTo(0x33));
        }

        PlaybackResult presentResult = ownedSwapChain.Present();
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
        PlaybackResult result = _renderer.Render(frame);

        Assert.That(result.IsSuccessful, Is.False);
    }

    [Test]
    public void ReportsMissingOutputSurface()
    {
        using FrameBuffer buffer = new(4, 4, (_, _) => (1, 2, 3, 4));
        using RawVideoFrame frame = CreateFrame(4, 4, buffer);

        _renderer.SetOutput(null, 0, 0);
        PlaybackResult result = _renderer.Render(frame);

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
        public int SurfaceWidth { get; init; }

        public int SurfaceHeight { get; init; }

        public double RasterizationScale { get; init; }

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