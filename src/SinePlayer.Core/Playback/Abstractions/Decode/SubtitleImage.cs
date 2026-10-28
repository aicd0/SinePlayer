// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Decode;

public sealed partial class SubtitleImage : IDisposable
{
    public required ISubtitleImageBuffer Buffer { get; init; }

    public required double Left { get; init; }

    public required double Top { get; init; }

    public required double Width { get; init; }

    public required double Height { get; init; }

    public void Dispose()
    {
        Buffer.Dispose();
    }
}
