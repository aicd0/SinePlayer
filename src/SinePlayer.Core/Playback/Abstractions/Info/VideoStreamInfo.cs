// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Info;

public sealed class VideoStreamInfo : MediaStreamInfo
{
    public override MediaStreamKind Kind => MediaStreamKind.Video;

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required double FrameRate { get; init; }

    public required PixelFormat PixelFormat { get; init; }

    public int BitDepth { get; init; }
}
