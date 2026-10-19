// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Text;

namespace SinePlayer.Common.Expression.Filter.Tokens;

class RawNameTokenExtra(string name) : ITokenExtra
{
    public readonly string Name = name;

    public void ToString(StringBuilder stringBuilder)
    {
        stringBuilder.Append(Name);
    }
}
