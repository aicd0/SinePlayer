// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Engine;

internal sealed class PipelineNodeAsync : IPipelineWakeTarget
{
    private readonly Lock _lock = new();

    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _stamp;
    private long _wakeCount;

    internal long Stamp => Volatile.Read(ref _stamp);

    internal long WakeCount => Interlocked.Read(ref _wakeCount);

    public void Wake()
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
