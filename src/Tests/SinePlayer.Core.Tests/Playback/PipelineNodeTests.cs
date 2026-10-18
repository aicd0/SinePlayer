// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

using SinePlayer.Core.Playback.Internal;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The one place a thread of the pipeline sleeps. What is pinned here is the protocol that makes a wake
/// un-losable — the stamp read before the decision, and a wait that compares against it — together with the
/// two things the rest of the design leans on: a wake never waits for the woken thread, and a wait with a
/// time on it ends by itself.
/// </summary>
[TestFixture]
public class PipelineNodeTests
{
    [Test]
    public void WakesAThreadThatIsAlreadyWaiting()
    {
        PipelineNode node = new();
        long stamp = node.Stamp;
        int woke = 0;

        Thread waiter = new(() =>
        {
            node.Wait(stamp);
            Volatile.Write(ref woke, 1);
        })
        {
            IsBackground = true,
        };

        waiter.Start();
        Assert.That(waiter.Join(TimeSpan.FromMilliseconds(100)), Is.False, "nothing has woken it yet");

        node.Wake();
        Assert.That(waiter.Join(TimeSpan.FromSeconds(2)), Is.True, "a wake has to release the wait");
        Assert.That(Volatile.Read(ref woke), Is.EqualTo(1));
    }

    /// <summary>
    /// The window the whole design rests on: a thread reads the stamp, then looks at the facts, and only
    /// then decides there is nothing to do. A wake that lands in the middle of that has to end the wait at
    /// once rather than being slept through until the next one.
    /// </summary>
    [Test]
    public void DoesNotSleepWhenAWakeArrivedBeforeTheWait()
    {
        PipelineNode node = new();
        long stamp = node.Stamp;
        node.Wake();

        var watch = Stopwatch.StartNew();
        node.Wait(stamp, 5000);
        watch.Stop();

        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)),
            "a wake that arrives between the read and the wait is not lost");
    }

    [Test]
    public void TimesOutWithoutAWake()
    {
        PipelineNode node = new();

        var watch = Stopwatch.StartNew();
        node.Wait(node.Stamp, 50);
        watch.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(watch.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(20)), "a wait with a time on it actually waits");
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)), "and it ends by itself, without a wake");
        }
    }

    /// <summary>
    /// A wake is a note, not a rendezvous: the waker must not end up waiting for whatever the woken thread
    /// does next. That is what lets a media thread wake another one from inside its own loop.
    /// </summary>
    [Test]
    public void DoesNotWaitForTheWokenThreadToDoAnything()
    {
        PipelineNode node = new();
        bool done = false;

        Thread busy = new(() =>
        {
            while (!Volatile.Read(ref done))
            {
                node.Wait(node.Stamp);
                Thread.Sleep(500);
            }
        })
        {
            IsBackground = true,
        };

        busy.Start();
        Thread.Sleep(100);

        var watch = Stopwatch.StartNew();
        node.Wake();
        watch.Stop();

        Volatile.Write(ref done, true);
        node.Wake();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(200)), "a wake hands over and returns");
            Assert.That(busy.Join(TimeSpan.FromSeconds(3)), Is.True, "and the thread can still be released at the end");
        }
    }

    [Test]
    public void RemembersEveryWakeItWasGiven()
    {
        PipelineNode node = new();
        long before = node.Stamp;

        node.Wake();
        node.Wake();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Stamp, Is.EqualTo(before + 2), "every wake moves the stamp the waiter compares against");
            Assert.That(node.WakeCount, Is.EqualTo(2), "and it is counted, which is how a parked pipeline proves it is not polling");
        }
    }
}
