// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Source;

public sealed partial class MediaPacket : IDisposable
{
    private int _refCount = 1;

    public required int StreamIndex { get; init; }

    public required TimeSpan Timestamp { get; init; }

    public required TimeSpan Duration { get; init; }

    public required bool IsKeyFrame { get; init; }

    public required IMediaBuffer Buffer { get; init; }

    internal void Ref()
    {
        Interlocked.Increment(ref _refCount);
    }

    public void Dispose()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            Buffer.Dispose();
        }
    }
}
