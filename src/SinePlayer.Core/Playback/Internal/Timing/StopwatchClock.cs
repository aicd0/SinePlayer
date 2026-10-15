// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Diagnostics;

using SinePlayer.Core.Playback.Abstractions.Timing;

namespace SinePlayer.Core.Playback.Internal.Timing;

/// <summary>
/// A clock for playback with no audio to follow, driven by the wall clock. Audio is the better master
/// whenever it is present, so this exists for the files where it is not: a video whose frames would
/// otherwise have nothing to be matched against.
/// <para>
/// Every thread touches it — the queue starts, pauses and seeks it, the first frame of a position anchors
/// it, and anything may ask what time it is — so it is written as one that expects that, like the device
/// clock of the audio renderer. The lock covers the base and the running state as a whole rather than field
/// by field: reading one half of the clock and the other half of a moment ago would report a position that
/// never happened.
/// </para>
/// </summary>
internal sealed class StopwatchClock : IPlaybackClock
{
    private readonly Stopwatch _stopwatch = new();
    private readonly Lock _lock = new();

    private TimeSpan _base;
    private bool _running;
    private double _rate = 1.0;

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

    public double Rate
    {
        get
        {
            lock (_lock)
            {
                return _rate;
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

    public void SetRate(double rate)
    {
        lock (_lock)
        {
            _base = ReadPosition();
            _stopwatch.Restart();
            if (!_running)
            {
                _stopwatch.Stop();
            }

            _rate = rate > 0 ? rate : 1.0;
        }
    }

    /// <summary>Where the clock is now. Called under the lock, and by nothing else.</summary>
    private TimeSpan ReadPosition()
    {
        return _running ? _base + TimeSpan.FromSeconds(_stopwatch.Elapsed.TotalSeconds * _rate) : _base;
    }
}
