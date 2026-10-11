// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.Core.Database.SqlHelpers;
using SinePlayerUWP.Data.Tables;

namespace SinePlayerUWP.Helpers.Search;

internal class ComicSQLProviderUtils
{
    public const string VAR_EXP = "exp";
    public const string VAR_TAG = "tag";
    public const string VAR_TITLE = "title";
    public const string VAR_RATING = "rating";
    public const string VAR_COMPLETION_STATUS = "completion_state";
    public const string VAR_TITLE1 = "title1";
    public const string VAR_TITLE2 = "title2";
    public const string VAR_PROGRESS = "progress";
    public const string VAR_PAGE_COUNT = "page_count";
    public const string VAR_HIDDEN = "hidden";

    public static ICondition CreateTagCondition(ICondition condition)
    {
        var subquery = SelectCommand.Create(MediaTagTable.Instance);
        subquery.AppendCondition(condition);
        subquery.PutQueryInt64(MediaTagTable.ColumnMediaId);
        subquery.Distinct();
        return new InCondition(ColumnOrValue.FromColumn(MediaTable.ColumnId), subquery);
    }

    public static ICondition CreateTagCategoryCondition(string category)
    {
        var subquery = SelectCommand.Create(MediaTagCategoryTable.Instance);
        subquery.AppendCondition(MediaTagCategoryTable.ColumnName, category);
        subquery.PutQueryInt64(MediaTagCategoryTable.ColumnMediaId);
        subquery.Distinct();
        return new InCondition(ColumnOrValue.FromColumn(MediaTable.ColumnId), subquery);
    }

    public static ICondition CreateTagInTagCategoryCondition(string category, ICondition condition)
    {
        var subquery1 = SelectCommand.Create(MediaTagCategoryTable.Instance);
        subquery1.AppendCondition(MediaTagCategoryTable.ColumnName, category);
        subquery1.PutQueryInt64(MediaTagCategoryTable.ColumnId);
        subquery1.Distinct();
        var subquery2 = SelectCommand.Create(MediaTagTable.Instance);
        subquery2.AppendCondition(new InCondition(ColumnOrValue.FromColumn(MediaTagTable.ColumnTagCategoryId), subquery1));
        subquery2.AppendCondition(condition);
        subquery2.PutQueryInt64(MediaTagTable.ColumnMediaId);
        subquery2.Distinct();
        return new InCondition(ColumnOrValue.FromColumn(MediaTable.ColumnId), subquery2);
    }
}
