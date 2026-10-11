// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.Core.Database.SqlHelpers;
using SinePlayerUWP.Data.Database;

namespace SinePlayerUWP.Data.Tables;

internal class MediaHistoryTable : ITable
{
    public static MediaHistoryTable Instance { get; } = new MediaHistoryTable();

    public static Int64Column ColumnMediaId { get; } = new("media_id");
    public static StringColumn ColumnTitle { get; } = new("title");
    public static Int64Column ColumnTime { get; } = new("time");

    private MediaHistoryTable() { }

    public SqlDatabase GetDatabase()
    {
        return SqliteDB.MiscDatabase;
    }

    public string GetTableName()
    {
        return "media_history";
    }
}
