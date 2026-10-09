// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.Core.Database.SqlHelpers;
using SinePlayerUWP.Data.Database;

namespace SinePlayerUWP.Data.Tables;

internal class TagsTable : ITable
{
    public static TagsTable Instance { get; } = new TagsTable();

    public static StringColumn ColumnName { get; } = new("name");
    public static StringColumn ColumnTagCategory { get; } = new("tag_category");
    public static StringColumn ColumnExt { get; } = new("ext");

    private TagsTable() { }

    public SqlDatabase GetDatabase()
    {
        return SqliteDB.TagsDatabase;
    }

    public string GetTableName()
    {
        return "tags";
    }
}
