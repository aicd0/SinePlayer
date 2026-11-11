// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;

namespace SinePlayer.Core.Playback.Engine;

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

    internal static TimeSpan ReadOffsetOf(IReadOnlyDictionary<IChainFeed, TimeSpan> offsets)
    {
        TimeSpan readOffset = TimeSpan.Zero;
        bool first = true;

        foreach (TimeSpan offset in offsets.Values)
        {
            TimeSpan candidate = TimeSpan.Zero - offset;
            if (first || candidate < readOffset)
            {
                readOffset = candidate;
                first = false;
            }
        }

        return readOffset;
    }

    internal static bool AnyOffsetIncreased(IReadOnlyDictionary<IChainFeed, TimeSpan> before, IReadOnlyDictionary<IChainFeed, TimeSpan> after)
    {
        foreach (KeyValuePair<IChainFeed, TimeSpan> entry in after)
        {
            TimeSpan was = before.TryGetValue(entry.Key, out TimeSpan held) ? held : TimeSpan.Zero;
            if (entry.Value > was)
            {
                return true;
            }
        }

        return false;
    }
}
