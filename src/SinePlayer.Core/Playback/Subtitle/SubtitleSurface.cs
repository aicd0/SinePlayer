// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Microsoft.Graphics.Canvas;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Core.Playback.Subtitle;

internal sealed partial class SubtitleSurface : IDisposable
{
    private bool _disposed;
    private volatile int _preparedWidth;
    private volatile int _preparedHeight;
    private IVideoRenderTarget? _target;
    private SubtitleCompositor? _compositor;

    internal int Width => _preparedWidth;

    internal int Height => _preparedHeight;

    internal double CompositionScale => _compositor?.CompositionScale ?? 1.0;

    internal CanvasDevice? CanvasDevice
    {
        get
        {
            SubtitleCompositor? compositor = _compositor;
            return compositor?.EnsureCanvasDevice();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        SubtitleCompositor? compositor = _compositor;
        _compositor = null;
        _target = null;

        if (compositor is not null)
        {
            compositor.RemoveLayer(this);
            compositor.Release();
        }
    }

    internal void BindTarget(IVideoRenderTarget target)
    {
        if (_target == target)
        {
            return;
        }

        if (_compositor is { } previous)
        {
            previous.RemoveLayer(this);
            previous.Release();
            _compositor = null;
        }

        _target = target;
        var compositor = SubtitleCompositor.Acquire(target);
        _compositor = compositor;
        compositor.AddLayer(this);
    }

    internal OperationResult EnsureSurface()
    {
        SubtitleCompositor? compositor = _compositor;
        if (compositor is null)
        {
            return OperationResult.Failure("The subtitle renderer has no surface to draw into.");
        }

        // The bitmap, its size and its lifetime are the compositor's: it is the one that composes every layer of this
        // target, so it is the one that may let go of one without a frame on another thread still drawing into it.
        OperationResult prepared = compositor.Prepare(this, out int width, out int height);
        if (prepared.IsSuccessful)
        {
            _preparedWidth = width;
            _preparedHeight = height;
        }

        return prepared;
    }

    internal void SetOrder(int order)
    {
        _compositor?.SetLayerOrder(this, order);
    }

    internal OperationResult DrawAndPresent(Action<CanvasDrawingSession, int, int> draw)
    {
        SubtitleCompositor? compositor = _compositor;
        if (compositor is null)
        {
            return OperationResult.Failure("The subtitle surface has no target to present into.");
        }

        return compositor.Render(this, draw);
    }
}
