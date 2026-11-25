// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;
using System.IO;

using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Storage;
using SinePlayer.Core.Common.Threading;
using SinePlayer.Core.Database.SqlHelpers;
using SinePlayer.Data.Tables;

namespace SinePlayer.Data.Database;

internal static class SqliteDB
{
    #region Constructor

    private const string TAG = nameof(SqliteDB);

    private static string DatabaseFolderPath => Path.Combine(StorageLocation.LocalFolderPath, "Sqlite");

    private static bool _initialized = false;

    #endregion

    #region Public Methods

    public static void Dispose()
    {
        _mediaDatabase?.Dispose();
        _mediaDatabase = null;
        _tagsDatabase?.Dispose();
        _tagsDatabase = null;
        _miscDatabase?.Dispose();
        _miscDatabase = null;
    }

    public static void Initialize()
    {
        if (_initialized)
        {
            Logger.F(TAG, "Database is already initialized");
            return;
        }

        string databasePath = DatabaseFolderPath;
        if (!Directory.Exists(databasePath))
        {
            Directory.CreateDirectory(databasePath);
        }

        InitializeMediaDatabase();
        InitializeTagsDatabase();
        InitializeMiscDatabase();
        _initialized = true;
    }

    public static void UpgradeDatabase()
    {
        UpgradeMediaDatabase();
    }

    #endregion

    #region Media Database

    private const int MEDIA_DATABASE_VERSION = 1;

    private static SqlDatabase? _mediaDatabase = null;
    public static SqlDatabase MediaDatabase => _mediaDatabase!;

    private static readonly ITaskDispatcher _mediaDbDispatcher = TaskDispatcher.Factory.NewQueue("MediaDatabaseQueue");
    public static ITaskDispatcher MediaDatabaseDispatcher => _mediaDbDispatcher;

    private static void UpgradeMediaDatabase()
    {
        int version = ReadMediaDatabaseVersion();
        if (version >= MEDIA_DATABASE_VERSION)
        {
            return;
        }

        string mediaTable = MediaTable.Instance.TableName;
        MediaDatabase.WithTransaction(() =>
        {
            switch (version)
            {
                case 0: // 1.0.1
                    ExecuteCommand(MediaDatabase, $"ALTER TABLE {mediaTable} ADD COLUMN {MediaTable.ColumnReleaseDate.Name} TEXT");
                    break;
                default:
                    Logger.F(TAG, $"Unknown database version: {version}");
                    break;
            }

            WriteMediaDatabaseVersion(MEDIA_DATABASE_VERSION);
        });
    }

    private static void InitializeMediaDatabase()
    {
        _mediaDatabase?.Dispose();
        _mediaDatabase = new SqlDatabase(Path.Combine(DatabaseFolderPath, "Media.sqlite"));

        bool isNewDatabase = !MediaDatabase.TableExists(MediaTable.Instance.TableName);
        MediaDatabase.WithTransaction(() =>
        {
            CreateMediaTables();
            if (isNewDatabase)
            {
                WriteMediaDatabaseVersion(MEDIA_DATABASE_VERSION);
            }
        });
    }

    private static void CreateMediaTables()
    {
        string metaTable = MetaTable.Media.TableName;
        string mediaTable = MediaTable.Instance.TableName;
        string mediaCollectionTable = MediaCollectionTable.Instance.TableName;
        string mediaTagCategoryTable = MediaTagCategoryTable.Instance.TableName;
        string mediaTagTable = MediaTagTable.Instance.TableName;

        ExecuteCommand(MediaDatabase, "CREATE TABLE IF NOT EXISTS " + metaTable + " (" +
            MetaTable.ColumnKey.Name + " TEXT NOT NULL PRIMARY KEY" +
            "," + MetaTable.ColumnValue.Name + " INTEGER NOT NULL" +
            ")");

        ExecuteCommand(MediaDatabase, "CREATE TABLE IF NOT EXISTS " + mediaTable + " (" +
            MediaTable.ColumnId.Name + " INTEGER PRIMARY KEY AUTOINCREMENT" +
            "," + MediaTable.ColumnType.Name + " INTEGER NOT NULL" +
            "," + MediaTable.ColumnLocation.Name + " TEXT NOT NULL" +
            "," + MediaTable.ColumnExternal.Name + " BOOLEAN NOT NULL" +
            "," + MediaTable.ColumnTitle1.Name + " TEXT" +
            "," + MediaTable.ColumnTitle2.Name + " TEXT" +
            "," + MediaTable.ColumnHidden.Name + " BOOLEAN NOT NULL" +
            "," + MediaTable.ColumnRating.Name + " INTEGER NOT NULL" +
            "," + MediaTable.ColumnProgress.Name + " INTEGER NOT NULL" +
            "," + MediaTable.ColumnLastVisit.Name + " TIMESTAMP NOT NULL" +
            "," + MediaTable.ColumnLastPositionMs.Name + " INTEGER NOT NULL" +
            "," + MediaTable.ColumnDescription.Name + " TEXT" +
            "," + MediaTable.ColumnCompletionStatus.Name + " INTEGER NOT NULL" +
            "," + MediaTable.ColumnDurationMs.Name + " INTEGER NOT NULL" +
            "," + MediaTable.ColumnReleaseDate.Name + " TEXT" +
            "," + MediaTable.ColumnExt.Name + " TEXT" +
            ")");

        ExecuteCommand(MediaDatabase, "CREATE TABLE IF NOT EXISTS " + mediaCollectionTable + " (" +
            MediaCollectionTable.ColumnCollectionId.Name + " INTEGER NOT NULL REFERENCES " + mediaTable + "(" + MediaTable.ColumnId.Name + ") ON DELETE CASCADE" +
            "," + MediaCollectionTable.ColumnMediaId.Name + " INTEGER NOT NULL REFERENCES " + mediaTable + "(" + MediaTable.ColumnId.Name + ") ON DELETE CASCADE" +
            ")");

        ExecuteCommand(MediaDatabase, "CREATE TABLE IF NOT EXISTS " + mediaTagCategoryTable + " (" +
            MediaTagCategoryTable.ColumnId.Name + " INTEGER PRIMARY KEY AUTOINCREMENT" +
            "," + MediaTagCategoryTable.ColumnName.Name + " TEXT" +
            "," + MediaTagCategoryTable.ColumnMediaId.Name + " INTEGER REFERENCES " + mediaTable + "(" + MediaTable.ColumnId.Name + ") ON DELETE CASCADE" +
            ")");

        ExecuteCommand(MediaDatabase, "CREATE TABLE IF NOT EXISTS " + mediaTagTable + " (" +
            MediaTagTable.ColumnContent.Name + " TEXT" +
            "," + MediaTagTable.ColumnMediaId.Name + " INTEGER NOT NULL" +
            "," + MediaTagTable.ColumnTagCategoryId.Name + " INTEGER REFERENCES " + mediaTagCategoryTable + "(" + MediaTagCategoryTable.ColumnId.Name + ") ON DELETE CASCADE" +
            ")");
    }

    private static int ReadMediaDatabaseVersion()
    {
        SelectCommand command = SelectCommand.Create(MetaTable.Media)
            .AppendCondition(MetaTable.ColumnKey, MetaTable.KEY_VERSION);
        IReaderToken<int> valueToken = command.PutQueryInt32(MetaTable.ColumnValue);
        using SelectCommand.IReader reader = command.Execute();
        return reader.Read() ? valueToken.GetValue() : 0;
    }

    private static void WriteMediaDatabaseVersion(int version)
    {
        InsertCommand.Create(MetaTable.Media)
            .AppendColumn(MetaTable.ColumnKey, MetaTable.KEY_VERSION)
            .AppendColumn(MetaTable.ColumnValue, version)
            .OnConflict((List<IColumnTypeless>)[MetaTable.ColumnKey])
            .Update(MetaTable.ColumnValue, version)
            .End()
            .Execute();
    }

    #endregion

    #region Tag Info Database

    private static SqlDatabase? _tagsDatabase = null;
    public static SqlDatabase TagsDatabase => _tagsDatabase!;

    private static readonly ITaskDispatcher _tagsDbDispatcher = TaskDispatcher.Factory.NewQueue("TagsDatabaseQueue");
    public static ITaskDispatcher TagsDatabaseDispatcher => _tagsDbDispatcher;

    private static void InitializeTagsDatabase()
    {
        _tagsDatabase?.Dispose();
        _tagsDatabase = new SqlDatabase(Path.Combine(DatabaseFolderPath, "Tags.sqlite"));

        string tagCategoriesTable = TagCategoriesTable.Instance.TableName;
        string tagsTable = TagsTable.Instance.TableName;

        ExecuteCommand(TagsDatabase, "CREATE TABLE IF NOT EXISTS " + tagCategoriesTable + " (" +
            TagCategoriesTable.ColumnName.Name + " TEXT NOT NULL PRIMARY KEY" +
            "," + TagCategoriesTable.ColumnExt.Name + " TEXT" +
            ")");

        ExecuteCommand(TagsDatabase, "CREATE TABLE IF NOT EXISTS " + tagsTable + " (" +
            TagsTable.ColumnName.Name + " TEXT NOT NULL" +
            "," + TagsTable.ColumnTagCategory.Name + " TEXT NOT NULL REFERENCES " + tagCategoriesTable + "(" + TagCategoriesTable.ColumnName.Name + ") ON DELETE CASCADE ON UPDATE CASCADE" +
            "," + TagsTable.ColumnExt.Name + " TEXT" +
            ")");
    }

    #endregion

    #region Miscellaneous Database

    private static SqlDatabase? _miscDatabase = null;
    public static SqlDatabase MiscDatabase => _miscDatabase!;

    private static readonly ITaskDispatcher _miscDbDispatcher = TaskDispatcher.Factory.NewQueue("MiscDatabaseQueue");
    public static ITaskDispatcher MiscDatabaseDispatcher => _miscDbDispatcher;

    private static void InitializeMiscDatabase()
    {
        _miscDatabase?.Dispose();
        _miscDatabase = new SqlDatabase(Path.Combine(DatabaseFolderPath, "Misc.sqlite"));

        string mediaHistoryTable = MediaHistoryTable.Instance.TableName;

        ExecuteCommand(MiscDatabase, "CREATE TABLE IF NOT EXISTS " + mediaHistoryTable + " (" +
            MediaHistoryTable.ColumnMediaId.Name + " INTEGER PRIMARY KEY" +
            "," + MediaHistoryTable.ColumnTitle.Name + " TEXT NOT NULL" +
            "," + MediaHistoryTable.ColumnTime.Name + " INTEGER NOT NULL" +
            ")");
    }

    #endregion

    #region Helpers

    private static void ExecuteCommand(SqlDatabase database, string commandText)
    {
        UnsafeCommand.Create(commandText)
            .Execute(database);
    }

    #endregion
}
