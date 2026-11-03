// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;

using Windows.Win32.Graphics.Direct3D11;

namespace SinePlayer.Core.Playback.Backends.Direct3D11.Surfaces;

internal interface IVideoSurface : IDisposable
{
    VideoSurfaceKind Kind { get; }

    int Width { get; }

    int Height { get; }

    ID3D11RenderTargetView View { get; }

    VideoSurfaceTransform SelectTransform(ColourInfo content);

    OperationResult AttachTo(IVideoRenderTarget target);

    OperationResult Resize(int width, int height, double compositionScale);

    bool TryAcquireFrameSlot();

    OperationResult Present();

    bool TryGetCompositionTransform(out double scaleX, out double scaleY);

    byte[] ReadPixels();
}
