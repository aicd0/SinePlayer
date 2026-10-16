// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using SinePlayer.Core.Playback.Internal;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The seqlock the position change is published through: a value read without a lock, and the work that
/// travels with it — the reset that has to be done before a thread may act on the new value.
/// <para>
/// What the tests here are about is the one guarantee the class exists for: a reader that accepts a value
/// accepts a change that has <em>finished</em>. The rest of the protocol (three reads, the retry, the wait for
/// a change that outlasts the attempts) is only the means, and is covered by the two readings of the same
/// guarantee below — the reader that races a changing writer, and the reader that meets a change which is
/// slow on purpose.
/// </para>
/// </summary>
[TestFixture]
public class SeqLockTests
{
    /// <summary>How much work the writer puts inside the change, so that readers have a window to race.</summary>
    private const int ChangeWork = 2000;

    /// <summary>How long the slow change sleeps, and the least a reader that meets it has to have waited.</summary>
    private const int SlowChangeMilliseconds = 500;
    private const int WaitedMilliseconds = 200;

    [Test]
    public void HandsOutTheValueOfTheLastChange()
    {
        SeqLock<long> target = new(0);
        long handedToWork = -1;

        Assert.That(target.Read(), Is.Zero, "nothing has been changed yet");

        target.Change(value => value + 1, value => handedToWork = value);
        target.Change(value => value + 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(target.Read(), Is.EqualTo(2));
            Assert.That(handedToWork, Is.EqualTo(1), "the work of a change is told which value it belongs to");
        }
    }

    /// <summary>
    /// The generic half of the class: a value of several fields is read as one fact, so a reader never sees one
    /// field of one change beside a field of another — the two halves below are only ever changed together.
    /// </summary>
    [Test]
    public void HandsOutACompoundValueAsOneFact()
    {
        SeqLock<(int Left, int Right)> target = new((0, 0));
        const int changes = 2000;

        int violations = 0;
        Thread writer = new(() =>
        {
            for (int index = 0; index < changes; index++)
            {
                target.Change(value => (value.Left + 1, value.Left + 1), _ => Thread.SpinWait(ChangeWork));
            }
        });

        List<Thread> readers = new();
        for (int index = 0; index < 4; index++)
        {
            Thread reader = new(() =>
            {
                while (true)
                {
                    (int left, int right) = target.Read();
                    if (left != right)
                    {
                        Interlocked.Increment(ref violations);
                    }
                    if (left >= changes)
                    {
                        return;
                    }
                }
            });
            readers.Add(reader);
        }

        writer.Start();
        foreach (Thread reader in readers)
        {
            reader.Start();
        }

        Assert.That(writer.Join(TimeSpan.FromSeconds(30)), Is.True, "the writer did not finish");
        foreach (Thread reader in readers)
        {
            Assert.That(reader.Join(TimeSpan.FromSeconds(30)), Is.True, "a reader did not finish");
        }

        Assert.That(violations, Is.Zero, "a reader took the two halves of different changes");
    }

    /// <summary>
    /// The guarantee, raced: four readers read while the writer changes the value two thousand times, and a
    /// reader that takes a value must find the change that produced it finished — the change publishes the
    /// last value it finished, so a reader seeing a newer one has taken a half-written change.
    /// </summary>
    [Test]
    public void NeverLetsAReaderActOnAChangeThatIsStillRunning()
    {
        SeqLock<long> target = new(0);
        const int changes = 2000;

        long completed = 0;
        int violations = 0;

        Thread writer = new(() =>
        {
            for (int index = 0; index < changes; index++)
            {
                target.Change(value => value + 1, value =>
                {
                    Thread.SpinWait(ChangeWork);
                    Volatile.Write(ref completed, value);
                });
            }
        });

        List<Thread> readers = new();
        for (int index = 0; index < 4; index++)
        {
            Thread reader = new(() =>
            {
                while (true)
                {
                    long value = target.Read();
                    if (Volatile.Read(ref completed) < value)
                    {
                        Interlocked.Increment(ref violations);
                    }
                    if (value >= changes)
                    {
                        return;
                    }
                }
            });
            readers.Add(reader);
        }

        writer.Start();
        foreach (Thread reader in readers)
        {
            reader.Start();
        }

        Assert.That(writer.Join(TimeSpan.FromSeconds(30)), Is.True, "the writer did not finish");
        foreach (Thread reader in readers)
        {
            Assert.That(reader.Join(TimeSpan.FromSeconds(30)), Is.True, "a reader did not finish");
        }

        Assert.That(violations, Is.Zero, "a reader acted on a change that had not finished");
    }

    /// <summary>
    /// A change slow enough to outlast the attempts: the reader waits for it rather than taking the value
    /// while it is still being made, so what it returns is the finished change's value and not a half of it.
    /// </summary>
    [Test]
    public void WaitsWhenAChangeOutlastsTheAttempts()
    {
        SeqLock<long> target = new(0);
        using SemaphoreSlim inside = new(0, 1);

        Thread writer = new(() =>
        {
            target.Change(value => value + 1, _ =>
            {
                inside.Release();
                Thread.Sleep(SlowChangeMilliseconds);
            });
        });
        writer.Start();

        Assert.That(inside.Wait(TimeSpan.FromSeconds(10)), Is.True, "the change never started");

        var watch = Stopwatch.StartNew();
        long value = target.Read();
        watch.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(value, Is.EqualTo(1), "the read takes the finished value");
            Assert.That(watch.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(WaitedMilliseconds), "the read waited for the change instead of taking it half-made");
        }

        Assert.That(writer.Join(TimeSpan.FromSeconds(10)), Is.True, "the writer did not finish");
    }
}
