// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Core.Playback.Abstractions.Info;

public sealed class VideoStreamInfo : MediaStreamInfo
{
    public override MediaStreamKind Kind => MediaStreamKind.Video;

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Frames per second, or 0 when the container does not state one.</summary>
    public required double FrameRate { get; init; }

    /// <summary>Format the decoder is expected to produce for this stream.</summary>
    public required PixelFormat PixelFormat { get; init; }

    public int BitDepth { get; init; }

    /// <summary>Rotation the container asks for, normalized to 0, 90, 180 or 270.</summary>
    public int RotationDegrees { get; init; }
}
