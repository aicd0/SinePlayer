// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Core.Playback.Backends.Null;

/// <summary>
/// A renderer that presents nothing. Frames are counted and dropped, so the video chain still runs at the pace
/// the synchronizer sets — the pipeline's picture timing, starvation and end-of-stream logic all behave as they
/// would with a real renderer — while nothing needs a window, a panel or a graphics device.
/// <para>
/// <see cref="RenderedFrameCount"/> is what a test or the Lab watches to see that a switch happened: after a
/// replacement the new instance starts from zero and the old one stops counting.
/// </para>
/// </summary>
public sealed partial class NullVideoRenderer : IVideoRenderer
{
    private long _renderedFrameCount;

    public string Name => "Null";

    /// <summary>Nothing to draw with: a null renderer presents nothing, so no decoder needs to decode onto it.</summary>
    public IGpuDevice? GpuDevice => null;

    /// <summary>How many frames this renderer has been handed since it was created.</summary>
    public long RenderedFrameCount => Interlocked.Read(ref _renderedFrameCount);

    public OperationResult Configure(VideoStreamInfo stream, IVideoRenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return OperationResult.Success();
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

    public bool TryGetDisplayedFrameCount(out long count)
    {
        // It has no display, so it has nothing to answer with — which is the honest failure the interface allows,
        // and what keeps "presented" from being read as "shown".
        count = 0;
        return false;
    }

    public void Dispose()
    {
    }
}
