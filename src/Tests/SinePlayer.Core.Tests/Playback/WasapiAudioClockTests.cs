// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;

using SinePlayer.Core.Playback.Backends.Wasapi;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// What the clock does when its device fails. Two rules are pinned here, and they are the whole of the class's
/// error handling: a reader never gets an exception (the host's position timer and every frame the picture
/// judges read this clock), and the failure is kept **exactly as it was thrown** — same instance, same message,
/// same stack — because only the layer that failed knows what happened. The first failure is the one kept:
/// everything after it is a consequence, and replacing the origin with it would hide the cause.
/// </summary>
[TestFixture]
public class WasapiAudioClockTests
{
    private const ulong Frequency = TimeSpan.TicksPerSecond;

    [Test]
    public void KeepsTheFirstFailureExactlyAsThrown()
    {
        FakeClockDevice device = new();
        WasapiAudioClock clock = new(device, Frequency);

        device.Advance(Frequency / 2);
        Assert.That(clock.Position, Is.EqualTo(TimeSpan.FromMilliseconds(500)), "the device's travel is the clock's progress");

        InvalidOperationException first = new("the endpoint went away");
        device.NextFailure = first;

        Assert.That(clock.Position, Is.EqualTo(TimeSpan.FromMilliseconds(500)), "a clock that cannot read its device keeps the position it had");
        Assert.That(clock.DeviceLost, Is.True);
        Assert.That(clock.Cause, Is.SameAs(first), "the failure is kept as it was thrown, not described");

        // A second failure, reported the way the renderer reports one: the first is the origin, and everything
        // after it is a consequence of the same device being unusable.
        InvalidOperationException later = new("something else");
        clock.ReportDeviceLost(later);

        Assert.That(clock.Cause, Is.SameAs(first), "the first failure is the origin; a later one does not replace it");
    }

    [Test]
    public void DoesNotLetAFailingStartOrStopEscape()
    {
        FakeClockDevice device = new();
        WasapiAudioClock clock = new(device, Frequency);

        InvalidOperationException refused = new("the device will not start");
        device.NextFailure = refused;

        Assert.DoesNotThrow(clock.Start);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(clock.IsRunning, Is.False, "a stream that did not start is not running");
            Assert.That(clock.DeviceLost, Is.True);
            Assert.That(clock.Cause, Is.SameAs(refused));
        }

        FakeClockDevice running = new();
        WasapiAudioClock started = new(running, Frequency);
        started.Start();
        Assert.That(started.IsRunning, Is.True);

        InvalidOperationException stuck = new("the device will not stop");
        running.NextFailure = stuck;

        Assert.DoesNotThrow(started.Pause);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(started.IsRunning, Is.True, "whether the stream stopped is not known once the stop failed, so nothing is claimed");
            Assert.That(started.DeviceLost, Is.True);
            Assert.That(started.Cause, Is.SameAs(stuck));
        }
    }

    [Test]
    public void ReadsAsThePositionItWasLastPutAtOnceTheDeviceIsLost()
    {
        FakeClockDevice device = new();
        WasapiAudioClock clock = new(device, Frequency);

        device.NextFailure = new InvalidOperationException("the endpoint went away");
        _ = clock.Position;

        var placed = TimeSpan.FromSeconds(42);
        clock.SetPosition(placed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clock.Position, Is.EqualTo(placed), "time has stopped where it was last put, which is what a lost device means");
            Assert.That(clock.Position, Is.EqualTo(placed), "and it does not move on its own");
        }

        Assert.DoesNotThrow(() => clock.SetRate(1.5), "retiming a clock whose device is gone is not an event");
    }

    /// <summary>The device as the clock sees it, with a failure the test can arm and inspect.</summary>
    private sealed class FakeClockDevice : IWasapiClockDevice
    {
        private ulong _position;

        /// <summary>Thrown by the next device call that touches the endpoint, and kept by reference.</summary>
        internal Exception? NextFailure { get; set; }

        public ulong Position
        {
            get
            {
                ThrowIfArmed();
                return _position;
            }
        }

        internal void Advance(ulong by)
        {
            _position += by;
        }

        public void Start()
        {
            ThrowIfArmed();
        }

        public void Stop()
        {
            ThrowIfArmed();
        }

        private void ThrowIfArmed()
        {
            if (NextFailure is Exception failure)
            {
                throw failure;
            }
        }
    }
}
