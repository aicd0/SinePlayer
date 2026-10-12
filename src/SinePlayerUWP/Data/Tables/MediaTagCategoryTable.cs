// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.Core.Database.SqlHelpers;
using SinePlayerUWP.Data.Database;

namespace SinePlayerUWP.Data.Tables;

internal class MediaTagCategoryTable : ITable
{
    public static MediaTagCategoryTable Instance { get; } = new MediaTagCategoryTable();

    public static Int64Column ColumnId { get; } = new("id");
    public static StringColumn ColumnName { get; } = new("name");
    public static Int64Column ColumnMediaId { get; } = new("media_id");

    private MediaTagCategoryTable() { }

    public SqlDatabase GetDatabase()
    {
        return SqliteDB.MediaDatabase;
    }

    public string GetTableName()
    {
        return "media_tag_category";
    }
}
