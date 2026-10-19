// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

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
    [Test]
    public void CreatesHardwareDeviceAtAnAcceptableFeatureLevel()
    {
        bool created = D3D11Device.TryCreate(out D3D11Device? device, out string error);
        Assert.That(created, Is.True, error);

        using D3D11Device ownedDevice = device!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ownedDevice.Context, Is.Not.Null);
            Assert.That(ownedDevice.FeatureLevel, Is.GreaterThanOrEqualTo(D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0));
        }
    }

    [Test]
    public void CreatesTextureWithTheRequestedDescription()
    {
        bool created = D3D11Device.TryCreate(out D3D11Device? device, out string error);
        Assert.That(created, Is.True, error);

        using D3D11Device ownedDevice = device!;
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