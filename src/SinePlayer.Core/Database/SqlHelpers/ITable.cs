// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Database.SqlHelpers;

public interface ITable
{
    SqlDatabase GetDatabase();

    string GetTableName();
}
