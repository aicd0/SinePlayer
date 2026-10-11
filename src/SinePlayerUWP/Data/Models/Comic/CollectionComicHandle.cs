// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;

using SinePlayerUWP.Data.Tables;

namespace SinePlayerUWP.Data.Models.Comic;

internal partial class CollectionComicHandle : ComicHandle
{
    public CollectionComicHandle() : base(MediaType.Collection)
    {
    }

    public static ComicHandle FromExternal()
    {
        return new CollectionComicHandle()
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
