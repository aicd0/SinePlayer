// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Database.SqlHelpers;

public class NotCondition : ICondition
{
    private readonly ICondition _condition;

    public NotCondition(ICondition condition)
    {
        _condition = condition;
    }

    string ICondition.GetExpression(ICommandContext command)
    {
        return $"NOT ({_condition.GetExpression(command)})";
    }
}
