// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Abstractions.Decode;

public sealed partial class RawVideoFrame : IDisposable
{
    public required TimeSpan Timestamp { get; init; }

    public required TimeSpan Duration { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required PixelFormat Format { get; init; }

    public required IMediaBuffer Buffer { get; init; }

    public bool IsHardwareBacked { get; init; }

    public ColourInfo Colour { get; init; }

    public void Dispose()
    {
        Buffer.Dispose();
    }
}
