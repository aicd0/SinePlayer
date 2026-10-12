// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;

using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;

namespace SinePlayer.Views.Pages.Sidebar.MediaInfo;

internal class MediaChangedEventArgs
{
    public required MediaModel? Media { get; init; }
    public required PlaylistModel Playlist { get; init; }
    public required IReadOnlyList<string> ImageDescriptions { get; init; }
}
