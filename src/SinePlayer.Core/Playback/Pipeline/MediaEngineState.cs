// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

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
