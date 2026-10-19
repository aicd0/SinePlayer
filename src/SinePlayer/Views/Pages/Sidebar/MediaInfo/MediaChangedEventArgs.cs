// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;

using SinePlayer.Common.Playback;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;

namespace SinePlayer.Views.Pages.Sidebar.MediaInfo;

internal class MediaChangedEventArgs
{
    public required MediaModel? Media { get; init; }
    public required PlayQueueModel PlayQueue { get; init; }
    public required IReadOnlyList<string> ImageDescriptions { get; init; }
    public IPlaybackInfoProvider? PlaybackInfoProvider { get; init; }
}
