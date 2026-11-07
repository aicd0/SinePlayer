// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Linq;

using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Common.Playback.Core;

internal sealed class PlaybackInfoHandle
{
    private volatile PlaybackPipeline? _pipeline;

    public MediaInfo? Media => Sources is [MediaSource first, ..] ? first.Info : null;

    public VideoStreamInfo? SelectedVideoStream => Video?.SelectedStream;

    public AudioStreamInfo? SelectedAudioStream => Audio?.SelectedStream;

    public SubtitleStreamInfo? SelectedSubtitleStream => Subtitle?.SelectedStream;

    public IReadOnlyList<MediaSource> Sources => _pipeline?.Sources ?? [];

    public MediaEngineState State => _pipeline?.State ?? MediaEngineState.None;

    public string? Error => _pipeline?.Error;

    public TimeSpan Position => _pipeline?.Position ?? TimeSpan.Zero;

    public long PresentedFrames => Video?.PresentedFrameCount ?? 0;

    public long DroppedFrames => Video?.DroppedFrameCount ?? 0;

    public TimeSpan PictureLag => Video?.PictureLag ?? TimeSpan.Zero;

    public double AverageVideoDecodeMilliseconds => Video?.AverageDecodeMilliseconds ?? 0;

    public double AverageVideoRenderMilliseconds => Video?.AverageRenderMilliseconds ?? 0;

    public long AudioUnderruns => Audio?.Underruns ?? 0;

    public string? VideoDecoderName => Video?.DecoderName;

    public string? VideoRendererName => Video?.RendererName;

    public string? AudioDecoderName => Audio?.DecoderName;

    public string? AudioRendererName => Audio?.RendererName;

    public string? SubtitleDecoderName => Subtitle?.DecoderName;

    public string? SubtitleRendererName => Subtitle?.RendererName;

    public bool IsVideoHardwareAccelerated => Video?.IsHardwareAccelerated ?? false;

    public bool IsAudioHardwareAccelerated => Audio?.IsHardwareAccelerated ?? false;

    private IVideoChain? Video => _pipeline?.VideoChains.FirstOrDefault();

    private IAudioChain? Audio => _pipeline?.AudioChains.FirstOrDefault();

    private ISubtitleChain? Subtitle => _pipeline?.SubtitleChains.FirstOrDefault();

    internal void Attach(PlaybackPipeline pipeline)
    {
        _pipeline = pipeline;
    }

    internal void Detach()
    {
        _pipeline = null;
    }
}
