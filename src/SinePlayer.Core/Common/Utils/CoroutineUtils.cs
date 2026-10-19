// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Microsoft.UI.Dispatching;

using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Threading;

namespace SinePlayer.Core.Common.Utils;

public static class CoroutineUtils
{
    public static void Run(Func<Task> task)
    {
        task().ContinueWith(t =>
        {
            AggregateException? ex = t.Exception;
            if (ex is not null)
            {
                DebugUtils.CaptureFatalError("An unknown error occurred in CoroutineUtils#Run.", ex);
            }
        });
    }

    public static void RunInMainThread(Action action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
    {
        Run(() => MainThreadUtils.RunInMainThread(action, priority));
    }

    public static void PostInMainThread(Action action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
    {
        Run(() => MainThreadUtils.PostInMainThread(action, priority));
    }

    public static void RunInMainThreadAsync(Func<Task> action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
    {
        Run(() => MainThreadUtils.RunInMainThreadAsync(action, priority));
    }

    public static void PostInMainThreadAsync(Func<Task> action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
    {
        Run(() => MainThreadUtils.PostInMainThreadAsync(action, priority));
    }
}
