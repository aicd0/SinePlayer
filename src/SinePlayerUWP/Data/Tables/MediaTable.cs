// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.Core.Database.SqlHelpers;
using SinePlayerUWP.Data.Database;

namespace SinePlayerUWP.Data.Tables;

internal class MediaTable : ITable
{
    public static MediaTable Instance { get; } = new MediaTable();

    public static Int64Column ColumnId { get; } = new("id");
    public static Int64Column ColumnType { get; } = new("type");
    public static StringColumn ColumnLocation { get; } = new("location");
    public static StringColumn ColumnTitle1 { get; } = new("title1");
    public static StringColumn ColumnTitle2 { get; } = new("title2");
    public static BooleanColumn ColumnHidden { get; } = new("hidden");
    public static Int32Column ColumnRating { get; } = new("rating");
    public static Int32Column ColumnProgress { get; } = new("progress");
    public static DateTimeOffsetColumn ColumnLastVisit { get; } = new("last_visit");
    public static Int64Column ColumnLastPosition { get; } = new("last_pos_ms");
    public static StringColumn ColumnDescription { get; } = new("description");
    public static Int32Column ColumnCompletionStatus { get; } = new("completion_status");
    public static Int64Column ColumnDuration { get; } = new("duration_ms");
    public static StringColumn ColumnExt { get; } = new("ext");

    private MediaTable() { }

    public SqlDatabase GetDatabase()
    {
        return SqliteDB.MediaDatabase;
    }

    public string GetTableName()
    {
        return "media";
    }
}
