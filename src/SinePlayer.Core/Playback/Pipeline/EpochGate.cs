// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class EpochGate
{
    public long Epoch { get; init; }

    public TimeSpan SeekPosition { get; init; }

    public SeekMode SeekMode { get; init; }

    public TimeSpan ReadOffset { get; init; }

    public IReadOnlyDictionary<IChainFeed, TimeSpan> Offsets { get; init; } = new Dictionary<IChainFeed, TimeSpan>();

    public TimeSpan OffsetOf(IChainFeed chain)
    {
        return Offsets.TryGetValue(chain, out TimeSpan offset) ? offset : TimeSpan.Zero;
    }
}
