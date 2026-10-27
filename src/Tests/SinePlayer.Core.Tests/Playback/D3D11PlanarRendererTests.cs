// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Backends.Direct3D11;

using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Verifies the planar frame layouts that decoded video actually produces. The colours are chosen so
/// the expected result is known exactly: BT.601 limited range maps luma 235 with neutral chroma to
/// white and luma 16 to black, and a specific triple maps to pure red.
/// </summary>
[TestFixture]
public class D3D11PlanarRendererTests
{
    private const int Width = 4;
    private const int Height = 4;

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
    public void ConvertsSemiPlanarNv12ToWhite()
    {
        // Y = 235, U = V = 128 is white in BT.601 limited range.
        using PlanarFrameBuffer buffer = new(
            (Width, Height, 1, (_, _) => [0xEB]),
            ((Width + 1) / 2, (Height + 1) / 2, 2, (_, _) => [0x80, 0x80]));

        byte[] pixels = Render(PixelFormat.Nv12, buffer);

        AssertPixel(pixels, 0, 255, 255, 255);
        AssertPixel(pixels, (Width * Height) - 1, 255, 255, 255);
    }

    [Test]
    public void ConvertsSemiPlanarNv12ToRed()
    {
        // Y = 81, U = 90, V = 240 is pure red in BT.601 limited range.
        using PlanarFrameBuffer buffer = new(
            (Width, Height, 1, (_, _) => [0x51]),
            ((Width + 1) / 2, (Height + 1) / 2, 2, (_, _) => [0x5A, 0xF0]));

        byte[] pixels = Render(PixelFormat.Nv12, buffer);

        AssertPixel(pixels, 0, 255, 0, 0);
    }

    [Test]
    public void ConvertsPlanarYuv420ToWhite()
    {
        using PlanarFrameBuffer buffer = new(
            (Width, Height, 1, (_, _) => [0xEB]),
            ((Width + 1) / 2, (Height + 1) / 2, 1, (_, _) => [0x80]),
            ((Width + 1) / 2, (Height + 1) / 2, 1, (_, _) => [0x80]));

        byte[] pixels = Render(PixelFormat.Yuv420P, buffer);

        AssertPixel(pixels, 0, 255, 255, 255);
        AssertPixel(pixels, (Width * Height) - 1, 255, 255, 255);
    }

    [Test]
    public void ConvertsPlanarYuv420ToBlack()
    {
        // Y = 16 is black regardless of chroma in limited range.
        using PlanarFrameBuffer buffer = new(
            (Width, Height, 1, (_, _) => [0x10]),
            ((Width + 1) / 2, (Height + 1) / 2, 1, (_, _) => [0x80]),
            ((Width + 1) / 2, (Height + 1) / 2, 1, (_, _) => [0x80]));

        byte[] pixels = Render(PixelFormat.Yuv420P, buffer);

        AssertPixel(pixels, 0, 0, 0, 0);
    }

    [Test]
    public void RejectsLayoutWithMissingPlanes()
    {
        // A semi planar layout needs two planes; one is not enough.
        using PlanarFrameBuffer buffer = new((Width, Height, 1, (_, _) => [0xEB]));
        using RawVideoFrame frame = CreateFrame(PixelFormat.Nv12, buffer);

        using var target = D3D11RenderTarget.CreateOffscreen(_device, Width, Height);
        _renderer.SetOutput(target.View, Width, Height);

        OperationResult result = _renderer.Render(frame, VideoTransform.Identity);

        Assert.That(result.IsSuccessful, Is.False);
    }

    [Test]
    public void WaitsForTheDeviceLockBeforeDrawing()
    {
        // A decoder sharing this device holds the same lock around its own calls, so a draw that did not take
        // it could build its sequence on the immediate context while another thread was building one too.
        using PlanarFrameBuffer buffer = new(
            (Width, Height, 1, (_, _) => [0xEB]),
            ((Width + 1) / 2, (Height + 1) / 2, 2, (_, _) => [0x80, 0x80]));

        using RawVideoFrame frame = CreateFrame(PixelFormat.Nv12, buffer);
        using var target = D3D11RenderTarget.CreateOffscreen(_device, Width, Height);
        _renderer.SetOutput(target.View, Width, Height);

        object deviceLock = _renderer.DeviceLock;
        Task<OperationResult> draw;
        bool blocked;
        Monitor.Enter(deviceLock);
        try
        {
            draw = Task.Run(() => _renderer.Render(frame, VideoTransform.Identity));
            blocked = !draw.Wait(TimeSpan.FromMilliseconds(250));
        }
        finally
        {
            Monitor.Exit(deviceLock);
        }

        Assert.That(blocked, Is.True, "a draw has to wait while another thread holds the device lock");
        Assert.That(draw.Wait(TimeSpan.FromSeconds(5)), Is.True, "the draw has to finish once the lock is free");
        Assert.That(draw.Result.IsSuccessful, Is.True, draw.Result.Message);
    }

    [Test]
    public void ConvertsSemiPlanarP010ToWhite()
    {
        // A P010 texel is the ten-bit code left-aligned in sixteen bits, so the ten-bit equivalent of the eight-bit
        // Y = 235 / U = V = 128 is 0xEB00 and 0x8000 — the same colour, and the same shader, as the NV12 case above.
        using PlanarFrameBuffer buffer = new(
            (Width, Height, 2, (_, _) => [0x00, 0xEB]),
            ((Width + 1) / 2, (Height + 1) / 2, 4, (_, _) => [0x00, 0x80, 0x00, 0x80]));

        byte[] pixels = Render(PixelFormat.P010, buffer);

        // Three levels of slack rather than two: normalising a ten-bit code by 1023 is very slightly darker than
        // normalising the eight-bit code it came from by 255, which is the whole reason no second shader is needed.
        AssertPixel(pixels, 0, 255, 255, 255, 3);
        AssertPixel(pixels, (Width * Height) - 1, 255, 255, 255, 3);
    }

    [Test]
    public void ConvertsSemiPlanarP010ToRed()
    {
        using PlanarFrameBuffer buffer = new(
            (Width, Height, 2, (_, _) => [0x00, 0x51]),
            ((Width + 1) / 2, (Height + 1) / 2, 4, (_, _) => [0x00, 0x5A, 0x00, 0xF0]));

        byte[] pixels = Render(PixelFormat.P010, buffer);

        AssertPixel(pixels, 0, 255, 0, 0, 3);
    }

    [Test]
    public void ConvertsSemiPlanarP010ToBlack()
    {
        using PlanarFrameBuffer buffer = new(
            (Width, Height, 2, (_, _) => [0x00, 0x04]),
            ((Width + 1) / 2, (Height + 1) / 2, 4, (_, _) => [0x00, 0x80, 0x00, 0x80]));

        byte[] pixels = Render(PixelFormat.P010, buffer);

        AssertPixel(pixels, 0, 0, 0, 0, 3);
    }

    /// <summary>
    /// A ten-bit frame that is still a texture is drawn rather than refused. The texture of a hardware decoder is
    /// P010, whose planes only cast to sixteen-bit views, and that is what the failure this covers looked like:
    /// "the Direct3D renderer cannot display P010 frames that are still on the GPU".
    /// </summary>
    [Test]
    public void DrawsATenBitFrameThatIsStillOnTheGpu()
    {
        using P010Texture texture = new(_device, Width, Height);

        using FakeGpuBuffer gpuBuffer = new(texture.Native);
        using var frame = new RawVideoFrame
        {
            Timestamp = TimeSpan.Zero,
            Duration = TimeSpan.FromMilliseconds(40),
            Width = Width,
            Height = Height,
            Format = PixelFormat.P010,
            Buffer = gpuBuffer,
            IsHardwareBacked = true,
        };

        using var target = D3D11RenderTarget.CreateOffscreen(_device, Width, Height);
        _renderer.SetOutput(target.View, Width, Height);

        OperationResult result = _renderer.Render(frame, VideoTransform.Identity);

        Assert.That(result.IsSuccessful, Is.True, result.Message);
        Assert.That(target.ReadPixels(_device), Has.Length.EqualTo(Width * Height * 4));
    }

    /// <summary>
    /// A ten-bit frame the CPU decoded is three planar components sixteen bits wide — the layout a software decoder
    /// produces when hardware decoding is off, and the one the renderer had no place for.
    /// </summary>
    [Test]
    public void ConvertsPlanarTenBitToWhite()
    {
        using PlanarFrameBuffer buffer = new(
            (Width, Height, 2, (_, _) => [0x00, 0xEB]),
            ((Width + 1) / 2, (Height + 1) / 2, 2, (_, _) => [0x00, 0x80]),
            ((Width + 1) / 2, (Height + 1) / 2, 2, (_, _) => [0x00, 0x80]));

        byte[] pixels = Render(PixelFormat.Yuv420P10, buffer);

        AssertPixel(pixels, 0, 255, 255, 255, 3);
        AssertPixel(pixels, (Width * Height) - 1, 255, 255, 255, 3);
    }

    [Test]
    public void ConvertsPlanarTenBitToRed()
    {
        using PlanarFrameBuffer buffer = new(
            (Width, Height, 2, (_, _) => [0x00, 0x51]),
            ((Width + 1) / 2, (Height + 1) / 2, 2, (_, _) => [0x00, 0x5A]),
            ((Width + 1) / 2, (Height + 1) / 2, 2, (_, _) => [0x00, 0xF0]));

        byte[] pixels = Render(PixelFormat.Yuv420P10, buffer);

        AssertPixel(pixels, 0, 255, 0, 0, 3);
    }

    [Test]
    public void ConvertsPlanarTenBitToBlack()
    {
        using PlanarFrameBuffer buffer = new(
            (Width, Height, 2, (_, _) => [0x00, 0x04]),
            ((Width + 1) / 2, (Height + 1) / 2, 2, (_, _) => [0x00, 0x80]),
            ((Width + 1) / 2, (Height + 1) / 2, 2, (_, _) => [0x00, 0x80]));

        byte[] pixels = Render(PixelFormat.Yuv420P10, buffer);

        AssertPixel(pixels, 0, 0, 0, 0, 3);
    }

    private byte[] Render(PixelFormat format, PlanarFrameBuffer buffer)
    {
        using RawVideoFrame frame = CreateFrame(format, buffer);
        using var target = D3D11RenderTarget.CreateOffscreen(_device, Width, Height);

        _renderer.SetOutput(target.View, Width, Height);
        OperationResult result = _renderer.Render(frame, VideoTransform.Identity);
        Assert.That(result.IsSuccessful, Is.True, result.Message);

        return target.ReadPixels(_device);
    }

    private static RawVideoFrame CreateFrame(PixelFormat format, IMediaBuffer buffer)
    {
        return new RawVideoFrame
        {
            Timestamp = TimeSpan.Zero,
            Duration = TimeSpan.FromMilliseconds(40),
            Width = Width,
            Height = Height,
            Format = format,
            Buffer = buffer,
        };
    }

    private static void AssertPixel(byte[] pixels, int index, int red, int green, int blue, int tolerance = 2)
    {
        int offset = index * 4;
        using (Assert.EnterMultipleScope())
        {
            // The target is BGRA, so the bytes are blue, green, red, alpha.
            Assert.That(pixels[offset], Is.EqualTo(blue).Within(tolerance), "blue");
            Assert.That(pixels[offset + 1], Is.EqualTo(green).Within(tolerance), "green");
            Assert.That(pixels[offset + 2], Is.EqualTo(red).Within(tolerance), "red");
            Assert.That(pixels[offset + 3], Is.EqualTo(255), "alpha");
        }
    }

    /// <summary>
    /// A hardware frame the decoder will not keep alive is refused rather than drawn. The views name a slice of the
    /// decoder's own texture; without a retention the decoder is free to refill that slice while it is sampled, and a
    /// torn picture says nothing about why it happened. The frame already on the surface is left alone.
    /// </summary>
    [Test]
    public void RefusesAHardwareFrameItCannotKeepAlive()
    {
        using P010Texture texture = new(_device, Width, Height);
        using var target = D3D11RenderTarget.CreateOffscreen(_device, Width, Height);
        _renderer.SetOutput(target.View, Width, Height);

        using FakeGpuBuffer drawable = new(texture.Native);
        using (RawVideoFrame first = CreateGpuFrame(drawable))
        {
            Assert.That(_renderer.Render(first, VideoTransform.Identity).IsSuccessful, Is.True, "the frame that can be kept alive draws");
        }

        using FakeGpuBuffer unretainable = new(texture.Native) { CanRetain = false };
        using RawVideoFrame second = CreateGpuFrame(unretainable, width: Width + 2);
        OperationResult result = _renderer.Render(second, VideoTransform.Identity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccessful, Is.False);
            Assert.That(result.Message, Does.Contain("kept alive"));
            Assert.That(target.ReadPixels(_device), Has.Length.EqualTo(Width * Height * 4), "what was on the surface stays there");
        }
    }

    private static RawVideoFrame CreateGpuFrame(IGpuVideoBuffer buffer, int? width = null)
    {
        return new RawVideoFrame
        {
            Timestamp = TimeSpan.Zero,
            Duration = TimeSpan.FromMilliseconds(40),
            Width = width ?? Width,
            Height = Height,
            Format = PixelFormat.P010,
            Buffer = buffer,
            IsHardwareBacked = true,
        };
    }

    /// <summary>
    /// A P010 texture with two slices, the way a hardware decoder hands one over: the renderer views a slice of it,
    /// so the array has to be a real one for the view to be created.
    /// </summary>
    private sealed class P010Texture : IDisposable
    {
        private readonly ID3D11Texture2D _texture;
        private bool _disposed;

        internal P010Texture(D3D11Device device, int width, int height)
        {
            D3D11_TEXTURE2D_DESC description = new()
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 2,
                Format = DXGI_FORMAT.DXGI_FORMAT_P010,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            };

            _texture = D3D11Interop.CreateTexture2D(device.Device, description);

            // Asked for once: the renderer takes the pointer, marshals it and lets go of what it marshalled, so
            // asking again for the same texture would be asking a wrapper whose reference has already been given back.
            Native = Marshal.GetIUnknownForObject(_texture);
        }

        internal nint Native { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Marshal.Release(Native);
            D3D11Interop.Release(_texture);
        }
    }

    /// <summary>Stands in for the buffer a hardware decoder hands over: a texture and the slice the frame lives in.</summary>
    private sealed class FakeGpuBuffer(nint texture) : IGpuVideoBuffer
    {
        /// <summary>Whether this stand-in can be kept alive, which a real decoder's buffer cannot always promise.</summary>
        internal bool CanRetain { get; init; } = true;

        public long Length => 0;

        public nint NativeTexture => texture;

        public int SubresourceIndex => 1;

        public IGpuVideoBuffer? Retain()
        {
            return CanRetain ? this : null;
        }

        public void Dispose()
        {
        }
    }
    /// <summary>Planes in unmanaged memory, each with its own row padding.</summary>
    private sealed class PlanarFrameBuffer : IPlanarVideoBuffer
    {
        private readonly nint[] _planes;
        private readonly int[] _strides;

        internal PlanarFrameBuffer(params (int Width, int Height, int BytesPerTexel, Func<int, int, byte[]> Texel)[] planes)
        {
            _planes = new nint[planes.Length];
            _strides = new int[planes.Length];

            for (int index = 0; index < planes.Length; index++)
            {
                (int width, int height, int bytesPerTexel, Func<int, int, byte[]> texel) = planes[index];
                int stride = (width * bytesPerTexel) + 8;
                _strides[index] = stride;
                _planes[index] = Marshal.AllocHGlobal(stride * height);

                byte[] row = new byte[stride];
                for (int y = 0; y < height; y++)
                {
                    Array.Clear(row);
                    for (int x = 0; x < width; x++)
                    {
                        byte[] value = texel(x, y);
                        Array.Copy(value, 0, row, x * bytesPerTexel, bytesPerTexel);
                    }

                    Marshal.Copy(row, 0, _planes[index] + (y * stride), stride);
                }
            }
        }

        public int PlaneCount => _planes.Length;

        public long Length => 0;

        public nint GetPlanePointer(int plane)
        {
            return plane >= 0 && plane < _planes.Length ? _planes[plane] : 0;
        }

        public int GetPlaneStride(int plane)
        {
            return plane >= 0 && plane < _strides.Length ? _strides[plane] : 0;
        }

        public void Dispose()
        {
            for (int index = 0; index < _planes.Length; index++)
            {
                nint memory = _planes[index];
                if (memory != 0)
                {
                    _planes[index] = 0;
                    Marshal.FreeHGlobal(memory);
                }
            }
        }
    }
}