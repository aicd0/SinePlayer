// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.SDK.Models;

namespace SinePlayer.Core.Database.Registry;

public static class RegistryStore
{
    public static IRegistryDatabase CreateDatabase(string databasePath, bool shared)
    {
        databasePath = Path.GetFullPath(databasePath);
        return new LiteDBLayer(databasePath, shared);
    }
}
