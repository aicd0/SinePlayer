// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Diagnostics.CodeAnalysis;

namespace SinePlayer.Core.Playback.Pipeline;

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
/// <b>It holds what it is given and enforces nothing.</b> All three of playback's queues are this class, and
/// the difference between them is not a policy the queue carries: the one that weighs its items is given a way
/// to weigh them (<see cref="Weight"/>), and the one whose producer must not run ahead has that limit at the
/// producer — the frame window lives in the decode thread, which checks it before it takes work, and the
/// reader's read-ahead minimums live in the reader. A queue that enforced a limit would have to make its
/// producer wait, and a producer waiting inside a queue is a thread sleeping on a lock the consumer does not
/// own; keeping the number at the end that acts on it is what lets every thread sleep in one place (see
/// <see cref="PipelineNode"/>). A limit that is checked before work is taken is also a pacing target rather
/// than an invariant, which is the honest shape: a drain hands over whatever the codec was holding, and the
/// queue holds it for the moment it takes the picture to catch up.
/// </para>
/// <para>
/// <b>The end of a stream.</b> A producer that will hand over nothing more says so with
/// <see cref="EndStream(long)"/>, and what it says is an entry like any other: the consumer takes it after
/// everything enqueued before it, as an entry of its own — no media in it, because the media ran out rather
/// than because there is none yet, and the tag of the position the stream ended at. That is what lets a
/// consumer know the stream is over without inferring it from an empty queue and without asking the pipeline
/// about the position — and without waiting either, since an entry wakes whoever is waiting for one. It is
/// ordered with the media it follows, so it cannot be seen early; one that outlives a position change — the
/// race between a producer ending its stream and playback moving on — is harmless rather than special,
/// because its tag is old and comparing tags is what both ends already do. What the queue does not carry is
/// the pipeline's own statement, "the file has run out at this position" (`_endingEpoch`): the end of a
/// stream is the producer's fact about *this* queue.
/// </para>
/// <para>
/// <b>Looking and taking are separate words.</b> Everything a consumer is handed is a
/// <see cref="MediaItem{T}"/> — the media, the tag, and which of the two kinds of entry it is — so a consumer
/// that has to decide about an entry before it can use it can look without owning it: <see cref="Peek"/>
/// waits for one and changes nothing, and can be told how long to wait — a look with a deadline is how such a
/// consumer wakes up when its own answer could have changed, without the queue holding a clock or the caller
/// polling — <see cref="TryPeek"/> answers now or not at all, and
/// <see cref="TryDequeue(MediaItem{T})"/> takes back the entry it was given, if it is still the oldest one.
/// Nothing else about an entry may be relied on between the look and the take: the entry itself is the token,
/// which is why it is a class.
/// </para>
/// </summary>
internal sealed class MediaQueue<T> : IDisposable
    where T : IDisposable
{
    private readonly object _lock = new();
    private readonly Queue<MediaItem<T>> _items = [];

    /// <summary>What one item is worth to whoever paces the producer, in items' own units.</summary>
    private readonly Func<T, long>? _weigh;

    private int _mediaCount;
    private long _weight;
    private bool _closed;

    /// <param name="weigh">What one item is worth, for a producer that is paced in something other than items.</param>
    internal MediaQueue(Func<T, long>? weigh = null)
    {
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

    /// <summary>
    /// True once the queue has been closed, which means it takes nothing more and holds nothing. It is also
    /// what tells the two reasons <see cref="Peek"/> can answer null apart: a close, or a wait whose time ran
    /// out.
    /// </summary>
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
    /// Adds an item under the tag it was produced for. It never waits: false is the queue closed, in which
    /// case the caller still owns the item. How much may be waiting is the producer's business, and a
    /// producer that paces itself checks <see cref="Count"/>/<see cref="Weight"/> before it produces.
    /// </summary>
    internal bool TryEnqueue(T item, long tag)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return false;
            }

            // The tag goes in with the media rather than being read again on the way out: the two ends are
            // different threads, and what was queued for one position must not be taken for another.
            _items.Enqueue(new MediaItem<T>(item, tag, false));
            _mediaCount++;
            _weight += Weigh(item);
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

            _items.Enqueue(new MediaItem<T>(default, tag, true));
        }
    }

    /// <summary>
    /// Looks at the oldest entry without taking it or waiting for one, which costs nothing and changes
    /// nothing. False is nothing to look at at all.
    /// </summary>
    internal bool TryPeek([NotNullWhen(true)] out MediaItem<T>? entry)
    {
        lock (_lock)
        {
            entry = _items.Count == 0 ? null : _items.Peek();
            return entry is not null;
        }
    }

    /// <summary>
    /// Takes the oldest entry if there is one, and takes nothing otherwise. False is nothing taken — the queue
    /// is empty, or closed — and <see cref="IsClosed"/> tells the two apart for a caller that has to. It never
    /// waits: a consumer that has nothing to do sleeps on its own node and is woken when an entry arrives.
    /// </summary>
    internal bool TryDequeue([NotNullWhen(true)] out MediaItem<T>? entry)
    {
        lock (_lock)
        {
            entry = TakeHead();
            return entry is not null;
        }
    }

    /// <summary>
    /// Takes the entry that was looked at, if it is still the oldest one. False means it is not — the queue
    /// was cleared, or closed — and nothing was taken.
    /// <para>
    /// The entry is compared by identity, because that is the only fact about it a clear cannot leave
    /// standing: the entries it removes may carry the same tag as the ones that replace them. And a caller
    /// that is refused must not look at the entry again — the clear that removed it may already have let go
    /// of what it held — it looks at the queue instead.
    /// </para>
    /// </summary>
    internal bool TryDequeue(MediaItem<T> expected)
    {
        lock (_lock)
        {
            if (_items.Count == 0 || !ReferenceEquals(_items.Peek(), expected))
            {
                return false;
            }

            TakeHead();
            return true;
        }
    }

    /// <summary>Removes the oldest entry, or answers null when there is none to remove.</summary>
    private MediaItem<T>? TakeHead()
    {
        if (_items.Count == 0)
        {
            return null;
        }

        MediaItem<T> entry = _items.Dequeue();

        // The end of a stream has no weight and takes no room: it is a word about the media rather than
        // media, and the one thing that does not have to make room for it is the consumer that follows.
        if (entry.Item is not null)
        {
            _mediaCount--;
            _weight -= Weigh(entry.Item);
        }
        return entry;
    }

    /// <summary>
    /// Drops what the queue holds, which is what a seek does to it. It wakes nobody: a consumer waiting for
    /// an entry has nothing to do about a seek, and the position moving is what wakes it — on its own node,
    /// not here. Nothing waits for room in this queue either, so there is nothing else to release.
    /// </summary>
    internal void Clear()
    {
        lock (_lock)
        {
            DisposeItems();
        }
    }

    /// <summary>
    /// Closes the queue: it takes nothing more, holds nothing, and releases everyone waiting on it — which is
    /// the release a stopping pipeline's joins depend on. A closed queue answers at once, so a consumer asking
    /// one finds out that it is over rather than waiting for an entry that is not coming.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _closed = true;
            DisposeItems();
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
