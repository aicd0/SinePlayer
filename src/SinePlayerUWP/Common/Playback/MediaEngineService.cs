// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayerUWP.Common.Playback;

internal static class MediaEngineService
{
    // Only the native engine exists for now; settings-driven engine selection
    // will be added when alternative engines (e.g. libVLC) are introduced.
    public static IMediaEngine CreateEngine()
    {
        return new NativeMediaEngine();
    }
}
