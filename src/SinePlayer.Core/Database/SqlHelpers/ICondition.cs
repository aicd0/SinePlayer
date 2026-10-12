// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Core.Database.SqlHelpers;

public interface ICondition
{
    internal string GetExpression(ICommandContext command);
}
