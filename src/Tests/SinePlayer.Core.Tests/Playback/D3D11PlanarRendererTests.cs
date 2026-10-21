// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Backends.Direct3D11;

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

        PlaybackOperationResult result = _renderer.Render(frame);

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
        Task<PlaybackOperationResult> draw;
        bool blocked;
        Monitor.Enter(deviceLock);
        try
        {
            draw = Task.Run(() => _renderer.Render(frame));
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

    private byte[] Render(PixelFormat format, PlanarFrameBuffer buffer)
    {
        using RawVideoFrame frame = CreateFrame(format, buffer);
        using var target = D3D11RenderTarget.CreateOffscreen(_device, Width, Height);

        _renderer.SetOutput(target.View, Width, Height);
        PlaybackOperationResult result = _renderer.Render(frame);
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

    private static void AssertPixel(byte[] pixels, int index, int red, int green, int blue)
    {
        int offset = index * 4;
        using (Assert.EnterMultipleScope())
        {
            // The target is BGRA, so the bytes are blue, green, red, alpha.
            Assert.That(pixels[offset], Is.EqualTo(blue).Within(2), "blue");
            Assert.That(pixels[offset + 1], Is.EqualTo(green).Within(2), "green");
            Assert.That(pixels[offset + 2], Is.EqualTo(red).Within(2), "red");
            Assert.That(pixels[offset + 3], Is.EqualTo(255), "alpha");
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