// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Core.Playback.Internal;

/// <summary>
/// A queue between one stage of playback and the next: a producer adds, a consumer takes, and every entry
/// carries the tag it was produced under — and, when it is the end of a stream, the flag that says so.
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
/// <b>The end of a stream.</b> A producer that will hand over nothing more says so with
/// <see cref="EndStream(long)"/>, and what it says is an entry like any other: the consumer takes it after
/// everything enqueued before it, as a <see cref="Dequeue"/> that returned true with no item — the
/// `isLast` that says nothing more is coming, and the tag of the position the stream ended at. That is what
/// lets a consumer know the stream is over without inferring it from an empty queue and without asking the
/// pipeline about the position — and without waiting either, since an entry wakes whoever is waiting for
/// one. It is ordered with the media it follows, so it cannot be seen early; one that outlives a position
/// change — the race between a producer ending its stream and playback moving on — is harmless rather than
/// special, because its tag is old and comparing tags is what both ends already do. What the queue does not
/// carry is the pipeline's own statement, "the file has run out at this position" (`_endingEpoch`): the end
/// of a stream is the producer's fact about *this* queue.
/// </para>
/// </summary>
internal sealed class MediaQueue<T> : IDisposable
    where T : IDisposable
{
    private readonly object _lock = new();
    private readonly Queue<(long Tag, T? Item, bool IsLast)> _items = [];

    /// <summary>How many items may wait here, or zero for as many as the producer produces.</summary>
    private readonly int _limit;

    /// <summary>What one item is worth to whoever paces the producer, in items' own units.</summary>
    private readonly Func<T, long>? _weigh;

    private int _mediaCount;
    private long _weight;
    private bool _closed;

    /// <param name="limit">How many items may be waiting before the producer has to pause, or zero for no limit.</param>
    /// <param name="weigh">What one item is worth, for a producer that is paced in something other than items.</param>
    internal MediaQueue(int limit = 0, Func<T, long>? weigh = null)
    {
        _limit = Math.Max(0, limit);
        _weigh = weigh;
    }

    /// <summary>How much media is waiting; the end of a stream is not media and is not counted.</summary>
    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _mediaCount;
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
    internal bool Enqueue(T item, long tag)
    {
        lock (_lock)
        {
            while (_limit > 0 && _mediaCount >= _limit && !_closed)
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
            _items.Enqueue((tag, item, false));
            _mediaCount++;
            _weight += Weigh(item);
            Monitor.PulseAll(_lock);
            return true;
        }
    }

    /// <summary>
    /// Says that nothing more will be handed over for this tag. The consumer takes an entry of its own after
    /// everything enqueued before it — an entry that answers true and hands over no media, because the media
    /// ran out rather than because there is none yet — so the end of a stream reaches the consumer as
    /// something it was handed rather than something it has to infer, and it reaches it even when the queue
    /// is full: the entry takes no room, which matters because a producer that has ended its stream is very
    /// often the one a consumer is waiting for room from.
    /// </summary>
    internal void EndStream(long tag)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _items.Enqueue((tag, default, true));
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>
    /// Looks at the oldest entry without taking it, which costs nothing and changes nothing. The end of a
    /// stream is looked at as an entry with no media in it, which is all a caller asking what is behind the
    /// one it holds needs to know.
    /// </summary>
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

            (tag, item, _) = _items.Peek();
            return true;
        }
    }

    /// <summary>
    /// Removes the oldest entry, waiting for one to arrive. True is an entry: media of the tag's position,
    /// or — with <paramref name="isLast"/> — the end of the stream that <paramref name="tag"/> names, which
    /// comes with no item because the media ran out rather than because there is none yet. False is the
    /// queue closed and nothing more coming: it is answered at once, never waited for.
    /// <para>
    /// There is no clock in this wait: what ends it is an entry or a close, and a caller is never woken to
    /// be told that nothing happened. That is why the wait is a condition rather than a single check — a
    /// clear is a pulse no consumer has anything to do about, and it must leave the consumer where it was
    /// rather than hand it a result it would have to guess the meaning of. Nothing is inferred from an
    /// empty queue: the end of a stream is an entry, and a closed queue is what a stopping pipeline gives
    /// every waiter, which is the release the stop's joins depend on.
    /// </para>
    /// </summary>
    internal bool Dequeue(out T? item, out long tag, out bool isLast)
    {
        item = default;
        tag = 0;
        isLast = false;

        lock (_lock)
        {
            while (_items.Count == 0 && !_closed)
            {
                Monitor.Wait(_lock);
            }

            if (_items.Count == 0)
            {
                return false;
            }

            long queued;
            (queued, item, isLast) = _items.Dequeue();
            tag = queued;

            // The end of a stream has no weight and takes no room: it is a word about the media rather than
            // media, and the one thing that does not have to make room for it is the consumer that follows.
            if (item is not null)
            {
                _mediaCount--;
                _weight -= Weigh(item);
            }

            Monitor.PulseAll(_lock);
            return true;
        }
    }

    /// <summary>
    /// Drops what the queue holds, which is what a seek does to it. The producer waiting for room goes on —
    /// that room is the reason a seek releases the decoder — while a consumer waiting for an entry is left
    /// waiting: there is nothing for it to act on, and what it should play next arrives as the next entry,
    /// stamped with the position it belongs to.
    /// </summary>
    internal void Clear()
    {
        lock (_lock)
        {
            DisposeItems();
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>
    /// Closes the queue: it takes nothing more, holds nothing, and releases everyone waiting on it — a
    /// producer with false, a consumer with false — which is the release a stop's joins depend on. A closed
    /// queue answers at once, so a consumer asking one finds out that it is over rather than waiting for
    /// an entry that is not coming.
    /// </summary>
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
            _items.Dequeue().Item?.Dispose();
        }

        _mediaCount = 0;
        _weight = 0;
    }
}
