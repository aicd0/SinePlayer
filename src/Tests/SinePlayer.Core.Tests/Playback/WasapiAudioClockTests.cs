// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Backends.Wasapi;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// What the clock does when its device fails. Two rules are pinned here, and they are the whole of the class's
/// error handling: a **reader** never gets an exception (the host's position timer and every frame the picture
/// judges read this clock), while a **command** to the device throws once — after the clock has already latched the
/// failure, so it keeps answering from where it stands. The failure is kept **exactly as it was thrown** — same
/// instance, same message, same stack — because only the layer that failed knows what happened. The first failure
/// is the one kept: everything after it is a consequence, and replacing the origin with it would hide the cause.
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
        Assert.That(clock.Error, Is.SameAs(first), "the failure is kept as it was thrown, not described");

        // A second failure, reported the way the renderer reports one: the first is the origin, and everything
        // after it is a consequence of the same device being unusable.
        InvalidOperationException later = new("something else");
        clock.ReportDeviceLost(later);

        Assert.That(clock.Error, Is.SameAs(first), "the first failure is the origin; a later one does not replace it");
    }

    /// <summary>
    /// A command to the device that fails is not a secret, but a read is: the pipeline catches the one, and no
    /// reader ever catches the other.
    /// </summary>
    [Test]
    public void RaisesAFailingStartOrStopAndKeepsTheFailure()
    {
        FakeClockDevice device = new();
        WasapiAudioClock clock = new(device, Frequency);

        InvalidOperationException refused = new("the device will not start");
        device.NextFailure = refused;

        InvalidOperationException? thrown = Assert.Throws<InvalidOperationException>(clock.Start);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.SameAs(refused), "what travels is the device's own exception, not a description of it");
            Assert.That(clock.IsRunning, Is.False, "a stream that did not start is not running");
            Assert.That(clock.DeviceLost, Is.True, "and the clock has stopped with it, for whoever reads it");
            Assert.That(clock.Error, Is.SameAs(refused));
        }

        Assert.That(clock.Position, Is.EqualTo(TimeSpan.Zero), "a lost clock still answers, which is the half that must not throw");

        FakeClockDevice running = new();
        WasapiAudioClock started = new(running, Frequency);
        started.Start();
        Assert.That(started.IsRunning, Is.True);

        InvalidOperationException stuck = new("the device will not stop");
        running.NextFailure = stuck;

        InvalidOperationException? stuckThrown = Assert.Throws<InvalidOperationException>(started.Pause);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stuckThrown, Is.SameAs(stuck));
            Assert.That(started.IsRunning, Is.True, "whether the stream stopped is not known once the stop failed, so nothing is claimed");
            Assert.That(started.DeviceLost, Is.True);
            Assert.That(started.Error, Is.SameAs(stuck));
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
    }

    /// <summary>The device as the clock sees it, with a failure the test can arm and inspect.</summary>
    [Test]
    public void PutsWhatTheDeviceNeedsInPlaceBeforeEveryStart()
    {
        // A paused stream keeps what the device already holds, and starting it plays that: whatever has to be true of
        // the sound before it is heard — the level — has to happen before the device runs, every time it is started
        // again, not only the first.
        FakeClockDevice device = new();
        int levelApplied = 0;
        WasapiAudioClock clock = new(device, Frequency, () => levelApplied++);

        clock.Start();
        clock.Pause();
        clock.Start();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clock.IsRunning, Is.True);
            Assert.That(levelApplied, Is.EqualTo(2), "once before each start, and a pause does not apply it again");
        }
    }

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
