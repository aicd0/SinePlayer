// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.SDK.Models;

namespace SinePlayerUWP.Core.Database.Registry;

public static class RegistryStore
{
    public static IRegistryDatabase CreateDatabase(string databasePath, bool shared)
    {
        databasePath = Path.GetFullPath(databasePath);
        return new LiteDBLayer(databasePath, shared);
    }
}
