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
/// </summary>
internal sealed class StopwatchClock : IPlaybackClock
{
    private readonly Stopwatch _stopwatch = new();

    private TimeSpan _base;
    private bool _running;
    private double _rate = 1.0;

    public TimeSpan Position => _running ? _base + TimeSpan.FromSeconds(_stopwatch.Elapsed.TotalSeconds * _rate) : _base;

    public bool IsRunning => _running;

    public double Rate => _rate;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _stopwatch.Restart();
        _running = true;
    }

    public void Pause()
    {
        if (!_running)
        {
            return;
        }

        _base = Position;
        _stopwatch.Stop();
        _running = false;
    }

    public void SetPosition(TimeSpan position)
    {
        _base = position;
        _stopwatch.Restart();
        if (!_running)
        {
            _stopwatch.Stop();
        }
    }

    public void SetRate(double rate)
    {
        _base = Position;
        _stopwatch.Restart();
        if (!_running)
        {
            _stopwatch.Stop();
        }

        _rate = rate > 0 ? rate : 1.0;
    }
}
