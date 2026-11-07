// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Pipeline;

public interface IPlaybackChain
{
    MediaStreamKind Kind { get; }

    string? DecoderName { get; }

    string? RendererName { get; }

    bool IsHardwareAccelerated { get; }
}

public interface IPlaybackChain<out TStream> : IPlaybackChain where TStream : MediaStreamInfo
{
    TStream? SelectedStream { get; }

    TStream? PlayingStream { get; }
}