// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Common.Playback;

/// <summary>
/// Chooses the engine the player page runs on. This is the one place that decides, which is what lets the
/// page be written against <see cref="IMediaEngine"/> and never against an implementation.
/// </summary>
internal static class MediaEngineService
{
    public static IMediaEngine CreateEngine()
    {
        return new PlaybackEngineAdapter();
    }
}
