// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class CoroutineHost
{
    private readonly IReadOnlyList<Func<Func<bool>, Task>> _coroutines;
    private readonly PipelineThread _thread;

    public CoroutineHost(string name, IReadOnlyList<Func<Func<bool>, Task>> coroutines)
    {
        _coroutines = coroutines;
        _thread = new PipelineThread(name, Run);
    }

    public void Start()
    {
        _thread.Start();
    }

    public void RequestStop()
    {
        _thread.RequestStop();
    }

    public void Join()
    {
        _thread.Join();
    }

    private void Run(Func<bool> stopRequested)
    {
        SingleThreadSynchronizationContext context = new();
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            async Task AsyncCore()
            {
                try
                {
                    Task[] tasks = [.. _coroutines.Select(coroutine => coroutine(stopRequested))];
                    await Task.WhenAll(tasks);
                }
                finally
                {
                    context.Complete();
                }
            }

            Task task = AsyncCore();
            context.RunOnCurrentThread();
            task.Wait();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            context.Dispose();
        }
    }
}
