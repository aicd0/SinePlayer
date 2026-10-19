// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Text;

namespace SinePlayer.Common.Expression.Search.Tokens;

internal class IntermediateFilterTokenExtra(string key, string value) : ITokenExtra
{
    public readonly string Key = key;
    public readonly string Value = value;

    public void ToString(StringBuilder stringBuilder)
    {
        stringBuilder.Append(Key).Append(':').Append(Value);
    }
}
