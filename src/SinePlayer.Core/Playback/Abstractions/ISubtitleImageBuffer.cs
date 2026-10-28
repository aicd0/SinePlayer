// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions;

public interface ISubtitleImageBuffer : IMediaBuffer
{
    int Width { get; }

    int Height { get; }

    int Stride { get; }

    nint Pixels { get; }
}
