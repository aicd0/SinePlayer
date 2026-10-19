// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Data.Database;

internal static class DatabaseUpgradeManager
{
    public static void UpgradeDatabaseBeforeInitialization()
    {
        int version = DatabaseVersionModel.Version;

        if (version == DatabaseVersionModel.VERSION)
        {
            return;
        }

        switch (version)
        {
            default:
                break;
        }

        DatabaseVersionModel.Version = DatabaseVersionModel.VERSION;
    }

    public static void UpgradeDatabaseAfterInitialization()
    {
        SqliteDB.UpgradeDatabase(DatabaseVersionModel.SqliteSchemeVersion);
    }
}
