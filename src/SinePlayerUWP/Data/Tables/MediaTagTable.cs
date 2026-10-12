// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.Core.Database.SqlHelpers;
using SinePlayerUWP.Data.Database;

namespace SinePlayerUWP.Data.Tables;

internal class MediaTagTable : ITable
{
    public static MediaTagTable Instance { get; } = new MediaTagTable();

    public static StringColumn ColumnContent { get; } = new("content");
    public static Int64Column ColumnMediaId { get; } = new("media_id");
    public static Int64Column ColumnTagCategoryId { get; } = new("tag_category_id");

    private MediaTagTable() { }

    public SqlDatabase GetDatabase()
    {
        return SqliteDB.MediaDatabase;
    }

    public string GetTableName()
    {
        return "media_tag";
    }
}
