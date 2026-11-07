// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Core.Playback.Pipeline;

public interface ISubtitleChain : IPlaybackChain
{
    IVideoRenderTarget? Target { get; }
}
