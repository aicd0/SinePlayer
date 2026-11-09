// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Microsoft.Graphics.Canvas;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Core.Playback.Backends.Win2D;

public sealed partial class Win2DSubtitleRenderer : ISubtitleRenderer
{
    private bool _disposed;

    /// <summary>Written when the renderer is configured, read on the render thread: a swap of one reference, so volatile.</summary>
    private volatile Win2DSubtitleLayer? _layer;

    private Win2DSubtitleLayer? _offscreen;

    public string Name => "Win2D";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_layer is { } layer)
        {
            _layer = null;
            Win2DSubtitleLayer.Leave(layer, this);
        }

        _offscreen?.Dispose();
        _offscreen = null;
    }

    public OperationResult Configure(SubtitleStreamInfo stream, IReadOnlyList<MediaAttachment> attachments, IVideoRenderTarget target)
    {
        if (_disposed)
        {
            return OperationResult.Failure("The subtitle renderer has already been disposed.");
        }

        // A chain may be pointed at another target, so the layer follows the target rather than being fixed once: leaving
        // the old one and joining the new one is the whole of "shares the drawing of the panel it is drawn on".
        if (_layer is { } previous)
        {
            _layer = null;
            Win2DSubtitleLayer.Leave(previous, this);
        }

        var layer = Win2DSubtitleLayer.Join(target, this);
        _layer = layer;
        return layer.EnsureSurface();
    }

    public OperationResult Show(IReadOnlyList<SubtitleCue> cues, TimeSpan position, SubtitlePresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(cues);

        if (_disposed)
        {
            return OperationResult.Failure("The subtitle renderer has already been disposed.");
        }

        Win2DSubtitleLayer? layer = _layer;
        if (layer is null)
        {
            return OperationResult.Failure("The subtitle renderer has no surface to draw into.");
        }

        layer.SetCues(this, cues, presentation);
        return layer.Draw();
    }

    /// <summary>
    /// The device text is laid out with. A test that wants to check the drawing against an offscreen target uses this rather
    /// than a swap chain, which cannot be read back after it has presented.
    /// </summary>
    internal CanvasDevice EnsureCanvasDevice()
    {
        if (_layer is { } layer)
        {
            return layer.EnsureCanvasDevice();
        }

        // Nothing is bound, so all this device is used for is drawing that goes nowhere, which needs no shared layer.
        _offscreen ??= new Win2DSubtitleLayer();
        return _offscreen.EnsureCanvasDevice();
    }
}