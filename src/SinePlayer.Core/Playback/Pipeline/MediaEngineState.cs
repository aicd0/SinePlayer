// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Pipeline;

public enum MediaEngineState
{
    None,
    Opening,
    Playing,
    Paused,
    Ended,
    Error,
}
