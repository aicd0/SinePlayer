// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Database.SqlHelpers;

public class BooleanCondition : ICondition
{
    private readonly bool _value;

    public BooleanCondition(bool value)
    {
        _value = value;
    }

    string ICondition.GetExpression(ICommandContext command)
    {
        return _value ? "TRUE" : "FALSE";
    }
}
