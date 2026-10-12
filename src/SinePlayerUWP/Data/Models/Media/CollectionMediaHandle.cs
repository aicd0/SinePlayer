// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;

using SinePlayerUWP.Data.Tables;

namespace SinePlayerUWP.Data.Models.Media;

internal partial class CollectionMediaHandle : MediaHandle
{
    public CollectionMediaHandle() : base(MediaType.Collection)
    {
    }

    public static MediaHandle FromExternal()
    {
        return new CollectionMediaHandle()
        {
            Location = string.Empty,
            Title1 = string.Empty,
        };
    }

    public override bool IsEditable => !IsExternal;

    public override IReadOnlyList<string> GetFolderViewPath()
    {
        return [];
    }
}
