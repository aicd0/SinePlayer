// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Selection;

namespace SinePlayer.Core.Playback.Hosting;

public interface ISubtitleChain : IPlaybackChain<SubtitleStreamInfo>
{
    SubtitleComponentResolver Components { get; }

    SubtitlePresentation Presentation { get; set; }

    IVideoRenderTarget? Target { get; }

    IVideoChain? LayoutVideoChain { get; set; }
}
