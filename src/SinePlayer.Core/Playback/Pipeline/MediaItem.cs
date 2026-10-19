// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class MediaItem<T>
{
    internal MediaItem(T? item, long epoch, bool isLast)
    {
        Item = item;
        Epoch = epoch;
        IsLast = isLast;
    }

    internal T? Item { get; }

    internal long Epoch { get; }

    internal bool IsLast { get; }
}
