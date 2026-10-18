// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Core.Playback.Pipeline;

/// <summary>
/// The one place a thread of the pipeline sleeps, and the only thing another thread does to wake it.
/// <para>
/// <b>One node per thread.</b> A node has exactly one waiter — the thread it belongs to — and any number of
/// wakers. It carries no state and answers no questions: the facts a thread decides on live where they are
/// written, and a node only says "look again". That is what keeps the decision in one place and the wake
/// mechanism in another, with nothing to keep in step between them.
/// </para>
/// <para>
/// <b>The protocol, and why a wake cannot be lost.</b> A loop reads <see cref="Stamp"/> <em>before</em> it
/// looks at anything, decides what to do with what it sees, and — only when there is nothing to do — calls
/// <see cref="Wait"/>. A wake that arrives after the read has already moved the stamp, so the wait finds a
/// stamp that is not the one it was given and returns at once; a wake that arrives before the read is
/// visible in the facts the loop then reads. There is no window between deciding and sleeping in which a
/// wake can disappear — which is the whole reason this class exists, and the reason the read must come
/// first.
/// </para>
/// <para>
/// <b>A wake may be spurious, and must never be missed.</b> Wakers are allowed to be generous: a woken
/// thread that finds nothing to do goes back to sleep, and wakes nobody in turn, so a loose wake costs one
/// iteration rather than a spin. What is not allowed is failing to wake a thread whose decision changed,
/// which is why every writer of a fact a thread reads is expected to call one of the pipeline's
/// <c>TryWake</c> methods.
/// </para>
/// <para>
/// <b>A wake never waits.</b> <see cref="Wake"/> takes this node's lock, which is only ever held for the
/// bookkeeping of a wait, and returns: it never waits for the woken thread to look, decide or work. A waker
/// is therefore free to call it from a queue operation, a command handler or another media thread, provided
/// it holds no lock of its own at the time.
/// </para>
/// </summary>
internal sealed class PipelineNode
{
    private readonly object _lock = new();
    private long _stamp;
    private long _wakeCount;

    /// <summary>
    /// What the stamp was when this thread last looked, which is what a wait compares against. Read it
    /// before looking at anything else.
    /// </summary>
    internal long Stamp => Volatile.Read(ref _stamp);

    /// <summary>
    /// How many times the node has been woken, as a diagnostic: a parked pipeline should leave this still,
    /// which is how "no thread is polling" is stated rather than assumed.
    /// </summary>
    internal long WakeCount => Interlocked.Read(ref _wakeCount);

    /// <summary>
    /// Says that something a thread looks at has changed. Returns as soon as the note is left — it never
    /// waits for the woken thread — and waking a node nobody is waiting on is nothing at all: the stamp
    /// moves, and the thread's next wait returns without sleeping.
    /// </summary>
    internal void Wake()
    {
        lock (_lock)
        {
            _stamp++;
            Interlocked.Increment(ref _wakeCount);
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>
    /// Sleeps until the node is woken or the time given runs out, and returns at once when the stamp is not
    /// the one handed in — which is what makes a wake that arrived before this call un-losable. A returned
    /// wait says nothing about why: the caller decides again, which is where the meaning of the wake lives.
    /// </summary>
    /// <param name="stamp">What <see cref="Stamp"/> read before the caller looked at anything.</param>
    /// <param name="millisecondsTimeout">
    /// How long to sleep at most, or <see cref="Timeout.Infinite"/>. A caller that waits for a fact of its
    /// own never gives a time; a caller whose answer also depends on time gives a fixed cadence.
    /// </param>
    internal void Wait(long stamp, int millisecondsTimeout = Timeout.Infinite)
    {
        lock (_lock)
        {
            if (_stamp != stamp)
            {
                return;
            }

            if (millisecondsTimeout < 0)
            {
                Monitor.Wait(_lock);
            }
            else
            {
                Monitor.Wait(_lock, millisecondsTimeout);
            }
        }
    }
}
