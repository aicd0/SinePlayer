// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Database.SqlHelpers;
using SinePlayer.Data.Database;

namespace SinePlayer.Data.Tables;

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
