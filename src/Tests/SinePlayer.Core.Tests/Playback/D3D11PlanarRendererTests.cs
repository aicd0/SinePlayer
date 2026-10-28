// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Backends.Direct3D11;
using SinePlayer.Core.Playback.Backends.Direct3D11.Surfaces;

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
        OperationResult<D3D11Device> deviceCreated = D3D11Device.Create();
        Assert.That(deviceCreated.IsSuccessful, Is.True, deviceCreated.Message);
        _device = deviceCreated.Result;

        OperationResult<D3D11VideoRenderer> rendererCreated = D3D11VideoRenderer.Create(_device, VideoSurfacePreference.Auto);
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

    /// <summary>
    /// 4:2:2 keeps every row of chroma and halves only its width. It is the same three channels as 4:2:0, so the
    /// planar shader draws it: only the sizes of the textures the renderer allocates differ.
    /// </summary>
    [Test]
    public void ConvertsPlanarYuv422ToWhite()
    {
        using PlanarFrameBuffer buffer = new(
            (Width, Height, 1, (_, _) => [0xEB]),
            ((Width + 1) / 2, Height, 1, (_, _) => [0x80]),
            ((Width + 1) / 2, Height, 1, (_, _) => [0x80]));

        byte[] pixels = Render(PixelFormat.Yuv422P, buffer);

        AssertPixel(pixels, 0, 255, 255, 255);
        AssertPixel(pixels, (Width * Height) - 1, 255, 255, 255);
    }

    [Test]
    public void ConvertsPlanarYuv422ToRed()
    {
        // Every row carries chroma here, so a colour that varies down the picture is told apart from 4:2:0.
        using PlanarFrameBuffer buffer = new(
            (Width, Height, 1, (_, _) => [0x51]),
            ((Width + 1) / 2, Height, 1, (_, _) => [0x5A]),
            ((Width + 1) / 2, Height, 1, (_, _) => [0xF0]));

        byte[] pixels = Render(PixelFormat.Yuv422P, buffer);

        AssertPixel(pixels, (Width * Height) - 1, 255, 0, 0);
    }

    /// <summary>
    /// 4:4:4 keeps chroma whole, so a picture whose colour changes every pixel down to the last column is the case
    /// a half-width chroma plane would get wrong.
    /// </summary>
    [Test]
    public void ConvertsPlanarYuv444ToWhite()
    {
        using PlanarFrameBuffer buffer = new(
            (Width, Height, 1, (_, _) => [0xEB]),
            (Width, Height, 1, (_, _) => [0x80]),
            (Width, Height, 1, (_, _) => [0x80]));

        byte[] pixels = Render(PixelFormat.Yuv444P, buffer);

        AssertPixel(pixels, 0, 255, 255, 255);
        AssertPixel(pixels, (Width * Height) - 1, 255, 255, 255);
    }

    [Test]
    public void ConvertsPlanarYuv444ToRed()
    {
        using PlanarFrameBuffer buffer = new(
            (Width, Height, 1, (_, _) => [0x51]),
            (Width, Height, 1, (_, _) => [0x5A]),
            (Width, Height, 1, (_, _) => [0xF0]));

        byte[] pixels = Render(PixelFormat.Yuv444P, buffer);

        AssertPixel(pixels, (Width * Height) - 1, 255, 0, 0);
    }

    /// <summary>
    /// A packed frame carries its channels in the order the texture format names, and the packed shader hands them
    /// on unchanged — so a red-first buffer viewed as a blue-first texture would show its reds and blues exchanged.
    /// </summary>
    [Test]
    public void KeepsPackedChannelOrder()
    {
        // Red, green, blue, opaque, in the byte order the layout names.
        using PlanarFrameBuffer red = new((Width, Height, 4, (_, _) => [0xFF, 0x00, 0x00, 0xFF]));
        using PlanarFrameBuffer green = new((Width, Height, 4, (_, _) => [0x00, 0xFF, 0x00, 0xFF]));
        using PlanarFrameBuffer blue = new((Width, Height, 4, (_, _) => [0x00, 0x00, 0xFF, 0xFF]));

        AssertPixel(Render(PixelFormat.Rgba8, red), 0, 255, 0, 0);
        AssertPixel(Render(PixelFormat.Rgba8, green), 0, 0, 255, 0);
        AssertPixel(Render(PixelFormat.Rgba8, blue), 0, 0, 0, 255);

        // The blue-first layout is the same bytes read from the other end, which is the case that has to stay right.
        using PlanarFrameBuffer bgra = new((Width, Height, 4, (_, _) => [0xFF, 0x00, 0x00, 0xFF]));
        AssertPixel(Render(PixelFormat.Bgra8, bgra), 0, 0, 0, 255);
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
    /// Every layout the renderer claims a decoder may keep on the device is one it can actually draw from. The claim
    /// and the casting are two readings of the same table, and this is what keeps them one: a layout added to the claim
    /// without a texture format behind it would pass the pre-check and then be refused on every frame that arrived,
    /// which looks like a decoder fault rather than this one.
    /// </summary>
    [TestCase(PixelFormat.Nv12, DXGI_FORMAT.DXGI_FORMAT_NV12)]
    [TestCase(PixelFormat.P010, DXGI_FORMAT.DXGI_FORMAT_P010)]
    [TestCase(PixelFormat.P016, DXGI_FORMAT.DXGI_FORMAT_P016)]
    public void DrawsEveryLayoutItClaimsItCanDraw(PixelFormat layout, DXGI_FORMAT textureFormat)
    {
        Assert.That(_renderer.CanDisplayGpuFrame(layout).IsSuccessful, Is.True, "the layout is claimed");

        using GpuTexture texture = new(_device, textureFormat, Width, Height);

        using FakeGpuBuffer gpuBuffer = new(texture.Native);
        using RawVideoFrame frame = CreateGpuFrame(gpuBuffer, layout);

        using var target = D3D11RenderTarget.CreateOffscreen(_device, Width, Height);
        _renderer.SetOutput(target.View, Width, Height);

        OperationResult result = _renderer.Render(frame, VideoTransform.Identity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccessful, Is.True, $"the claim has to be one the draw can keep: {result.Message}");
            Assert.That(target.ReadPixels(_device), Has.Length.EqualTo(Width * Height * 4), "the picture was drawn into the whole surface");
        }
    }

    /// <summary>
    /// The layout is the texture's own. A frame that misreports it — a decoder's twelve-bit stream reports P016 for
    /// a P010-shaped pool, and a decoder bug can report anything — is drawn from what the texture actually holds
    /// rather than from the description, which is the only description that cannot drift.
    /// </summary>
    [Test]
    public void DrawsFromTheTextureFormatRatherThanTheReportedOne()
    {
        using GpuTexture texture = new(_device, DXGI_FORMAT.DXGI_FORMAT_NV12, Width, Height);

        using FakeGpuBuffer gpuBuffer = new(texture.Native);
        using RawVideoFrame frame = CreateGpuFrame(gpuBuffer, PixelFormat.P010);

        using var target = D3D11RenderTarget.CreateOffscreen(_device, Width, Height);
        _renderer.SetOutput(target.View, Width, Height);

        OperationResult result = _renderer.Render(frame, VideoTransform.Identity);

        Assert.That(result.IsSuccessful, Is.True, result.Message);
    }

    /// <summary>
    /// A texture whose layout DXGI defines no plane views for is refused, and the refusal names the format that
    /// arrived rather than the one the frame claimed.
    /// </summary>
    [Test]
    public void RefusesATextureItCannotCast()
    {
        using GpuTexture texture = new(_device, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, Width, Height);

        using FakeGpuBuffer gpuBuffer = new(texture.Native);
        using RawVideoFrame frame = CreateGpuFrame(gpuBuffer);

        using var target = D3D11RenderTarget.CreateOffscreen(_device, Width, Height);
        _renderer.SetOutput(target.View, Width, Height);

        OperationResult result = _renderer.Render(frame, VideoTransform.Identity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccessful, Is.False);
            Assert.That(result.Message, Does.Contain("B8G8R8A8_UNORM"));
        }
    }

    /// <summary>
    /// The layouts a decoder is allowed to keep on the device are the ones the draw can sample. Asked before a frame
    /// exists, which is why it is about a layout and not about a texture.
    /// </summary>
    [Test]
    public void AnswersWhichLayoutsItCanDrawFromTheDevice()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_renderer.CanDisplayGpuFrame(PixelFormat.Nv12).IsSuccessful, Is.True);
            Assert.That(_renderer.CanDisplayGpuFrame(PixelFormat.P010).IsSuccessful, Is.True);
            Assert.That(_renderer.CanDisplayGpuFrame(PixelFormat.P016).IsSuccessful, Is.True);
            Assert.That(_renderer.CanDisplayGpuFrame(PixelFormat.Yuv422P).IsSuccessful, Is.False);
            Assert.That(_renderer.CanDisplayGpuFrame(PixelFormat.Yuv444P).IsSuccessful, Is.False);
            Assert.That(_renderer.CanDisplayGpuFrame(PixelFormat.Unknown).IsSuccessful, Is.False);
        }
    }

    /// <summary>
    /// A ten-bit frame the CPU decoded is three planar components sixteen bits wide, with the samples right-aligned —
    /// 0 to 1023 held in sixteen bits, which is what a software decoder writes. The eight-bit code times four is the
    /// ten-bit one, so 235 becomes 940 and 128 becomes 512.
    /// </summary>
    [Test]
    public void ConvertsPlanarTenBitToWhite()
    {
        using PlanarFrameBuffer buffer = new(
            (Width, Height, 2, (_, _) => TenBit(940)),
            ((Width + 1) / 2, (Height + 1) / 2, 2, (_, _) => TenBit(512)),
            ((Width + 1) / 2, (Height + 1) / 2, 2, (_, _) => TenBit(512)));

        byte[] pixels = Render(PixelFormat.Yuv420P10, buffer);

        AssertPixel(pixels, 0, 255, 255, 255, 3);
        AssertPixel(pixels, (Width * Height) - 1, 255, 255, 255, 3);
    }

    [Test]
    public void ConvertsPlanarTenBitToRed()
    {
        using PlanarFrameBuffer buffer = new(
            (Width, Height, 2, (_, _) => TenBit(324)),
            ((Width + 1) / 2, (Height + 1) / 2, 2, (_, _) => TenBit(360)),
            ((Width + 1) / 2, (Height + 1) / 2, 2, (_, _) => TenBit(960)));

        byte[] pixels = Render(PixelFormat.Yuv420P10, buffer);

        AssertPixel(pixels, 0, 255, 0, 0, 3);
    }

    [Test]
    public void ConvertsPlanarTenBitToBlack()
    {
        using PlanarFrameBuffer buffer = new(
            (Width, Height, 2, (_, _) => TenBit(64)),
            ((Width + 1) / 2, (Height + 1) / 2, 2, (_, _) => TenBit(512)),
            ((Width + 1) / 2, (Height + 1) / 2, 2, (_, _) => TenBit(512)));

        byte[] pixels = Render(PixelFormat.Yuv420P10, buffer);

        AssertPixel(pixels, 0, 0, 0, 0, 3);
    }

    /// <summary>
    /// A PQ frame holds light levels, not code values: the sample below is a hundred nits, the diffuse white an HDR
    /// grade is mastered against. Read as SDR it lands in the middle of the scale, which is what made HDR look dim;
    /// read as the light it stands for it lands near the top of it, which is where diffuse white belongs.
    /// </summary>
    [Test]
    public void ReadsPqAsTheLightItStandsFor()
    {
        byte[] readAsSdr = Render(PixelFormat.Yuv420P10, CreateTenBitGrey(509));
        byte[] toneMapped = Render(PixelFormat.Yuv420P10, CreateTenBitGrey(509), HighDynamicRange(ColourTransfer.Smpte2084));

        AssertPixel(readAsSdr, 0, 129, 129, 129, 4);
        AssertPixel(toneMapped, 0, 195, 195, 195, 5);
    }

    /// <summary>
    /// HLG is backwards compatible with SDR, so its mid-tones are close to SDR's and the difference shows as a brighter
    /// picture rather than a clipped one: half signal is fifty nits against a thousand-nit white, and it has to land
    /// well above the same bytes read as SDR — 128 — without reaching white.
    /// </summary>
    [Test]
    public void ReadsHlgAsTheLightItStandsFor()
    {
        byte[] readAsSdr = Render(PixelFormat.Yuv420P10, CreateTenBitGrey(504));
        byte[] toneMapped = Render(PixelFormat.Yuv420P10, CreateTenBitGrey(504), HighDynamicRange(ColourTransfer.Hlg));

        AssertPixel(readAsSdr, 0, 129, 129, 129, 4);
        AssertPixel(toneMapped, 0, 164, 164, 164, 6);
    }

    /// <summary>
    /// A frame that says it is HDR comes out as the programme for that transfer says, and one for a layout with no such
    /// programme is drawn the way it always was: a stream that carries a transfer this build cannot name, or a depth
    /// it has no HDR programme for, is shown rather than refused.
    /// </summary>
    [Test]
    public void FallsBackToThePlainProgrammeWhenThereIsNoHdrOne()
    {
        using PlanarFrameBuffer buffer = new(
            (Width, Height, 1, (_, _) => [235]),
            ((Width + 1) / 2, (Height + 1) / 2, 1, (_, _) => [128]),
            ((Width + 1) / 2, (Height + 1) / 2, 1, (_, _) => [128]));

        byte[] pixels = Render(PixelFormat.Yuv420P, buffer, HighDynamicRange(ColourTransfer.Smpte2084));

        AssertPixel(pixels, 0, 255, 255, 255, 3);
    }

    /// <summary>
    /// PQ content into an HDR container is handed over as the light it is: the sample that names a hundred nits comes out
    /// as the same PQ code, and nothing between the file and the surface may reinterpret it. This is the picture the whole
    /// container exists for — the code is not re-encoded, only expanded from the limited range it is delivered in.
    /// </summary>
    [Test]
    public void PassesPqThroughIntoAnHdrContainer()
    {
        (int Red, int Green, int Blue) pixel = RenderIntoTenBit(CreateTenBitGrey(509), HighDynamicRange(ColourTransfer.Smpte2084));

        // 509 is a limited-range PQ code; expanded to the full range a ten-bit surface holds it is 517. The channels differ
        // slightly because 512 is the neutral chroma code in ten bits and lands a shade below it in sixteen-bit storage.
        Assert.That(pixel.Red, Is.EqualTo(514).Within(2), "red");
        Assert.That(pixel.Green, Is.EqualTo(519).Within(2), "green");
        Assert.That(pixel.Blue, Is.EqualTo(513).Within(2), "blue");
    }

    /// <summary>
    /// SDR content in an HDR container is lifted rather than left alone: white is placed at the reference level an HDR grade
    /// is mastered against — 203 nits — and that light is what the code names. Read as if the surface were SDR it would be
    /// near black, which is why the lift cannot be skipped.
    /// </summary>
    [Test]
    public void LiftsSdrIntoAnHdrContainer()
    {
        using PlanarFrameBuffer buffer = new(
            (Width, Height, 1, (_, _) => [235]),
            ((Width + 1) / 2, (Height + 1) / 2, 1, (_, _) => [128]),
            ((Width + 1) / 2, (Height + 1) / 2, 1, (_, _) => [128]));

        (int Red, int Green, int Blue) pixel = RenderIntoTenBit(buffer, ColourInfo.Unknown);

        // White at 203 nits is the PQ code 0.5793, which a ten-bit full-range surface holds as 593.
        Assert.That(pixel.Red, Is.EqualTo(593).Within(3), "red");
        Assert.That(pixel.Green, Is.EqualTo(593).Within(3), "green");
        Assert.That(pixel.Blue, Is.EqualTo(593).Within(3), "blue");
    }

    /// <summary>
    /// Draws a frame into a ten-bit ten-bit surface, which is what an HDR container is, and reads one pixel back as its
    /// three ten-bit channels. The container is declared rather than created because a swap chain needs no element for the
    /// picture itself — only for presenting it, which is what this does not do.
    /// </summary>
    private (int Red, int Green, int Blue) RenderIntoTenBit(PlanarFrameBuffer buffer, ColourInfo colour)
    {
        _renderer.SurfaceKind = VideoSurfaceKind.Hdr10;

        using RawVideoFrame frame = CreateFrame(PixelFormat.Yuv420P10, buffer, colour);
        using var target = D3D11RenderTarget.CreateOffscreen(_device, Width, Height, DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM);

        _renderer.SetOutput(target.View, Width, Height);
        OperationResult result = _renderer.Render(frame, VideoTransform.Identity);
        Assert.That(result.IsSuccessful, Is.True, result.Message);

        byte[] pixels = target.ReadPixels(_device, DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM);
        uint packed = BitConverter.ToUInt32(pixels, 0);
        return ((int)(packed & 0x3FF), (int)((packed >> 10) & 0x3FF), (int)((packed >> 20) & 0x3FF));
    }

    /// <summary>
    /// A ten-bit 4:2:0 frame of one flat grey. Each call is a fresh buffer, because a frame owns its buffer and
    /// disposes it once that frame is gone, so the same one cannot be drawn twice.
    /// </summary>
    private static PlanarFrameBuffer CreateTenBitGrey(int luma)
    {
        return new PlanarFrameBuffer(
            (Width, Height, 2, (_, _) => TenBit(luma)),
            ((Width + 1) / 2, (Height + 1) / 2, 2, (_, _) => TenBit(512)),
            ((Width + 1) / 2, (Height + 1) / 2, 2, (_, _) => TenBit(512)));
    }

    private static ColourInfo HighDynamicRange(ColourTransfer transfer)
    {
        return new ColourInfo(ColourPrimaries.Bt2020, transfer, ColourRange.Limited);
    }

    /// <summary>
    /// A ten-bit sample as a software decoder stores it: the code in the low bits of a little-endian sixteen-bit value,
    /// which is the opposite of the P010 texture a hardware decoder writes, where the same code is left-aligned.
    /// </summary>
    private static byte[] TenBit(int value)
    {
        return [(byte)value, (byte)(value >> 8)];
    }

    private byte[] Render(PixelFormat format, PlanarFrameBuffer buffer)
    {
        return Render(format, buffer, ColourInfo.Unknown);
    }

    private byte[] Render(PixelFormat format, PlanarFrameBuffer buffer, ColourInfo colour)
    {
        using RawVideoFrame frame = CreateFrame(format, buffer, colour);
        using var target = D3D11RenderTarget.CreateOffscreen(_device, Width, Height);

        _renderer.SetOutput(target.View, Width, Height);
        OperationResult result = _renderer.Render(frame, VideoTransform.Identity);
        Assert.That(result.IsSuccessful, Is.True, result.Message);

        return target.ReadPixels(_device);
    }

    private static RawVideoFrame CreateFrame(PixelFormat format, IMediaBuffer buffer)
    {
        return CreateFrame(format, buffer, ColourInfo.Unknown);
    }

    private static RawVideoFrame CreateFrame(PixelFormat format, IMediaBuffer buffer, ColourInfo colour)
    {
        return new RawVideoFrame
        {
            Timestamp = TimeSpan.Zero,
            Duration = TimeSpan.FromMilliseconds(40),
            Width = Width,
            Height = Height,
            Format = format,
            Colour = colour,
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
        using GpuTexture texture = new(_device, DXGI_FORMAT.DXGI_FORMAT_P010, Width, Height);
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

    private static RawVideoFrame CreateGpuFrame(IGpuVideoBuffer buffer, PixelFormat format = PixelFormat.P010, int? width = null)
    {
        return new RawVideoFrame
        {
            Timestamp = TimeSpan.Zero,
            Duration = TimeSpan.FromMilliseconds(40),
            Width = width ?? Width,
            Height = Height,
            Format = format,
            Buffer = buffer,
            IsHardwareBacked = true,
        };
    }

    /// <summary>
    /// A texture with two slices, the way a hardware decoder hands one over: the renderer views a slice of it,
    /// so the array has to be a real one for the view to be created.
    /// </summary>
    private sealed class GpuTexture : IDisposable
    {
        private readonly ID3D11Texture2D _texture;
        private bool _disposed;

        internal GpuTexture(D3D11Device device, DXGI_FORMAT format, int width, int height)
        {
            D3D11_TEXTURE2D_DESC description = new()
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 2,
                Format = format,
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