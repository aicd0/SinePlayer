// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Concurrent;

namespace SinePlayer.Core.Playback.Utils;

internal sealed partial class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly ManualResetEventSlim _arrived = new(false);

    private volatile bool _completed;

    public override void Post(SendOrPostCallback d, object? state)
    {
        _queue.Enqueue((d, state));
        _arrived.Set();
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        throw new NotSupportedException("The pipeline's coroutines are posted to, never sent to.");
    }

    internal void Complete()
    {
        _completed = true;
        _arrived.Set();
    }

    internal void RunOnCurrentThread()
    {
        while (true)
        {
            while (_queue.TryDequeue(out (SendOrPostCallback Callback, object? State) work))
            {
                work.Callback(work.State);
            }

            _arrived.Reset();

            if (_completed)
            {
                return;
            }

            if (!_queue.IsEmpty)
            {
                continue;
            }

            _arrived.Wait();
        }
    }

    public void Dispose()
    {
        _arrived.Dispose();
    }
}
