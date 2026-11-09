// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Threading.Tasks;

namespace SinePlayer.Views.Pages.Player;

internal sealed class SimpleTaskQueue
{
    private Task _tail = Task.CompletedTask;

    public Task Enqueue(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        Task previous = _tail;
        Task next = RunAsync(previous, work);
        _tail = next;
        return next;
    }

    private static async Task RunAsync(Task previous, Func<Task> work)
    {
        try
        {
            await previous;
        }
        catch (Exception)
        {
        }

        await work();
    }
}
