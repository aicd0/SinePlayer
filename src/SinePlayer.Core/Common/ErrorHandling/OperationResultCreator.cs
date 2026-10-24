// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Common.ErrorHandling;

internal readonly ref struct OperationResultCreator<T>
{
    public OperationResultCreator() { }

    public OperationResult<T> Success(T result)
    {
        return new OperationResult<T>(result)
        {
            Message = string.Empty,
            Exception = null,
            IsFatal = false,
        };
    }

    public OperationResult<T> Failure(BaseOperationResult result)
    {
        return Failure(result.Message, result.Exception, result.IsFatal);
    }

    public OperationResult<T> Failure(Exception exception, bool isFatal = false)
    {
        return Failure(exception.Message, exception, isFatal);
    }

    public OperationResult<T> Failure(string message, Exception? exception = null, bool isFatal = false)
    {
        return new OperationResult<T>()
        {
            Message = message,
            Exception = exception,
            IsFatal = isFatal,
        };
    }
}