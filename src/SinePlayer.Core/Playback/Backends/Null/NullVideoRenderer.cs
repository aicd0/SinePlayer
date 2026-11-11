// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Core.Playback.Backends.Null;

internal sealed partial class NullVideoRenderer : IVideoRenderer
{
    private long _renderedFrameCount;

    public string Name => "Null";

    /// <summary>Nothing to draw with: a null renderer presents nothing, so no decoder needs to decode onto it.</summary>
    public IGpuDevice? GpuDevice => null;

    /// <summary>How many frames this renderer has been handed since it was created.</summary>
    public long RenderedFrameCount => Interlocked.Read(ref _renderedFrameCount);

    public void Dispose()
    {
    }

    public OperationResult Configure(VideoStreamInfo stream, IVideoRenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return OperationResult.Success();
    }

    public bool TryAcquireFrameSlot()
    {
        return true;
    }

    public OperationResult Render(RawVideoFrame frame, VideoTransform transform)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Interlocked.Increment(ref _renderedFrameCount);
        return OperationResult.Success();
    }

    public OperationResult RefreshSurface(VideoTransform transform)
    {
        return OperationResult.Success();
    }

    public OperationResult ClearSurface()
    {
        return OperationResult.Success();
    }
}
