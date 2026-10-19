// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;

namespace SinePlayer.Common.ErrorHandling;

internal interface IErrorLogger
{
    bool IsSuccessful { get; }

    string Message { get; }

    Exception? Exception { get; }

    bool IsFatal { get; }

    IReadOnlyList<IErrorLogger> Children { get; }
}
