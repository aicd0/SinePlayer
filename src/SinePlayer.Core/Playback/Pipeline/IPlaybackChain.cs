// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Pipeline;

public interface IPlaybackChain
{
    MediaStreamKind Kind { get; }

    MediaStreamInfo? SelectedStream { get; }

    MediaStreamInfo? PlayingStream { get; }

    bool IsActive { get; }

    string? DecoderName { get; }

    string? RendererName { get; }

    bool IsHardwareAccelerated { get; }
}