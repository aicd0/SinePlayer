// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Abstractions.Render;

public interface IVideoRenderer : IDisposable
{
    string Name { get; }

    IGpuDevice? GpuDevice { get; }

    OperationResult Configure(VideoStreamInfo stream, IVideoRenderTarget target);

    OperationResult Render(RawVideoFrame frame, VideoTransform transform);

    OperationResult RefreshSurface(VideoTransform transform);

    OperationResult ClearSurface();

    bool TryGetDisplayedFrameCount(out long count);
}
