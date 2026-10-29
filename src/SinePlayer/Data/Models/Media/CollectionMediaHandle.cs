// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;

using SinePlayer.Data.Tables;

namespace SinePlayer.Data.Models.Media;

internal partial class CollectionMediaHandle : MediaHandle
{
    public CollectionMediaHandle() : base(MediaType.Collection)
    {
    }

    public static MediaHandle CreateNew()
    {
        return new CollectionMediaHandle()
        {
            Location = string.Empty,
            Title1 = string.Empty,
        };
    }

    public override IReadOnlyList<string> GetFolderViewPath()
    {
        return [];
    }
}
