// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Diagnostics;

using SinePlayer.Core.Playback.Abstractions.Timing;

namespace SinePlayer.Core.Playback.Timing;

internal sealed class StopwatchClock : IPlaybackClock
{
    private readonly Stopwatch _stopwatch = new();
    private readonly Lock _lock = new();

    private TimeSpan _base;
    private bool _running;

    public TimeSpan Position
    {
        get
        {
            lock (_lock)
            {
                return ReadPosition();
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _running;
            }
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_running)
            {
                return;
            }

            _stopwatch.Restart();
            _running = true;
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (!_running)
            {
                return;
            }

            _base = ReadPosition();
            _stopwatch.Stop();
            _running = false;
        }
    }

    public void SetPosition(TimeSpan position)
    {
        lock (_lock)
        {
            _base = position;
            _stopwatch.Restart();
            if (!_running)
            {
                _stopwatch.Stop();
            }
        }
    }

    /// <summary>Where the clock is now. Called under the lock, and by nothing else.</summary>
    private TimeSpan ReadPosition()
    {
        return _running ? _base + _stopwatch.Elapsed : _base;
    }
}
