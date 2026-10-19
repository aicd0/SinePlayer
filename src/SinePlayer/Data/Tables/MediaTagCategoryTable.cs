// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Database.SqlHelpers;
using SinePlayer.Data.Database;

namespace SinePlayer.Data.Tables;

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
