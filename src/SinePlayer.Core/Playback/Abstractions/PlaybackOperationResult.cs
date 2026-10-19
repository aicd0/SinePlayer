// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions;

public sealed class PlaybackOperationResult
{
    private PlaybackOperationResult(bool isSuccessful, string message, Exception? exception)
    {
        IsSuccessful = isSuccessful;
        Message = message;
        Exception = exception;
    }

    public static PlaybackOperationResult Success { get; } = new(true, string.Empty, null);

    public bool IsSuccessful { get; }

    public string Message { get; }

    public Exception? Exception { get; }

    public static PlaybackOperationResult Failure(string message, Exception? exception = null)
    {
        return new PlaybackOperationResult(false, message, exception);
    }

    public static PlaybackOperationResult Failure(Exception exception)
    {
        return new PlaybackOperationResult(false, exception.Message, exception);
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
