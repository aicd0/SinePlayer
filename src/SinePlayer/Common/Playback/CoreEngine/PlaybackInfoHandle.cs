// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;

using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Common.Playback.CoreEngine;

internal sealed class PlaybackInfoHandle : IPlaybackInfoProvider
{
    private volatile PlaybackPipeline? _pipeline;

    public MediaInfo? Media => _pipeline?.MediaInfo;

    public MediaEngineState State => _pipeline?.State ?? MediaEngineState.None;

    public string? Error => _pipeline?.Error;

    public TimeSpan Position => _pipeline?.Position ?? TimeSpan.Zero;

    public long PresentedFrames => _pipeline?.PresentedFrameCount ?? 0;

    public long DroppedFrames => _pipeline?.DroppedFrameCount ?? 0;

    public TimeSpan PictureLag => _pipeline?.PictureLag ?? TimeSpan.Zero;

    public double AverageVideoDecodeMilliseconds => _pipeline?.AverageVideoDecodeMilliseconds ?? 0;

    public double AverageVideoRenderMilliseconds => _pipeline?.AverageVideoRenderMilliseconds ?? 0;

    public long AudioUnderruns => _pipeline?.AudioUnderruns ?? 0;

    public string? VideoDecoderName => _pipeline?.VideoDecoderName;

    public string? AudioDecoderName => _pipeline?.AudioDecoderName;

    public string? VideoRendererName => _pipeline?.VideoRendererName;

    public string? AudioRendererName => _pipeline?.AudioRendererName;

    public bool IsVideoHardwareAccelerated => _pipeline?.IsVideoHardwareAccelerated ?? false;

    public bool IsAudioHardwareAccelerated => _pipeline?.IsAudioHardwareAccelerated ?? false;

    internal void Attach(PlaybackPipeline pipeline)
    {
        _pipeline = pipeline;
    }

    internal void Detach()
    {
        _pipeline = null;
    }
}
