// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;

using Windows.Win32.Graphics.Direct3D11;

namespace SinePlayer.Core.Playback.Backends.Direct3D11.Surfaces;

/// <summary>
/// A surface backed by a composition swap chain. Composition, scaling, resizing, presenting and reading back are the
/// same whatever the buffers hold, so they are written once here; a container is the two things that are not: the
/// format the chain is created with, and what the codes in it mean.
/// </summary>
internal abstract class SwapChainVideoSurface : IVideoSurface
{
    private readonly D3D11SwapChain _swapChain;

    protected SwapChainVideoSurface(D3D11SwapChain swapChain)
    {
        _swapChain = swapChain;
    }

    public abstract VideoSurfaceKind Kind { get; }

    public int Width => _swapChain.Width;

    public int Height => _swapChain.Height;

    public ID3D11RenderTargetView View => _swapChain.View;

    public VideoSurfaceTransform SelectTransform(ColourInfo content)
    {
        return VideoSurfaceTransforms.Select(Kind, content);
    }

    public OperationResult AttachTo(IVideoRenderTarget target)
    {
        return _swapChain.AttachTo(target);
    }

    public OperationResult Resize(int width, int height, double compositionScale)
    {
        return _swapChain.Resize(width, height, compositionScale);
    }

    public OperationResult Present()
    {
        return _swapChain.Present();
    }

    public bool TryGetDisplayedFrameCount(out long count)
    {
        return _swapChain.TryGetDisplayedFrameCount(out count);
    }

    public bool TryGetCompositionTransform(out double scaleX, out double scaleY)
    {
        return _swapChain.TryGetCompositionTransform(out scaleX, out scaleY);
    }

    public byte[] ReadPixels()
    {
        return _swapChain.ReadPixels();
    }

    public void Dispose()
    {
        _swapChain.Dispose();
    }
}
