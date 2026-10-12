// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Common.Playback;

internal static class MediaEngineService
{
    public static IMediaEngine CreateEngine()
    {
        return new NativeMediaEngine();
    }
}
