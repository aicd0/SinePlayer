// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Selection;

public sealed record PlaybackPreferences
{
    public static PlaybackPreferences Default { get; } = new();

    public bool PreferHardwareDecoding { get; init; } = true;
}
