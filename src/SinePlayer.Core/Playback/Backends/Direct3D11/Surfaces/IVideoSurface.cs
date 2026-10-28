// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;

using Windows.Win32.Graphics.Direct3D11;

namespace SinePlayer.Core.Playback.Backends.Direct3D11.Surfaces;

/// <summary>
/// A surface the renderer draws into and hands to the compositor, together with what its pixels mean. Two containers
/// exist because two are all a display can be: an SDR one and a PQ one. Everything a container does that is not about
/// meaning — composition, scaling, resizing, presenting — is the same in both and lives in
/// <see cref="D3D11SwapChain"/>; each implementation here is only the difference.
/// </summary>
internal interface IVideoSurface : IDisposable
{
    VideoSurfaceKind Kind { get; }

    int Width { get; }

    int Height { get; }

    ID3D11RenderTargetView View { get; }

    /// <summary>Which transform this container needs for a picture of the given colour.</summary>
    VideoSurfaceTransform SelectTransform(ColourInfo content);

    OperationResult AttachTo(IVideoRenderTarget target);

    OperationResult Resize(int width, int height, double compositionScale);

    OperationResult Present();

    /// <summary>The scale the chain applies when the host composes it, so a test can prove it cancels the display's.</summary>
    bool TryGetCompositionTransform(out double scaleX, out double scaleY);

    /// <summary>Reads the presented buffer back, for capture and for the tests that pin what a container writes.</summary>
    byte[] ReadPixels();
}
