// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.Core.Common.Storage;
using SinePlayerUWP.Core.Database.Registry;
using SinePlayerUWP.SDK.Models;

namespace SinePlayerUWP.Core.Database.Misc;

public static class CoreDB
{
    private static readonly Lazy<IRegistryDatabase> _coreRegistryDatabase = new(() =>
    {
        string databasePath = Path.Combine(StorageLocation.RegistryFolderPath, "Core.db");
        return RegistryStore.CreateDatabase(databasePath, shared: true);
    });
    public static IRegistryDatabase CoreRegistry => _coreRegistryDatabase.Value;

    public static void Dispose()
    {
        if (_coreRegistryDatabase.IsValueCreated)
        {
            _coreRegistryDatabase.Value.Dispose();
        }
    }
}
