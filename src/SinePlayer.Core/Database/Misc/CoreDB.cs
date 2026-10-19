// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.Storage;
using SinePlayer.Core.Database.Registry;
using SinePlayer.SDK.Models;

namespace SinePlayer.Core.Database.Misc;

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
