// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Timing;
using SinePlayer.Core.Playback.Selection;

namespace SinePlayer.Core.Playback.Pipeline;

public interface IVideoChain : IPlaybackChain<VideoStreamInfo>, IPictureFrameSource
{
    VideoComponentResolver Components { get; }

    IVideoRenderTarget? Target { get; }

    VideoTransform Transform { get; set; }

    ISynchronizer Synchronizer { get; set; }

    long PresentedFrameCount { get; }

    long DroppedFrameCount { get; }

    long DecodedFrameCount { get; }

    double AverageDecodeMilliseconds { get; }

    double AverageRenderMilliseconds { get; }

    TimeSpan PictureLag { get; }

    TimeSpan LastPresentedTimestamp { get; }
}
