// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Render;

/// <summary>
/// The surface the host application presents into. The engine never creates a UI element: it builds a
/// swap chain and hands the handle over, so video renderers stay independent of the app framework and
/// one can replace another without the page noticing.
/// <para>
/// The size is reported in <em>logical</em> units with the scale alongside it, because that is what a UI
/// framework gives its host: the buffer the engine allocates is <c>SurfaceWidth × RasterizationScale</c>
/// pixels wide, and the scale is cancelled on the swap chain itself so those pixels reach the display
/// one to one. A target that reports a scale without the engine cancelling it would have its content
/// scaled twice.
/// </para>
/// </summary>
public interface IVideoRenderTarget
{
    /// <summary>Width in logical units, as the host's element reports it.</summary>
    int SurfaceWidth { get; }

    /// <summary>Height in logical units, as the host's element reports it.</summary>
    int SurfaceHeight { get; }

    /// <summary>Physical pixels per logical unit, needed to size the swap chain correctly.</summary>
    double RasterizationScale { get; }

    event Action? SurfaceSizeChanged;

    /// <summary>
    /// Binds a native swap chain to this target. The implementation decides what that means for its UI
    /// element, and takes its own reference to the swap chain.
    /// </summary>
    void AttachPresentationSurface(nint swapChain);
}