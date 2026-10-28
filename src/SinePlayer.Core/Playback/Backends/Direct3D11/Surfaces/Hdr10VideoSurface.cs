// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;

using Windows.Win32.Graphics.Dxgi.Common;

namespace SinePlayer.Core.Playback.Backends.Direct3D11.Surfaces;

/// <summary>
/// The container that can carry a high-dynamic-range picture as it was graded: ten-bit RGB holding PQ codes in
/// BT.2020, which are absolute light levels rather than values relative to white. Everything within the display's
/// reach is then shown at the light level the grade names, and nothing has to be compressed on the way.
/// <para>
/// The format alone would say nothing — eight-bit PQ is possible in the abstract and useless in practice — so the
/// colour space is declared too, and both halves have to be accepted before this surface exists. A chain that refuses
/// either is disposed rather than used, because a PQ picture in a chain that still claims BT.709 gamma would be shown
/// as a dim, flat version of itself with nothing to say why.
/// </para>
/// </summary>
internal sealed class Hdr10VideoSurface : SwapChainVideoSurface
{
    /// <summary>PQ, BT.2020, full range: what an HDR10 display is composed for.</summary>
    internal const DXGI_COLOR_SPACE_TYPE ColourSpace = DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;

    private Hdr10VideoSurface(D3D11SwapChain swapChain)
        : base(swapChain)
    {
    }

    public override VideoSurfaceKind Kind => VideoSurfaceKind.Hdr10;

    internal static OperationResult<Hdr10VideoSurface> TryCreate(D3D11Device device, int width, int height, double compositionScale, DXGI_ALPHA_MODE alphaMode)
    {
        OperationResultCreator<Hdr10VideoSurface> result = new();

        OperationResult<D3D11SwapChain> created = D3D11SwapChain.TryCreate(
            device,
            width,
            height,
            compositionScale,
            alphaMode,
            DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM);

        if (!created.IsSuccessful)
        {
            return result.Failure(created);
        }

        D3D11SwapChain swapChain = created.Result;
        OperationResult declared = swapChain.TrySetColourSpace(ColourSpace);
        if (!declared.IsSuccessful)
        {
            swapChain.Dispose();
            return result.Failure(declared);
        }

        return result.Success(new Hdr10VideoSurface(swapChain));
    }
}
