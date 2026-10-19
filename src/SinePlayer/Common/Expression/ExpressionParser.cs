// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;

namespace SinePlayer.Common.Expression;

internal class ExpressionParser
{
    public static Filter.ExpressionToken ParseFilter(string expression)
    {
        LinkedList<Filter.ExpressionToken> tokens = [];
        Filter.Parser.Tokenizer.Tokenize(expression, tokens);
        Filter.ExpressionToken token = Filter.Parser.Evaluator.Evaluate(tokens);
        token = Filter.Parser.Optimizer.Optimize(token);
        return token;
    }

    public static List<Search.ExpressionToken> ParseSearch(string expression)
    {
        LinkedList<Search.ExpressionToken> tokens = [];
        Search.Parser.Tokenizer.Tokenize(expression, tokens);
        Search.Parser.Evaluator.Evaluate(tokens);
        return [.. tokens];
    }
}
