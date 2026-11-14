// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;
using System.Text;

namespace SinePlayer.Common.Expression.Filter.Tokens;

class IntermediateExpressionListTokenExtra(IEnumerable<ExpressionToken> expressions) : ITokenExtra
{
    public readonly ExpressionToken[] Expressions = [.. expressions];

    public void ToString(StringBuilder stringBuilder)
    {
        stringBuilder.Append('(');
        for (int i = 0; i < Expressions.Length; i++)
        {
            if (i > 0)
            {
                stringBuilder.Append(", ");
            }
            Expressions[i].Extra?.ToString(stringBuilder);
        }
        stringBuilder.Append(')');
    }
}
