// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Database.SqlHelpers;

namespace SinePlayer.Common.Expression.Search.Sql;

internal interface ISQLCommandProvider
{
    bool IsHiddenFilter(string key);

    ICondition CreateNotHiddenCondition();

    ICondition CreateFilterCondition(string key, string value);
}
