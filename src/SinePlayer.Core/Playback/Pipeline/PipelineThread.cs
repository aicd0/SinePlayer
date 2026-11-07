// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class PipelineThread
{
    private static int _liveCount;

    internal static int LiveCount => Volatile.Read(ref _liveCount);

    private readonly Thread _thread;
    private volatile bool _stopRequested;

    public PipelineThread(string name, Action<Func<bool>> body)
    {
        _thread = new Thread(() => Run(body))
        {
            IsBackground = true,
            Name = name,
        };
    }

    public bool StopRequested => _stopRequested;

    public void Start()
    {
        _thread.Start();
    }

    public void RequestStop()
    {
        _stopRequested = true;
    }

    public void Join()
    {
        _thread.Join();
    }

    private void Run(Action<Func<bool>> body)
    {
        Interlocked.Increment(ref _liveCount);

        try
        {
            body(() => _stopRequested);
        }
        finally
        {
            Interlocked.Decrement(ref _liveCount);
        }
    }
}
