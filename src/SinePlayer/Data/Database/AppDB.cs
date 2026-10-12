// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

using SinePlayer.Common.Constants;
using SinePlayer.Core.Common.Storage;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Database.Misc;
using SinePlayer.Core.Database.Registry;
using SinePlayer.SDK.Models;

namespace SinePlayer.Data.Database;

internal static class AppDB
{
    //
    // Host Databases
    //

    private static readonly Lazy<IRegistryDatabase> _mainRegistryDatabase = new(() =>
    {
        string databasePath = Path.Combine(StorageLocation.RegistryFolderPath, "Main.db");
        return RegistryStore.CreateDatabase(databasePath, shared: false);
    });
    public static IRegistryDatabase MainRegistry => _mainRegistryDatabase.Value;

    //
    // Plugins Databases
    //

    private static readonly ConcurrentDictionary<string, IRegistryDatabase> sPluginRegistryDatabases = [];

    public static IRegistryDatabase PluginRegistry(string pluginName)
    {
        if (sPluginRegistryDatabases.TryGetValue(pluginName, out IRegistryDatabase? db))
        {
            return db;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(pluginName);
        byte[] hash = HashUtils.GetXxHash64(bytes);
        string hashString = Convert.ToHexString(hash)[..8].ToUpperInvariant();
        string databasePath = Path.Combine(StorageLocation.RegistryFolderPath, $"PluginRegistry_{hashString}.db");
        db = RegistryStore.CreateDatabase(databasePath, shared: false);
        if (sPluginRegistryDatabases.TryAdd(pluginName, db))
        {
            return db;
        }

        return sPluginRegistryDatabases[pluginName];
    }

    //
    // Helpers
    //

    public static void Dispose()
    {
        SqliteDB.Dispose();

        if (_mainRegistryDatabase.IsValueCreated)
        {
            _mainRegistryDatabase.Value.Dispose();
        }

        foreach (IRegistryDatabase db in sPluginRegistryDatabases.Values)
        {
            db.Dispose();
        }

        sPluginRegistryDatabases.Clear();
        CoreDB.Dispose();
    }

    public static void Initialize()
    {
        MainRegistry.RemoveKey(RegistryNames.RUNTIME_RESOURCES);
        SqliteDB.Initialize();
    }
}
