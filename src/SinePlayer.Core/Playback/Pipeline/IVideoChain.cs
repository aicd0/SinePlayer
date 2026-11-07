// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Timing;

namespace SinePlayer.Core.Playback.Pipeline;

public interface IVideoChain : IPlaybackChain
{
    IVideoRenderTarget? Target { get; }

    ISynchronizer Synchronizer { get; set; }

    long PresentedFrameCount { get; }

    long DroppedFrameCount { get; }

    long DecodedFrameCount { get; }

    double AverageDecodeMilliseconds { get; }

    double AverageRenderMilliseconds { get; }

    TimeSpan PictureLag { get; }

    TimeSpan LastPresentedTimestamp { get; }
}
