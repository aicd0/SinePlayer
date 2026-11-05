// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Common.Threading;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class PipelineEvents(Action wakeAll)
{
    private const string TAG = nameof(PlaybackPipeline);

    private readonly ITaskDispatcher _playbackQueue = TaskDispatcher.Factory.NewQueue("Pipeline - Playback");

    private volatile MediaEngineState _state = MediaEngineState.None;
    private volatile string? _error;

    public event Action<MediaEngineState, string?>? StateChanged;

    public event Action<string>? WarningRaised;

    public MediaEngineState State => _state;

    public string? Error => _error;

    public void SetState(MediaEngineState state, string? error = null)
    {
        bool changed = _state != state || _error != error;
        _state = state;
        _error = error;

        if (!changed)
        {
            return;
        }

        StateChanged?.Invoke(state, error);
    }

    public void RaiseWarning(string warning)
    {
        PlaybackLogger.W(TAG, warning);
        WarningRaised?.Invoke(warning);
    }

    public void PostWarning(string warning)
    {
        Submit(() => RaiseWarning(warning));
    }

    public void PostFailure(string message, Exception? exception = null)
    {
        Submit(() => Fail(message, exception));
    }

    public void PostFailure(OperationResult result)
    {
        PostFailure(result.Message, result.Exception);
    }

    public OperationResult Fail(string message, Exception? exception = null)
    {
        PlaybackLogger.E(TAG, message, exception);
        SetState(MediaEngineState.Error, message);
        wakeAll();
        return OperationResult.Failure(message, exception);
    }

    public Task Submit(Action work)
    {
        return _playbackQueue.Submit(work);
    }

    public Task<T> Submit<T>(Func<T> work)
    {
        return _playbackQueue.Submit(work);
    }

    public Task<OperationResult> SubmitAndLog(string action, Func<OperationResult> func)
    {
        return Submit(() =>
        {
            OperationResult result = func();
            if (!result.IsSuccessful)
            {
                PlaybackLogger.E(TAG, $"{action}: {result.Message}", result.Exception);
            }

            return result;
        });
    }

    public Task<OperationResult<T>> SubmitAndLog<T>(string action, Func<OperationResult<T>> func)
    {
        return Submit(() =>
        {
            OperationResult<T> result = func();
            if (!result.IsSuccessful)
            {
                PlaybackLogger.E(TAG, $"{action}: {result.Message}", result.Exception);
            }

            return result;
        });
    }

    public static OperationResult SafeInvoke(Func<OperationResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            return OperationResult.Failure(ex);
        }
    }
}
