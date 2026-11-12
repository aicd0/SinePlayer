// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Timing;
using SinePlayer.Core.Playback.Timing;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The rate is a layer above a clock, not a property of one: it scales what the clock underneath reports, without knowing
/// whether that clock is a stopwatch or a device. These tests crank such a clock by hand, so what is being checked is the
/// arithmetic and when it restarts, not how fast a machine happens to run.
/// </summary>
[TestFixture]
public class RetimedClockTests
{
    private HandClock _inner = null!;
    private RetimedClock _clock = null!;

    [SetUp]
    public void SetUp()
    {
        _inner = new HandClock();
        _clock = new RetimedClock(_inner);
    }

    [Test]
    public void ReportsTheClockItWrapsUntilItIsRetimed()
    {
        _inner.Position = TimeSpan.FromSeconds(7);

        Assert.That(_clock.Rate, Is.EqualTo(1.0));
        Assert.That(_clock.Position, Is.EqualTo(TimeSpan.FromSeconds(7)), "untouched, this layer is the identity");
    }

    [Test]
    public void ScalesWhatPassesAfterTheRateIsSet()
    {
        _inner.Position = TimeSpan.FromSeconds(5);
        _clock.SetRate(2.0);

        _inner.Position = TimeSpan.FromSeconds(5.25);

        Assert.That(_clock.Position, Is.EqualTo(TimeSpan.FromSeconds(5.5)), "a quarter of real time is half of media time");
    }

    [Test]
    public void DoesNotMoveThePositionWhenTheRateChanges()
    {
        _inner.Position = TimeSpan.FromSeconds(5);
        _clock.SetRate(4.0);
        _inner.Position = TimeSpan.FromSeconds(5.5);
        TimeSpan before = _clock.Position;

        _clock.SetRate(1.0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(before, Is.EqualTo(TimeSpan.FromSeconds(7)));
            Assert.That(_clock.Position, Is.EqualTo(before), "the rate changes what follows, not what is");
        }
    }

    [Test]
    public void MeasuresFromAPositionItIsPutAt()
    {
        _inner.Position = TimeSpan.FromSeconds(5);
        _clock.SetRate(4.0);
        _inner.Position = TimeSpan.FromSeconds(6);

        // A seek, an anchor or a correction lands exactly where it is put, whatever the rate was before it.
        _clock.SetPosition(TimeSpan.FromSeconds(100));
        _inner.Position = TimeSpan.FromSeconds(100.5);

        Assert.That(_clock.Position, Is.EqualTo(TimeSpan.FromSeconds(102)), "half a second at four times is two seconds");
    }

    [Test]
    public void StopsWhenTheClockItWrapsStops()
    {
        _inner.Position = TimeSpan.FromSeconds(5);
        _clock.SetRate(3.0);
        _clock.Start();
        _inner.Position = TimeSpan.FromSeconds(6);
        _clock.Pause();

        Assert.That(_clock.IsRunning, Is.False);
        Assert.That(_clock.Position, Is.EqualTo(TimeSpan.FromSeconds(8)), "a paused clock measures nothing further");
    }

    [Test]
    public void TreatsAnUnusableRateAsOne()
    {
        _inner.Position = TimeSpan.FromSeconds(5);

        foreach (double rate in new[] { 0.0, -2.0, double.NaN, double.PositiveInfinity })
        {
            _clock.SetRate(rate);
            Assert.That(_clock.Rate, Is.EqualTo(1.0), $"a rate of {rate} cannot be honoured");
        }

        _inner.Position = TimeSpan.FromSeconds(6);
        Assert.That(_clock.Position, Is.EqualTo(TimeSpan.FromSeconds(6)));
    }

    /// <summary>A clock that only moves when a test says so, so no assertion depends on how fast the machine is.</summary>
    private sealed class HandClock : IPlaybackClock
    {
        public TimeSpan Position { get; set; }

        public bool IsRunning { get; private set; }

        public void Start()
        {
            IsRunning = true;
        }

        public void Pause()
        {
            IsRunning = false;
        }

        public void SetPosition(TimeSpan position)
        {
            Position = position;
        }
    }
}
