// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Utils;

/// <summary>
/// A value that changes rarely and is read constantly: read without a lock, changed as one atomic fact.
/// <para>
/// A seqlock. A sequence number is even while the value is settled and odd while it is being changed; a reader
/// takes the sequence, the value and the sequence again, and starts over if either read saw a change or the
/// odd half of one. A value it accepts is therefore one it read while no change was in progress — which means
/// the change that produced it has <em>finished</em>.
/// </para>
/// <para>
/// That "has finished" is the point of the class rather than a detail of it: <see cref="Change"/> runs the
/// caller's work inside the same odd window as the value, so more than the value can travel with a change. The
/// playback pipeline uses it for the position — the epoch is the value, and the work is everything that has to
/// be true before a thread may act on the new position: the queues emptied of the old one's media, the
/// starvation flags cleared, the anchor re-armed. A thread that acts on a position whose reset is still
/// running produces media that reset throws away, and with the work inside the window that cannot happen.
/// </para>
/// <para>
/// The value may be several fields wide, and a reader still gets all of them from one change: the sequence is
/// read again <em>after</em> the value, so a value torn by a change in progress — or replaced by a change that
/// ran entirely between the reader's two reads of the sequence — is rejected and read again rather than handed
/// out half-made.
/// </para>
/// <para>
/// Reading is bounded rather than a spin: a few attempts, and then the reader waits for the writer's lock. That
/// is the correct answer and the rare one — a change that outlasts several attempts has something slow inside
/// it (a device call, say), and a reader holding a core is a reader delaying the writer.
/// </para>
/// </summary>
/// <typeparam name="TValue">What the lock carries; one fact as far as <see cref="Read"/> is concerned.</typeparam>
internal sealed class SeqLock<TValue>
{
    /// <summary>How many times a reader tries before waiting for the writer instead.</summary>
    private const int Attempts = 4;

    private readonly Lock _lock = new();
    private TValue _value;
    private int _sequence;

    /// <param name="initial">The value before anything has changed it.</param>
    internal SeqLock(TValue initial)
    {
        _value = initial;
    }

    /// <summary>The value, read without taking the lock.</summary>
    internal TValue Read()
    {
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            int sequence = Volatile.Read(ref _sequence);
            if ((sequence & 1) != 0)
            {
                // A change is in progress. Yielding rather than spinning: the writer is one thread doing a
                // bounded amount of work, and a reader that keeps the core busy is a reader delaying it.
                Thread.Yield();
                continue;
            }

            TValue value = _value;
            if (Volatile.Read(ref _sequence) == sequence)
            {
                return value;
            }
        }

        lock (_lock)
        {
            return _value;
        }
    }

    /// <summary>
    /// Changes the value — <paramref name="update"/> is given the current one and returns the new one — and
    /// runs <paramref name="work"/> as part of the same change, so that no reader can see the new value before
    /// that work is done. Both run under the lock the readers fall back to, which is what keeps two changes
    /// from interleaving; the work must not wait for a reader to make progress, and nothing the pipeline does
    /// inside the window does.
    /// </summary>
    internal void Change(Func<TValue, TValue> update, Action<TValue>? work = null)
    {
        lock (_lock)
        {
            // Odd for the whole change: a reader that sees the new value has to see the work that belongs to
            // it, and this is what tells it to step back until it does.
            Volatile.Write(ref _sequence, _sequence + 1);
            try
            {
                _value = update(_value);
                work?.Invoke(_value);
            }
            finally
            {
                // Even again whether the work finished or threw: a reader must never be left with an odd
                // sequence for ever, waiting for a change that is not coming.
                Volatile.Write(ref _sequence, _sequence + 1);
            }
        }
    }
}
