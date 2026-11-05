// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class PipelineThread
{
    private readonly Thread _thread;
    private volatile bool _stopRequested;

    public PipelineThread(string name, Action<Func<bool>> body)
    {
        _thread = new Thread(() => body(() => _stopRequested))
        {
            IsBackground = true,
            Name = name,
        };
    }

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
}
