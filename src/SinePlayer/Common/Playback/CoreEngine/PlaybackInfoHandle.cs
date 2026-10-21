// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;

using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Common.Playback.CoreEngine;

internal sealed class PlaybackInfoHandle : IPlaybackInfoProvider
{
    private volatile PlaybackPipeline? _pipeline;
    private volatile float _volume = 1.0f;
    private volatile bool _muted;

    public MediaInfo? Media => _pipeline?.MediaInfo;

    public MediaEngineState State => _pipeline?.State ?? MediaEngineState.None;

    public string? Error => _pipeline?.Error;

    public TimeSpan Position => _pipeline?.Position ?? TimeSpan.Zero;

    public long PresentedFrames => _pipeline?.PresentedFrameCount ?? 0;

    public long DroppedFrames => _pipeline?.DroppedFrameCount ?? 0;

    public long? DisplayedFrames => _pipeline?.DisplayedFrameCount;

    public TimeSpan PictureLag => _pipeline?.PictureLag ?? TimeSpan.Zero;

    public double AverageDecodeMilliseconds => _pipeline?.AverageDecodeMilliseconds ?? 0;

    public double AverageRenderMilliseconds => _pipeline?.AverageRenderMilliseconds ?? 0;

    public string? StarvedFor => _pipeline?.StarvedFor;

    public double Volume => _pipeline is null ? 0.0 : _volume;

    public bool IsMuted => _pipeline is not null && _muted;

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

    internal void ReportLevel(double volume, bool muted)
    {
        _volume = (float)volume;
        _muted = muted;
    }
}
