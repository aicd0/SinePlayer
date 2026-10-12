// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Core.Database.SqlHelpers;

public interface ITable
{
    SqlDatabase GetDatabase();

    string GetTableName();
}
