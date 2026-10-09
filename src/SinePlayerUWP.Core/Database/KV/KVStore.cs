// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayerUWP.Core.Database.KV;

public static class KVStore
{
    public static IKVDatabase CreateDatabase(string databasePath, bool shared)
    {
        return new CacheLayer(new LiteDBLayer(databasePath, shared));
    }
}
