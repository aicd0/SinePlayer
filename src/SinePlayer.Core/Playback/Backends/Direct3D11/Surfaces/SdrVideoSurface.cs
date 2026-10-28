// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;

using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;

namespace SinePlayer.Core.Playback.Backends.Direct3D11;

/// <summary>
/// The container every display understands: eight-bit BGRA in the colour space DXGI creates a chain with, which is
/// gamma-encoded BT.709 relative to the display's white. Nothing is declared on the chain because nothing else is
/// meant; an HDR picture is converted before it arrives here, which is the whole of what tone mapping does.
/// </summary>
internal sealed class SdrVideoSurface : SwapChainVideoSurface
{
    private SdrVideoSurface(D3D11SwapChain swapChain)
        : base(swapChain)
    {
    }

    public override VideoSurfaceKind Kind => VideoSurfaceKind.Sdr;

    internal static OperationResult<SdrVideoSurface> TryCreate(D3D11Device device, int width, int height, double compositionScale, DXGI_ALPHA_MODE alphaMode)
    {
        OperationResultCreator<SdrVideoSurface> result = new();

        OperationResult<D3D11SwapChain> created = D3D11SwapChain.TryCreate(
            device,
            width,
            height,
            compositionScale,
            alphaMode,
            DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM);

        if (!created.IsSuccessful)
        {
            return result.Failure(created);
        }

        return result.Success(new SdrVideoSurface(created.Result));
    }
}
