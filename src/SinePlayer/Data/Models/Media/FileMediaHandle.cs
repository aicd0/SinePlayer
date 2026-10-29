// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;

using SinePlayer.Data.Tables;

namespace SinePlayer.Data.Models.Media;

internal partial class FileMediaHandle(MediaType type) : MediaHandle(type)
{
    public override IReadOnlyList<string> GetFolderViewPath()
    {
        string[] pieces = Location.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length == 0)
        {
            return pieces;
        }

        return pieces[..^1];
    }
}
