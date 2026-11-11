// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Engine;

internal sealed class PipelineNode : IPipelineWakeTarget
{
    private readonly object _lock = new();
    private long _stamp;
    private long _wakeCount;

    internal long Stamp => Volatile.Read(ref _stamp);

    internal long WakeCount => Interlocked.Read(ref _wakeCount);

    public void Wake()
    {
        lock (_lock)
        {
            _stamp++;
            Interlocked.Increment(ref _wakeCount);
            Monitor.PulseAll(_lock);
        }
    }

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
