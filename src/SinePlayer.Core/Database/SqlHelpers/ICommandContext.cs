// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Core.Database.SqlHelpers;

internal interface ICommandContext
{
    string AppendParameter(object value);
}
