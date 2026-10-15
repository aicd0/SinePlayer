// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;

namespace SinePlayer.Core.Playback.Internal;

/// <summary>
/// A queue between one stage of playback and the next: a producer adds, a consumer takes, and every item
/// carries the tag it was produced under.
/// <para>
/// <b>What the queue does not know:</b> what the tag means. The pipeline tags media with the epoch it was
/// produced for, and both ends of the queue decide what to do about it — the producer checks before it hands
/// work over (because handing over is what books it as progress), and the consumer checks before it uses what
/// it took. Nothing in here compares a tag against anything, which is why a queue cannot be the place a seek
/// goes wrong.
/// </para>
/// <para>
/// <b>The three configurations, and why they are different.</b> All three of playback's queues are this
/// class, and the policy each one carries is one argument at its construction site:
/// <list type="bullet">
/// <item><b>No limit</b> (packets, audio) — a producer never waits, because the producer here is the reader
/// and the reader is the sound: a full video queue that stopped it would make the picture's slowness heard.
/// </item>
/// <item><b>A limit</b> (frames) — the producer waits for room, and the wait ends when the consumer takes an
/// item or the queue is closed. This is the one back-pressure the design keeps, and it is deliberate: a
/// decoder that has run ahead of the picture should stop, and it cannot stop the sound, because the sound
/// comes from a queue that has no limit.</item>
/// <item><b>A way to weigh items</b> (audio) — for a consumer that is paced in samples rather than in items.
/// </item>
/// </list>
/// What the queue also does not know is whether anything more is coming: "the file has run out" is the
/// pipeline's statement about the position it is at, and it is held there (`_sourceEnded`), where the thread
/// that drains a decoder can read it directly. A queue that could be completed would be a second copy of that
/// statement, kept in step by hand — and the one place it was ever needed (the decode thread deciding to
/// drain) reads the pipeline's flag anyway.
/// </para>
/// </summary>
internal sealed class MediaQueue<T> : IDisposable
    where T : IDisposable
{
    private readonly object _lock = new();
    private readonly Queue<(long Tag, T Item)> _items = [];

    /// <summary>How many items may wait here, or zero for as many as the producer produces.</summary>
    private readonly int _limit;

    /// <summary>What one item is worth to whoever paces the producer, in items' own units.</summary>
    private readonly Func<T, long>? _weigh;

    private long _weight;
    private bool _closed;

    /// <param name="limit">How many items may be waiting before the producer has to pause, or zero for no limit.</param>
    /// <param name="weigh">What one item is worth, for a producer that is paced in something other than items.</param>
    internal MediaQueue(int limit = 0, Func<T, long>? weigh = null)
    {
        _limit = Math.Max(0, limit);
        _weigh = weigh;
    }

    /// <summary>How many items are waiting.</summary>
    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>
    /// What is waiting, in the units the queue was given a way to weigh items in — the item count when it
    /// was not given one. This is what a producer that paces itself by how much it has produced reads.
    /// </summary>
    internal long Weight
    {
        get
        {
            lock (_lock)
            {
                return _weight;
            }
        }
    }

    /// <summary>True once the queue has been closed, which means it takes nothing more and holds nothing.</summary>
    internal bool IsClosed
    {
        get
        {
            lock (_lock)
            {
                return _closed;
            }
        }
    }

    /// <summary>
    /// Adds an item under the tag it was produced for, waiting for room when the queue has a limit. Returns
    /// false once the queue is closed, in which case the caller still owns the item.
    /// </summary>
    internal bool TryEnqueue(T item, long tag)
    {
        lock (_lock)
        {
            while (_limit > 0 && _items.Count >= _limit && !_closed)
            {
                // Released by a consumer taking an item, or by a close — the two things that can make room
                // or make the wait pointless.
                Monitor.Wait(_lock);
            }

            if (_closed)
            {
                return false;
            }

            // The tag goes in with the item rather than being read again on the way out: the two ends are
            // different threads, and what was queued for one position must not be taken for another.
            _items.Enqueue((tag, item));
            _weight += Weigh(item);
            Monitor.PulseAll(_lock);
            return true;
        }
    }

    /// <summary>Looks at the oldest item without taking it, which costs nothing and changes nothing.</summary>
    internal bool TryPeek(out T? item, out long tag)
    {
        item = default;
        tag = 0;

        lock (_lock)
        {
            if (_items.Count == 0)
            {
                return false;
            }

            (tag, item) = _items.Peek();
            return true;
        }
    }

    /// <summary>Removes the oldest item, waiting up to the given time for one to arrive.</summary>
    internal bool TryDequeue(out T? item, out long tag, int timeoutMilliseconds)
    {
        item = default;
        tag = 0;

        lock (_lock)
        {
            if (_items.Count == 0 && !_closed)
            {
                Monitor.Wait(_lock, timeoutMilliseconds);
            }

            if (_items.Count == 0)
            {
                return false;
            }

            long queued;
            (queued, item) = _items.Dequeue();
            tag = queued;
            _weight -= Weigh(item);
            Monitor.PulseAll(_lock);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _closed = true;
            DisposeItems();
            Monitor.PulseAll(_lock);
        }
    }

    private long Weigh(T item)
    {
        return _weigh is null ? 1 : _weigh(item);
    }

    private void DisposeItems()
    {
        while (_items.Count > 0)
        {
            _items.Dequeue().Item.Dispose();
        }

        _weight = 0;
    }
}
