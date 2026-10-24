// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Text.RegularExpressions;

namespace SinePlayer.Core.Common.ErrorHandling;

public abstract partial class BaseOperationResult(bool isSuccessful)
{
    public bool IsSuccessful { get; } = isSuccessful;
    public required string Message { get; init; }
    public required Exception? Exception { get; init; }
    public required bool IsFatal { get; init; }

    public string DetailedErrorMessage
    {
        get
        {
            if (IsSuccessful)
            {
                return string.Empty;
            }

            return GetErrorMessage();
        }
    }

    private string GetErrorMessage()
    {
        string commonMessage = NormalizeMessage(Message);
        string exceptionMessage = NormalizeMessage(Exception?.Message);

        string message;
        if (string.IsNullOrEmpty(commonMessage))
        {
            message = exceptionMessage;
        }
        else if (string.IsNullOrEmpty(exceptionMessage))
        {
            message = commonMessage;
        }
        else
        {
            message = $"{commonMessage}\n{exceptionMessage}";
        }

        if (string.IsNullOrEmpty(message))
        {
            message = "(no message)";
        }

        return message;
    }

    private static string NormalizeMessage(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        input = input.Trim('\r', '\n');
        return LineBreakRegex().Replace(input, " ");
    }

    [GeneratedRegex(@"[\r\n]+")]
    private static partial Regex LineBreakRegex();
}
