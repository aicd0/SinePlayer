// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace SinePlayer.Data.Models.Media;

internal class MediaTagCategory(HashSet<string> tags) : SDK.Plugins.Media.IMediaTagCategory
{
    public IReadOnlySet<string> Tags { get; } = tags;

    //
    // SDK.Plugins.Media.IMediaTagCategory Implementation
    //

    IReadOnlySet<string> SDK.Plugins.Media.IMediaTagCategory.Tags => Tags;
}
