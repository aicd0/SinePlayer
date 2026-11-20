// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Microsoft.UI.Dispatching;

namespace SinePlayer.Core.Common.Threading;

public static class MainThreadUtils
{
    private static DispatcherQueue? _mainDispatcherQueue = null;

    public static void Initialize(DispatcherQueue dispatcherQueue)
    {
        _mainDispatcherQueue = dispatcherQueue;
    }

    public static void AssertOnMainThread()
    {
        if (!IsMainThread())
        {
            throw new InvalidOperationException("This operation must be performed on the main thread.");
        }
    }

    public static DispatcherQueueTimer CreateTimer()
    {
        DispatcherQueue dispatcher = GetMainThreadDispatcher() ?? throw new InvalidOperationException("Main thread dispatcher is currently unavailable.");
        return dispatcher.CreateTimer();
    }

    public static async Task RunInMainThread(Action action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
    {
        await RunInMainThread(action, priority, true);
    }

    public static async Task RunInMainThreadAsync(Func<Task> func, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
    {
        await RunInMainThreadAsync(async () =>
        {
            await func();
            return true;
        }, priority, true);
    }

    public static async Task<R> RunInMainThreadAsync<R>(Func<Task<R>> func, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
    {
        return await RunInMainThreadAsync(func, priority, true);
    }

    public static async Task PostInMainThread(Action action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
    {
        await RunInMainThread(action, priority, false);
    }

    public static async Task PostInMainThreadAsync(Func<Task> func, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
    {
        await RunInMainThreadAsync(async () =>
        {
            await func();
            return true;
        }, priority, false);
    }

    public static async Task<R> PostInMainThreadAsync<R>(Func<Task<R>> func, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
    {
        return await RunInMainThreadAsync(func, priority, false);
    }

    private static async Task RunInMainThread(Action action, DispatcherQueuePriority priority, bool runImmediatelyIfPossible)
    {
        DispatcherQueue dispatcher = GetMainThreadDispatcher() ?? throw new InvalidOperationException("Main thread dispatcher is currently unavailable.");
        if (runImmediatelyIfPossible && dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        var taskCompletionSource = new TaskCompletionSource<bool>();
        bool success = dispatcher.TryEnqueue(priority, () =>
        {
            try
            {
                action();
                taskCompletionSource.SetResult(true);
            }
            catch (Exception ex)
            {
                taskCompletionSource.SetException(ex);
            }
        });

        if (!success)
        {
            throw new InvalidOperationException("Failed to enqueue the operation");
        }

        await taskCompletionSource.Task;
    }

    private static async Task<R> RunInMainThreadAsync<R>(Func<Task<R>> func, DispatcherQueuePriority priority, bool runImmediatelyIfPossible)
    {
        DispatcherQueue dispatcher = GetMainThreadDispatcher() ?? throw new InvalidOperationException("Main thread dispatcher is currently unavailable.");
        if (runImmediatelyIfPossible && dispatcher.HasThreadAccess)
        {
            return await func();
        }

        var taskCompletionSource = new TaskCompletionSource<R>();
        bool success = dispatcher.TryEnqueue(priority, async () =>
        {
            try
            {
                taskCompletionSource.SetResult(await func());
            }
            catch (Exception ex)
            {
                taskCompletionSource.SetException(ex);
            }
        });

        if (!success)
        {
            throw new InvalidOperationException("Failed to enqueue the operation");
        }

        return await taskCompletionSource.Task;
    }

    public static bool IsMainThread()
    {
        DispatcherQueue? queue = GetMainThreadDispatcher();
        if (queue == null)
        {
            return false;
        }

        return queue.HasThreadAccess;
    }

    private static DispatcherQueue? GetMainThreadDispatcher()
    {
        return _mainDispatcherQueue;
    }
}
