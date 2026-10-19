// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Timing;

/// <summary>
/// What the pipeline should do with a frame whose timestamp does not line up with the clock.
/// </summary>
public enum FrameSyncAction
{
    /// <summary>The frame is due and should be presented.</summary>
    Present,

    /// <summary>The frame is early and should be offered again later.</summary>
    Wait,

    /// <summary>The frame is too late to be useful and should be discarded.</summary>
    Drop,
}
