// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.IO;

using SinePlayerUWP.Core.Common.DebugTools;
using SinePlayerUWP.Core.Common.Storage;
using SinePlayerUWP.Core.Common.Threading;
using SinePlayerUWP.Core.Database.SqlHelpers;
using SinePlayerUWP.Data.Tables;

namespace SinePlayerUWP.Data.Database;

internal static class SqliteDB
{
    public const int VERSION = DatabaseVersionModel.SQLITE_SCHEME_VERSION;

    private const string TAG = nameof(SqliteDB);

    private static string DatabaseFolderPath => Path.Combine(StorageLocation.LocalFolderPath, "Sqlite");

    private static bool _initialized = false;

    //
    // Comic Database
    //

    private static SqlDatabase? _mediaDatabase = null;
    public static SqlDatabase MediaDatabase => _mediaDatabase!;

    private static readonly ITaskDispatcher _mediaDbDispatcher = TaskDispatcher.Factory.NewQueue("MediaDatabaseQueue");
    public static ITaskDispatcher MediaDatabaseDispatcher => _mediaDbDispatcher;

    //
    // Tag Info Database
    //

    private static SqlDatabase? _tagsDatabase = null;
    public static SqlDatabase TagsDatabase => _tagsDatabase!;

    private static readonly ITaskDispatcher _tagsDbDispatcher = TaskDispatcher.Factory.NewQueue("TagsDatabaseQueue");
    public static ITaskDispatcher TagsDatabaseDispatcher => _tagsDbDispatcher;

    //
    // Miscellaneous Database
    //

    private static SqlDatabase? _miscDatabase = null;
    public static SqlDatabase MiscDatabase => _miscDatabase!;

    private static readonly ITaskDispatcher _miscDbDispatcher = TaskDispatcher.Factory.NewQueue("MiscDatabaseQueue");
    public static ITaskDispatcher MiscDatabaseDispatcher => _miscDbDispatcher;

    //
    // Public Methods
    //

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

    public static void UpdateDatabase(int databaseVersion)
    {
        switch (databaseVersion)
        {
            case VERSION:
                break;
            default:
                Logger.F(TAG, $"Unknown database version: {databaseVersion}");
                break;
        }
    }

    private static void InitializeMediaDatabase()
    {
        _mediaDatabase?.Dispose();
        _mediaDatabase = new SqlDatabase(Path.Combine(DatabaseFolderPath, "Media.sqlite"));

        string comicTable = ComicTable.Instance.GetTableName();
        string comicCollectionTable = ComicCollectionTable.Instance.GetTableName();
        string tagCategoryTable = MediaTagCategoryTable.Instance.GetTableName();
        string tagTable = MediaTagTable.Instance.GetTableName();

        ExecuteCommand(MediaDatabase, "CREATE TABLE IF NOT EXISTS " + comicTable + " (" +
            ComicTable.ColumnId.Name + " INTEGER PRIMARY KEY AUTOINCREMENT" +
            "," + ComicTable.ColumnType.Name + " INTEGER NOT NULL" +
            "," + ComicTable.ColumnLocation.Name + " TEXT NOT NULL" +
            "," + ComicTable.ColumnTitle1.Name + " TEXT" +
            "," + ComicTable.ColumnTitle2.Name + " TEXT" +
            "," + ComicTable.ColumnHidden.Name + " BOOLEAN NOT NULL" +
            "," + ComicTable.ColumnRating.Name + " INTEGER NOT NULL" +
            "," + ComicTable.ColumnProgress.Name + " INTEGER NOT NULL" +
            "," + ComicTable.ColumnLastVisit.Name + " TIMESTAMP NOT NULL" +
            "," + ComicTable.ColumnLastPosition.Name + " REAL NOT NULL" +
            "," + ComicTable.ColumnDescription.Name + " TEXT" +
            "," + ComicTable.ColumnCompletionStatus.Name + " INTEGER NOT NULL" +
            "," + ComicTable.ColumnPageCount.Name + " INTEGER NOT NULL" +
            "," + ComicTable.ColumnExt.Name + " TEXT" +
            ")");

        ExecuteCommand(MediaDatabase, "CREATE TABLE IF NOT EXISTS " + comicCollectionTable + " (" +
            ComicCollectionTable.ColumnCollectionId.Name + " INTEGER NOT NULL REFERENCES " + comicTable + "(" + ComicTable.ColumnId.Name + ") ON DELETE CASCADE" +
            "," + ComicCollectionTable.ColumnComicId.Name + " INTEGER NOT NULL REFERENCES " + comicTable + "(" + ComicTable.ColumnId.Name + ") ON DELETE CASCADE" +
            ")");

        ExecuteCommand(MediaDatabase, "CREATE TABLE IF NOT EXISTS " + tagCategoryTable + " (" +
            MediaTagCategoryTable.ColumnId.Name + " INTEGER PRIMARY KEY AUTOINCREMENT" +
            "," + MediaTagCategoryTable.ColumnName.Name + " TEXT" +
            "," + MediaTagCategoryTable.ColumnComicId.Name + " INTEGER REFERENCES " + comicTable + "(" + ComicTable.ColumnId.Name + ") ON DELETE CASCADE" +
            ")");

        ExecuteCommand(MediaDatabase, "CREATE TABLE IF NOT EXISTS " + tagTable + " (" +
            MediaTagTable.ColumnContent.Name + " TEXT" +
            "," + MediaTagTable.ColumnComicId.Name + " INTEGER NOT NULL" +
            "," + MediaTagTable.ColumnTagCategoryId.Name + " INTEGER REFERENCES " + tagCategoryTable + "(" + MediaTagCategoryTable.ColumnId.Name + ") ON DELETE CASCADE" +
            ")");
    }

    private static void InitializeTagsDatabase()
    {
        _tagsDatabase?.Dispose();
        _tagsDatabase = new SqlDatabase(Path.Combine(DatabaseFolderPath, "Tags.sqlite"));

        string tagCategoryInfoTable = TagCategoriesTable.Instance.GetTableName();
        string tagInfoTable = TagsTable.Instance.GetTableName();

        ExecuteCommand(TagsDatabase, "CREATE TABLE IF NOT EXISTS " + tagCategoryInfoTable + " (" +
            TagCategoriesTable.ColumnName.Name + " TEXT NOT NULL PRIMARY KEY" +
            "," + TagCategoriesTable.ColumnExt.Name + " TEXT" +
            ")");

        ExecuteCommand(TagsDatabase, "CREATE TABLE IF NOT EXISTS " + tagInfoTable + " (" +
            TagsTable.ColumnName.Name + " TEXT NOT NULL" +
            "," + TagsTable.ColumnTagCategory.Name + " TEXT NOT NULL REFERENCES " + tagCategoryInfoTable + "(" + TagCategoriesTable.ColumnName.Name + ") ON DELETE CASCADE ON UPDATE CASCADE" +
            "," + TagsTable.ColumnExt.Name + " TEXT" +
            ")");
    }

    private static void InitializeMiscDatabase()
    {
        _miscDatabase?.Dispose();
        _miscDatabase = new SqlDatabase(Path.Combine(DatabaseFolderPath, "Misc.sqlite"));

        string comicHistoryTable = ComicHistoryTable.Instance.GetTableName();

        ExecuteCommand(MiscDatabase, "CREATE TABLE IF NOT EXISTS " + comicHistoryTable + " (" +
            ComicHistoryTable.ColumnComicId.Name + " INTEGER PRIMARY KEY" +
            "," + ComicHistoryTable.ColumnTitle.Name + " TEXT NOT NULL" +
            "," + ComicHistoryTable.ColumnTime.Name + " INTEGER NOT NULL" +
            ")");
    }

    private static void ExecuteCommand(SqlDatabase database, string commandText)
    {
        UnsafeCommand.Create(commandText)
            .Execute(database);
    }
}
