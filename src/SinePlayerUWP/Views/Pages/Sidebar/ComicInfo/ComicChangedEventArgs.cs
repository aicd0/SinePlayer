// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;

using SinePlayerUWP.Data.Models.Comic;
using SinePlayerUWP.Data.Models.Misc;

namespace SinePlayerUWP.Views.Pages.Sidebar.ComicInfo;

internal class ComicChangedEventArgs
{
    public required ComicModel? Comic { get; init; }
    public required PlaylistModel Playlist { get; init; }
    public required IReadOnlyList<string> ImageDescriptions { get; init; }
}
