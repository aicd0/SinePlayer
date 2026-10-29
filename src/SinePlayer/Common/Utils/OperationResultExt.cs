// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;

using SinePlayer.Common.Actions;
using SinePlayer.Common.Actions.Providers;
using SinePlayer.Common.Localization;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.ErrorHandling;

namespace SinePlayer.Common.Utils;

internal static class OperationResultExt
{
    public static OperationResult<T> Success<T>(T result)
    {
        return new OperationResult<T>(result)
        {
            Message = string.Empty,
            Exception = null,
            IsFatal = false,
        };
    }

    public static OperationResult Failure(string tag, Exception exception, bool isFatal = false)
    {
        return Failure(tag, exception.Message, exception, isFatal);
    }

    public static OperationResult<T> Failure<T>(string tag, Exception exception, bool isFatal = false)
    {
        return Failure<T>(tag, exception.Message, exception, isFatal);
    }

    public static OperationResult Failure(string tag, string message, Exception? exception = null, bool isFatal = false)
    {
        Log(tag, message, exception, isFatal);
        return OperationResult.Failure(message, exception, isFatal);
    }

    public static OperationResult<T> Failure<T>(string tag, string message, Exception? exception = null, bool isFatal = false)
    {
        Log(tag, message, exception, isFatal);
        return new OperationResult<T>()
        {
            Message = message,
            Exception = exception,
            IsFatal = isFatal,
        };
    }

    public static OperationResult Failure(BaseOperationResult inner)
    {
        return OperationResult.Failure(inner);
    }

    public static OperationResult<T> Failure<T>(BaseOperationResult inner)
    {
        return new OperationResult<T>()
        {
            Message = inner.Message,
            Exception = inner.Exception,
            IsFatal = inner.IsFatal,
        };
    }

    public static OperationResult Failure(string tag, string context, BaseOperationResult inner)
    {
        return Failure(tag, $"{context}: {inner.Message}", inner.Exception, inner.IsFatal);
    }

    public static void ShowErrorMessage(this BaseOperationResult result, ActionHandler actionHandler)
    {
        if (result.IsSuccessful)
        {
            return;
        }

        ActionModel actionModel = ActionModel.Builder.Create(MessageDialogProvider.NAME)
            .AddParameter(MessageDialogProvider.PARAM_TITLE, StringResourceProvider.Instance.Error)
            .AddParameter(MessageDialogProvider.PARAM_MESSAGE, StringResourceProvider.Instance.ErrorCommonDescription + "\n" + result.ErrorMessage)
            .Build();
        actionHandler.HandleNoResult(actionModel);
    }

    private static void Log(string tag, string message, Exception? exception, bool isFatal)
    {
        if (isFatal)
        {
            Logger.F(tag, message, exception);
        }
        else
        {
            Logger.E(tag, message, exception);
        }
    }
}
