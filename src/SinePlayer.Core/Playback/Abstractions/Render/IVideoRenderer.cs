// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Abstractions.Render;

public interface IVideoRenderer : IDisposable
{
    string Name { get; }

    IGpuDevice? GpuDevice { get; }

    PlaybackOperationResult Configure(VideoStreamInfo stream, IVideoRenderTarget target);

    PlaybackOperationResult Render(RawVideoFrame frame, VideoTransform transform);

    PlaybackOperationResult RefreshSurface(VideoTransform transform);

    bool TryGetDisplayedFrameCount(out long count);
}
