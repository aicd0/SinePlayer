// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Database.SqlHelpers;

internal interface ICommandContext
{
    string AppendParameter(object value);
}
