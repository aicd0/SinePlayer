// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;

using SinePlayer.Core.Database.SqlHelpers;
using SinePlayer.Data.Database;

namespace SinePlayer.Data.Tables;

internal class MetaTable(SqlDatabase database) : ITable
{
    public const string KEY_VERSION = "version";

    public static StringColumn ColumnKey { get; } = new("key");
    public static Int32Column ColumnValue { get; } = new("value");

    private static readonly Lazy<MetaTable> _media = new(() => new MetaTable(SqliteDB.MediaDatabase));

    public static MetaTable Media => _media.Value;

    public SqlDatabase Database => database;

    public string TableName => "meta";
}
