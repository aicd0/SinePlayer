// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.Core.Database.SqlHelpers;
using SinePlayerUWP.Data.Database;

namespace SinePlayerUWP.Data.Tables;

internal class TagCategoriesTable : ITable
{
    public static TagCategoriesTable Instance { get; } = new TagCategoriesTable();

    public static StringColumn ColumnName { get; } = new("name");
    public static StringColumn ColumnExt { get; } = new("ext");

    private TagCategoriesTable() { }

    public SqlDatabase GetDatabase()
    {
        return SqliteDB.TagsDatabase;
    }

    public string GetTableName()
    {
        return "tag_categories";
    }
}
