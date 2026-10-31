// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Pipeline;

/// <summary>
/// The one place a coroutine waits, and the only thing another thread does to wake it — the asynchronous twin of
/// <see cref="PipelineNode"/>, for work that shares its thread with other work instead of owning one.
/// <para>
/// <b>The protocol, and why a wake cannot be lost.</b> A coroutine reads <see cref="Stamp"/> <em>before</em> it looks at
/// anything, decides what to do with what it sees, and — only when there is nothing to do — awaits
/// <see cref="WaitAsync"/>. A wake that arrives after that read has already moved the stamp, so the wait returns at once;
/// a wake that arrives before the read is visible in the facts the coroutine then reads. A wait therefore never begins
/// on a stamp that has already been superseded.
/// </para>
/// <para>
/// <b>A wake may be spurious, and must never be missed.</b> A woken coroutine that finds nothing to do awaits again, and
/// wakes nobody in turn, so a loose wake costs one iteration. What is not allowed is failing to wake a coroutine whose
/// decision changed, which is why every writer of a fact a coroutine reads is expected to call one of the pipeline's
/// <c>TryWake</c> methods.
/// </para>
/// <para>
/// <b>A wake never waits.</b> <see cref="Wake"/> takes this node's lock only to move the stamp and to take the signal it
/// completes, which it completes after letting the lock go: it never waits for the woken coroutine to look, decide or
/// work.
/// </para>
/// </summary>
internal sealed class PipelineNodeAsync
{
    private readonly Lock _lock = new();

    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _stamp;
    private long _wakeCount;

    internal long Stamp => Volatile.Read(ref _stamp);

    internal long WakeCount => Interlocked.Read(ref _wakeCount);

    internal void Wake()
    {
        TaskCompletionSource signal;
        lock (_lock)
        {
            Interlocked.Increment(ref _stamp);
            Interlocked.Increment(ref _wakeCount);
            signal = _signal;
            _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Completed outside the lock, so a continuation never runs while this node is held.
        signal.TrySetResult();
    }

    internal Task WaitAsync(long stamp, int millisecondsTimeout = Timeout.Infinite)
    {
        Task signal;
        lock (_lock)
        {
            if (Volatile.Read(ref _stamp) != stamp)
            {
                return Task.CompletedTask;
            }

            signal = _signal.Task;
            if (millisecondsTimeout == Timeout.Infinite)
            {
                return signal;
            }
        }

        return WaitWithTimeoutAsync(signal, millisecondsTimeout);
    }

    private static async Task WaitWithTimeoutAsync(Task signal, int millisecondsTimeout)
    {
        try
        {
            await signal.WaitAsync(TimeSpan.FromMilliseconds(millisecondsTimeout));
        }
        catch (TimeoutException)
        {
        }
    }
}
