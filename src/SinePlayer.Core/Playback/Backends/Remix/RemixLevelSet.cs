// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Backends.Remix;

/// <summary>
/// The two sets of levels a listener keeps: one for a stream that keeps its layout, one for a stream that is folded
/// down. Which of them is in charge is decided by the routing, never by the listener.
/// </summary>
public enum RemixLevelSet
{
    Remix,
    Downmix,
}
