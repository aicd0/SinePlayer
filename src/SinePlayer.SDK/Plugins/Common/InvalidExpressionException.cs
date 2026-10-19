// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.SDK.Plugins.Common;

public class InvalidExpressionException : Exception
{
    public InvalidExpressionException() : base() { }

    public InvalidExpressionException(string message) : base(message) { }

    public InvalidExpressionException(string message, Exception innerException) : base(message, innerException) { }
}
