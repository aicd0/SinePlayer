// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class CoroutineHost
{
    private readonly Lock _lock = new();
    private readonly List<PipelineCoroutine> _coroutines = [];
    private readonly PipelineThread _thread;

    private SingleThreadSynchronizationContext? _context;
    private int _outstanding;
    private bool _closing;
    private Exception? _failure;

    public CoroutineHost(string name)
    {
        _thread = new PipelineThread(name, Run);
    }

    public void Start()
    {
        _thread.Start();
    }

    public PipelineCoroutine Register(Func<Func<bool>, Task> body)
    {
        PipelineCoroutine coroutine = new(body);

        SingleThreadSynchronizationContext? running;
        lock (_lock)
        {
            _coroutines.Add(coroutine);
            _outstanding++;
            running = _context;
        }

        running?.Post(_ => Begin(coroutine, () => _thread.StopRequested), null);
        return coroutine;
    }

    public void RequestStop()
    {
        SingleThreadSynchronizationContext? context;
        bool finished;
        lock (_lock)
        {
            _closing = true;
            foreach (PipelineCoroutine coroutine in _coroutines)
            {
                coroutine.RequestStop();
            }

            finished = _outstanding == 0;
            context = _context;
        }

        if (finished)
        {
            context?.Complete();
        }
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

        PipelineCoroutine[] initial;
        lock (_lock)
        {
            _context = context;
            initial = [.. _coroutines];
        }

        try
        {
            foreach (PipelineCoroutine coroutine in initial)
            {
                Begin(coroutine, stopRequested);
            }

            context.RunOnCurrentThread();

            if (_failure is { } failure)
            {
                throw failure;
            }
        }
        finally
        {
            lock (_lock)
            {
                _context = null;
            }

            SynchronizationContext.SetSynchronizationContext(previousContext);
            context.Dispose();
        }
    }

    private void Begin(PipelineCoroutine coroutine, Func<bool> stopRequested)
    {
        if (!coroutine.TryBegin())
        {
            return;
        }

        _ = RunCore(coroutine, () => stopRequested() || coroutine.IsStopRequested);
    }

    private async Task RunCore(PipelineCoroutine coroutine, Func<bool> stopRequested)
    {
        try
        {
            await coroutine.Body(stopRequested);
        }
        catch (Exception exception)
        {
            _failure ??= exception;
        }
        finally
        {
            bool finished;
            SingleThreadSynchronizationContext? context;
            lock (_lock)
            {
                _outstanding--;
                finished = _closing && _outstanding == 0;
                context = _context;
            }

            if (finished)
            {
                context?.Complete();
            }
        }
    }
}
