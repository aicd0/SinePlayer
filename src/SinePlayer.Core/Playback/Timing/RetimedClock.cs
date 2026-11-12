// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Timing;

namespace SinePlayer.Core.Playback.Timing;

internal sealed class RetimedClock(IPlaybackClock inner) : IPlaybackClock
{
    private readonly Lock _lock = new();

    private double _rate = 1.0;
    private TimeSpan _fromClock;
    private TimeSpan _fromPosition;

    internal double Rate
    {
        get
        {
            lock (_lock)
            {
                return _rate;
            }
        }
    }

    public TimeSpan Position
    {
        get
        {
            lock (_lock)
            {
                return At(inner.Position);
            }
        }
    }

    public bool IsRunning => inner.IsRunning;

    public void Start()
    {
        inner.Start();
    }

    public void Pause()
    {
        inner.Pause();
    }

    public void SetPosition(TimeSpan position)
    {
        lock (_lock)
        {
            inner.SetPosition(position);
            StartFrom(position, position);
        }
    }

    /// <summary>
    /// Changes how fast position passes, without moving it. A rate this layer cannot honour is taken as no rate at all: it
    /// cannot stop time or run it backwards, and the range a user may ask for belongs to whoever asks.
    /// </summary>
    internal void SetRate(double rate)
    {
        lock (_lock)
        {
            TimeSpan clock = inner.Position;
            TimeSpan position = At(clock);
            StartFrom(clock, position);
            _rate = double.IsFinite(rate) && rate > 0 ? rate : 1.0;
        }
    }

    /// <summary>Where a reading of the wrapped clock lands, at the current rate. Called under the lock.</summary>
    private TimeSpan At(TimeSpan clock)
    {
        // Untouched, the retiming is the identity - which keeps every caller that never sets a rate reading exactly what the
        // clock underneath reads.
        if (_rate == 1.0 && _fromClock == _fromPosition)
        {
            return clock;
        }

        return _fromPosition + TimeSpan.FromTicks((long)((clock - _fromClock).Ticks * _rate));
    }

    /// <summary>Says that the clock and the position are equal here, and that everything after is measured from it.</summary>
    private void StartFrom(TimeSpan clock, TimeSpan position)
    {
        _fromClock = clock;
        _fromPosition = position;
    }
}
