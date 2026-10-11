// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.Common.Expression;
using SinePlayerUWP.Common.Expression.Search.Sql;
using SinePlayerUWP.Core.Database.SqlHelpers;
using SinePlayerUWP.Data.Tables;

namespace SinePlayerUWP.Helpers.Search;

internal class MediaSearchSQLProvider(Common.Expression.Filter.Sql.ISQLCommandProvider expProvider) : ISQLCommandProvider
{
    private readonly Common.Expression.Filter.Sql.ISQLCommandProvider _expProvider = expProvider;

    public bool IsHiddenFilter(string key)
    {
        return key == MediaSQLProviderUtils.VAR_HIDDEN;
    }

    public ICondition CreateNotHiddenCondition()
    {
        return new ComparisonCondition(ColumnOrValue.FromColumn(MediaTable.ColumnHidden), ColumnOrValue.FromValue(false));
    }

    public ICondition CreateFilterCondition(string key, string value)
    {
        switch (key)
        {
            case MediaSQLProviderUtils.VAR_EXP:
                {
                    Common.Expression.Filter.ExpressionToken token;
                    try
                    {
                        token = ExpressionParser.ParseFilter(value);
                    }
                    catch (ExpressionException)
                    {
                        return new BooleanCondition(false);
                    }

                    ICondition condition;
                    try
                    {
                        condition = Common.Expression.Filter.Sql.SQLGenerator.CreateQuery(token, _expProvider);
                    }
                    catch (ExpressionException)
                    {
                        return new BooleanCondition(false);
                    }

                    return condition;
                }

            case MediaSQLProviderUtils.VAR_TAG:
                return MediaSQLProviderUtils.CreateTagCondition(new ComparisonCondition(ColumnOrValue.FromColumn(MediaTagTable.ColumnContent), ColumnOrValue.FromValue(value)));

            case MediaSQLProviderUtils.VAR_HIDDEN:
                return value switch
                {
                    "0" => new ComparisonCondition(ColumnOrValue.FromColumn(MediaTable.ColumnHidden), ColumnOrValue.FromValue(false)),
                    "1" => new ComparisonCondition(ColumnOrValue.FromColumn(MediaTable.ColumnHidden), ColumnOrValue.FromValue(true)),
                    _ => new BooleanCondition(false),
                };

            default:
                return MediaSQLProviderUtils.CreateTagInTagCategoryCondition(key, new ComparisonCondition(ColumnOrValue.FromColumn(MediaTagTable.ColumnContent), ColumnOrValue.FromValue(value)));
        }
    }
}
