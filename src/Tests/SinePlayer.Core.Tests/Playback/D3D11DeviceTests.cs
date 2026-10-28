// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Backends.Direct3D11;

using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Verifies that the generated Direct3D 11 interop actually runs, not merely compiles: device
/// creation, the COM hand-off of the created interfaces, and resource creation on that device.
/// </summary>
[TestFixture]
public class D3D11DeviceTests
{
    /// <summary>
    /// What the backend renders on. A machine can show a window and have no accelerated 3D driver — Windows composes the
    /// desktop with Direct3D and falls back to a software rasteriser itself — so asking only for acceleration turned "no GPU
    /// driver" into "no picture at all". WARP is the same API in software, and it is always there.
    /// </summary>
    [Test]
    public void CreatesADeviceAtAnAcceptableFeatureLevel()
    {
        // The production path: hardware when there is any, WARP when there is not. Either way a device has to come back,
        // because that is the difference between "slow" and "no picture".
        OperationResult<D3D11Device> created = D3D11Device.Create();
        Assert.That(created.IsSuccessful, Is.True, created.Message);

        using D3D11Device ownedDevice = created.Result;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ownedDevice.Context, Is.Not.Null);
            Assert.That(ownedDevice.FeatureLevel, Is.GreaterThanOrEqualTo(D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0));
            Assert.That(ownedDevice.DriverType, Is.Not.EqualTo(D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN), "the caller can ask what it got");
            Assert.That(ownedDevice.NativeDevice, Is.Not.EqualTo(nint.Zero), "a decoder is handed this pointer");
        }
    }

    [Test]
    public void CreatesASoftwareDeviceThatKnowsItIsOne()
    {
        // Asked for explicitly, so the software path is proven on a machine whose acceleration is perfectly fine.
        OperationResult<D3D11Device> created = D3D11Device.Create(D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP);
        Assert.That(created.IsSuccessful, Is.True, created.Message);

        using D3D11Device ownedDevice = created.Result;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ownedDevice.DriverType, Is.EqualTo(D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP));
            Assert.That(ownedDevice.IsSoftware, Is.True, "a name has to be able to say so");
            Assert.That(ownedDevice.FeatureLevel, Is.GreaterThanOrEqualTo(D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0));
        }
    }

    [Test]
    public void NamesASoftwareRendererAsSoftware()
    {
        // The only place a player says what it renders on: the same picture on a machine without acceleration would
        // otherwise be indistinguishable.
        OperationResult<D3D11Device> created = D3D11Device.Create(D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP);
        Assert.That(created.IsSuccessful, Is.True, created.Message);

        using D3D11Device ownedDevice = created.Result;
        OperationResult<D3D11VideoRenderer> renderer = D3D11VideoRenderer.Create(ownedDevice, VideoSurfacePreference.Sdr);
        Assert.That(renderer.IsSuccessful, Is.True, renderer.Message);

        using D3D11VideoRenderer ownedRenderer = renderer.Result;
        Assert.That(ownedRenderer.Name, Does.Contain("software"));
    }

    [Test]
    public void NamesTheContainerOnceThereIsOne()
    {
        // The name is written where each fact is decided: the device at construction, the container when the surface is
        // made. So it has to gain its container, and keep its device, from one to the other.
        OperationResult<D3D11Device> created = D3D11Device.Create();
        Assert.That(created.IsSuccessful, Is.True, created.Message);

        using D3D11Device ownedDevice = created.Result;
        OperationResult<D3D11VideoRenderer> renderer = D3D11VideoRenderer.Create(ownedDevice, VideoSurfacePreference.Sdr);
        Assert.That(renderer.IsSuccessful, Is.True, renderer.Message);

        using D3D11VideoRenderer ownedRenderer = renderer.Result;
        Assert.That(ownedRenderer.Name, Does.Not.Contain("SDR"), "no surface has been made yet, so no container is known");

        OperationResult configured = ownedRenderer.Configure(Stream(), new SizeTarget(64, 64, 1.0));
        if (!configured.IsSuccessful)
        {
            Assert.Ignore($"This machine cannot make a presentation surface: {configured.Message}");
            return;
        }

        Assert.That(ownedRenderer.Name, Does.Contain("(SDR)"), "the container joins the name once there is one");
    }

    private static VideoStreamInfo Stream()
    {
        return new VideoStreamInfo
        {
            Index = 0,
            CodecId = "h264",
            Width = 64,
            Height = 64,
            FrameRate = 30.0,
            PixelFormat = PixelFormat.Nv12,
        };
    }

    private sealed class SizeTarget(int width, int height, double scale) : IVideoRenderTarget
    {
        public int SurfaceWidth => width;

        public int SurfaceHeight => height;

        public double RasterizationScale => scale;

        public event Action? SurfaceSizeChanged
        {
            add { }
            remove { }
        }

        public void AttachPresentationSurface(nint swapChain)
        {
        }
    }

    [Test]
    public void CreatesTextureWithTheRequestedDescription()
    {
        OperationResult<D3D11Device> created = D3D11Device.Create();
        Assert.That(created.IsSuccessful, Is.True, created.Message);

        using D3D11Device ownedDevice = created.Result;
        D3D11_TEXTURE2D_DESC description = new()
        {
            Width = 16,
            Height = 8,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
        };

        ID3D11Texture2D texture = D3D11Interop.CreateTexture2D(ownedDevice.Device, description);
        Assert.That(texture, Is.Not.Null);

        D3D11_TEXTURE2D_DESC actual = D3D11Interop.GetDescription(texture);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.Width, Is.EqualTo(16));
            Assert.That(actual.Height, Is.EqualTo(8));
            Assert.That(actual.Format, Is.EqualTo(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM));
            Assert.That(actual.MipLevels, Is.EqualTo(1));
        }
    }
}