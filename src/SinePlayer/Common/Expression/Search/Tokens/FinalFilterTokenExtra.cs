// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Text;

namespace SinePlayer.Common.Expression.Search.Tokens;

internal class FinalFilterTokenExtra(string key, string value, bool inverse) : ITokenExtra
{
    public readonly string Key = key;
    public readonly string Value = value;
    public readonly bool Inverse = inverse;

    public void ToString(StringBuilder stringBuilder)
    {
        if (Inverse)
        {
            stringBuilder.Append('-');
        }

        stringBuilder.Append(Key).Append(':').Append(Value);
    }
}
