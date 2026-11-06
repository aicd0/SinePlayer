// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class PipelineCoroutine(Func<Func<bool>, Task> body)
{
    private volatile bool _stopRequested;
    private int _started;

    public bool IsStopRequested => _stopRequested;

    public Func<Func<bool>, Task> Body { get; } = body;

    public void RequestStop()
    {
        _stopRequested = true;
    }

    internal bool TryBegin()
    {
        return Interlocked.Exchange(ref _started, 1) == 0;
    }
}