// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Common.ErrorHandling;

public sealed partial class OperationResult<T> : BaseOperationResult
{
    private readonly T _result;

    public T Result => IsSuccessful ? _result : throw new InvalidOperationException("Result is not available.");

    public OperationResult() : base(false)
    {
        _result = default!;
    }

    public OperationResult(T result) : base(true)
    {
        _result = result;
    }
}

public sealed class OperationResult(bool isSuccessful) : BaseOperationResult(isSuccessful)
{
    private static readonly OperationResult _success = new(true)
    {
        Message = string.Empty,
        Exception = null,
        IsFatal = false,
    };

    public static OperationResult Success()
    {
        return _success;
    }

    public static OperationResult Failure(BaseOperationResult result)
    {
        return Failure(result.Message, result.Exception, result.IsFatal);
    }

    public static OperationResult Failure(Exception exception, bool isFatal = false)
    {
        return Failure(exception.Message, exception, isFatal);
    }

    public static OperationResult Failure(string message, Exception? exception = null, bool isFatal = false)
    {
        return new OperationResult(false)
        {
            Message = message,
            Exception = exception,
            IsFatal = isFatal,
        };
    }
}
