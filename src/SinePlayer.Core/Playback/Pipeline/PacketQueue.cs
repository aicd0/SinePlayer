// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Concurrent;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class PacketQueue
{
    private readonly ConcurrentQueue<PacketEntry> _entries = new();

    private long _count;
    private long _ticks;

    internal int Count => (int)Interlocked.Read(ref _count);

    /// <summary>How much media is still to be decoded, which is what playback can go on for without a read.</summary>
    internal TimeSpan Queued => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    internal bool IsEmpty => _entries.IsEmpty;

    internal void Enqueue(PacketEntry entry)
    {
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _ticks, Math.Max(0, entry.Span.Ticks));
        _entries.Enqueue(entry);
    }

    internal bool TryPeek(out PacketEntry entry)
    {
        return _entries.TryPeek(out entry);
    }

    internal bool TryDequeue(out PacketEntry entry)
    {
        if (!_entries.TryDequeue(out entry))
        {
            return false;
        }

        Interlocked.Decrement(ref _count);
        Interlocked.Add(ref _ticks, -Math.Max(0, entry.Span.Ticks));
        return true;
    }

    internal void Clear()
    {
        while (_entries.TryDequeue(out PacketEntry entry))
        {
            entry.Packet?.Dispose();
        }

        Interlocked.Exchange(ref _count, 0);
        Interlocked.Exchange(ref _ticks, 0);
    }
}
