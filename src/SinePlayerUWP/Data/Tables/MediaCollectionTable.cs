// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.Core.Database.SqlHelpers;
using SinePlayerUWP.Data.Database;

namespace SinePlayerUWP.Data.Tables;

internal class MediaCollectionTable : ITable
{
    public static MediaCollectionTable Instance { get; } = new();

    public static Int64Column ColumnCollectionId { get; } = new("collection_id");
    public static Int64Column ColumnMediaId { get; } = new("media_id");

    private MediaCollectionTable() { }

    public SqlDatabase GetDatabase()
    {
        return SqliteDB.MediaDatabase;
    }

    public string GetTableName()
    {
        return "media_collections";
    }
}
