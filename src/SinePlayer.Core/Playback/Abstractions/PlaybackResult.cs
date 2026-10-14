// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;

namespace SinePlayer.Core.Playback.Abstractions;

/// <summary>
/// The outcome of a playback operation. Engine stages report failures through this type instead of
/// throwing, so that a component which cannot handle a stream can be replaced or skipped without
/// unwinding the pipeline.
/// </summary>
public sealed class PlaybackResult
{
    private PlaybackResult(bool isSuccessful, string message, Exception? exception)
    {
        IsSuccessful = isSuccessful;
        Message = message;
        Exception = exception;
    }

    public static PlaybackResult Success { get; } = new(true, string.Empty, null);

    public bool IsSuccessful { get; }

    public string Message { get; }

    public Exception? Exception { get; }

    public static PlaybackResult Failure(string message, Exception? exception = null)
    {
        return new PlaybackResult(false, message, exception);
    }

    public static PlaybackResult Failure(Exception exception)
    {
        return new PlaybackResult(false, exception.Message, exception);
    }

    public override string ToString()
    {
        if (IsSuccessful)
        {
            return "Success";
        }

        return Exception is null ? Message : $"{Message} ({Exception.Message})";
    }
}
