// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;

using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Common.Playback.Core;

internal sealed class PlaybackInfoHandle(PlayerSession session)
{
    private readonly PlayerSession _session = session;
    private volatile IVideoChain? _video;
    private volatile ISubtitleChain[] _subtitles = [];

    public MediaInfo? Media => _session.Sources is [MediaSource first, ..] ? first.Info : null;

    public IVideoChain? VideoChain => _video;

    public IReadOnlyList<IAudioChain> AudioChains => _session.AudioChains;

    public IReadOnlyList<ISubtitleChain> SubtitleChains => _subtitles;

    public MediaEngineState State => _session.State;

    public string? Error => _session.Error;

    public TimeSpan Position => _session.Position;

    public long PresentedFrames => _video?.PresentedFrameCount ?? 0;

    public long DroppedFrames => _video?.DroppedFrameCount ?? 0;

    public TimeSpan PictureLag => _video?.PictureLag ?? TimeSpan.Zero;

    public double AverageVideoDecodeMilliseconds => _video?.AverageDecodeMilliseconds ?? 0;

    public double AverageVideoRenderMilliseconds => _video?.AverageRenderMilliseconds ?? 0;

    public string? VideoDecoderName => _video?.DecoderName;

    public string? VideoRendererName => _video?.RendererName;

    public bool IsVideoHardwareAccelerated => _video?.IsHardwareAccelerated ?? false;

    internal void Attach(IVideoChain? video, ISubtitleChain[] subtitles)
    {
        _video = video;
        _subtitles = subtitles;
    }

    internal void Detach()
    {
        _video = null;
        _subtitles = [];
    }
}
