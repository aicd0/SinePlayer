// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Concurrent;

using SinePlayer.Core.Common.Threading;

namespace SinePlayer.Core.Tests.Common;

/// <summary>
/// The one property the pipeline leans on instead of a lock: a queue dispatcher runs one command at a time. Every
/// publish, every reset and every version number taken from the slots assumes it — two commands running together
/// would let two publications take the same number, and "a decoder and its renderer are one generation" would stop
/// meaning anything. It is pinned here rather than left to a comment because nothing else in the suite would notice.
/// </summary>
[TestFixture]
public class TaskDispatcherTests
{
    /// <summary>
    /// Commands submitted from several threads at once never overlap, including the ones that suspend before they
    /// finish: a command that yields hands the thread back, and the next one must still wait for it.
    /// </summary>
    [Test]
    public async Task RunsOneCommandAtATime()
    {
        ITaskDispatcher queue = TaskDispatcher.Factory.NewQueue("Tests - Queue");

        const int submitters = 4;
        const int commandsEach = 25;

        using CountdownEvent startingLine = new(1);
        ConcurrentBag<Task> submitted = [];
        int active = 0;
        int overlaps = 0;
        int ran = 0;

        Task[] senders =
        [
            .. Enumerable.Range(0, submitters).Select(_ => Task.Run(async () =>
            {
                startingLine.Wait();
                for (int i = 0; i < commandsEach; i++)
                {
                    submitted.Add(queue.SubmitAsync(async () =>
                    {
                        if (Interlocked.Increment(ref active) != 1)
                        {
                            Interlocked.Increment(ref overlaps);
                        }

                        // A command that does not finish where it started: the continuation comes back on whatever
                        // thread the pool gives it, which is where a dispatcher that merely posted each command
                        // separately would let a second one in.
                        await Task.Yield();
                        Thread.Sleep(1);

                        Interlocked.Increment(ref ran);
                        Interlocked.Decrement(ref active);
                    }));
                }

                await Task.CompletedTask;
            }))
        ];

        startingLine.Signal();
        await Task.WhenAll(senders);
        await Task.WhenAll(submitted);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ran, Is.EqualTo(submitters * commandsEach), "every command ran");
            Assert.That(overlaps, Is.Zero, "a command started while another was still running");
        }
    }
}
