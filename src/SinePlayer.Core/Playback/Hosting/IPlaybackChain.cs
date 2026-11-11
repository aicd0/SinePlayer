// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Hosting;

public interface IPlaybackChain
{
    MediaStreamKind Kind { get; }

    TimeSpan Offset { get; set; }

    string? DecoderName { get; }

    string? RendererName { get; }

    bool IsHardwareAccelerated { get; }

    Task<OperationResult> SelectStream(MediaStreamInfo? stream);
}

public interface IPlaybackChain<out TStream> : IPlaybackChain where TStream : MediaStreamInfo
{
    TStream? SelectedStream { get; }

    TStream? PlayingStream { get; }
}