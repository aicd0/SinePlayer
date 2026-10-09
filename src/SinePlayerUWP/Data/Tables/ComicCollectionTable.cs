// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.Core.Database.SqlHelpers;
using SinePlayerUWP.Data.Database;

namespace SinePlayerUWP.Data.Tables;

internal class ComicCollectionTable : ITable
{
    public static ComicCollectionTable Instance { get; } = new();

    public static Int64Column ColumnCollectionId { get; } = new("collection_id");
    public static Int64Column ColumnComicId { get; } = new("comic_id");

    private ComicCollectionTable() { }

    public SqlDatabase GetDatabase()
    {
        return SqliteDB.MainDatabase;
    }

    public string GetTableName()
    {
        return "comic_collections";
    }
}
