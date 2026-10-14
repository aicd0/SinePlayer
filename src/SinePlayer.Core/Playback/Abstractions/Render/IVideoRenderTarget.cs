// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;

namespace SinePlayer.Core.Playback.Abstractions.Render;

/// <summary>
/// The surface the host application presents into. The engine never creates a UI element: it builds a
/// swap chain and hands the handle over, so video renderers stay independent of the app framework and
/// one can replace another without the page noticing.
/// </summary>
public interface IVideoRenderTarget
{
    int SurfaceWidth { get; }

    int SurfaceHeight { get; }

    /// <summary>Physical pixels per logical pixel, needed to size the swap chain correctly.</summary>
    double RasterizationScale { get; }

    event Action? SurfaceSizeChanged;

    /// <summary>
    /// Binds a native swap chain to this target. The implementation decides what that means for its UI
    /// element, and takes its own reference to the swap chain.
    /// </summary>
    void AttachPresentationSurface(nint swapChain);
}