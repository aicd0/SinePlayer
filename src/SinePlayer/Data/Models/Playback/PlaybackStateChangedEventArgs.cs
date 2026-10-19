// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Data.Models.Playback;

internal class PlaybackStateChangedEventArgs
{
    public required PlaybackStateChangeReason Reason { get; init; }
    public required long InitialPositionMs { get; init; }
}
