// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;

using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Common.Playback;

internal interface IPlaybackInfoProvider
{
    MediaInfo? Media { get; }

    MediaEngineState State { get; }

    string? Error { get; }

    TimeSpan Position { get; }

    long PresentedFrames { get; }

    long DroppedFrames { get; }

    TimeSpan PictureLag { get; }

    double AverageVideoDecodeMilliseconds { get; }

    double AverageVideoRenderMilliseconds { get; }

    long AudioUnderruns { get; }

    string? VideoDecoderName { get; }

    string? AudioDecoderName { get; }

    string? VideoRendererName { get; }

    string? AudioRendererName { get; }

    bool IsVideoHardwareAccelerated { get; }

    bool IsAudioHardwareAccelerated { get; }
}
