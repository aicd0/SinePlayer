// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;

using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Common.Playback.Core;

internal sealed class PlaybackInfoHandle
{
    private volatile PlaybackPipeline? _pipeline;
    private volatile IVideoChain? _video;
    private volatile IAudioChain? _audio;
    private volatile ISubtitleChain? _subtitle;

    public MediaInfo? Media => Sources is [MediaSource first, ..] ? first.Info : null;

    public VideoStreamInfo? SelectedVideoStream => _video?.SelectedStream;

    public AudioStreamInfo? SelectedAudioStream => _audio?.SelectedStream;

    public SubtitleStreamInfo? SelectedSubtitleStream => _subtitle?.SelectedStream;

    public IReadOnlyList<MediaSource> Sources => _pipeline?.Sources ?? [];

    public MediaEngineState State => _pipeline?.State ?? MediaEngineState.None;

    public string? Error => _pipeline?.Error;

    public TimeSpan Position => _pipeline?.Position ?? TimeSpan.Zero;

    public long PresentedFrames => _video?.PresentedFrameCount ?? 0;

    public long DroppedFrames => _video?.DroppedFrameCount ?? 0;

    public TimeSpan PictureLag => _video?.PictureLag ?? TimeSpan.Zero;

    public double AverageVideoDecodeMilliseconds => _video?.AverageDecodeMilliseconds ?? 0;

    public double AverageVideoRenderMilliseconds => _video?.AverageRenderMilliseconds ?? 0;

    public long AudioUnderruns => _audio?.Underruns ?? 0;

    public string? VideoDecoderName => _video?.DecoderName;

    public string? VideoRendererName => _video?.RendererName;

    public string? AudioDecoderName => _audio?.DecoderName;

    public string? AudioRendererName => _audio?.RendererName;

    public string? SubtitleDecoderName => _subtitle?.DecoderName;

    public string? SubtitleRendererName => _subtitle?.RendererName;

    public bool IsVideoHardwareAccelerated => _video?.IsHardwareAccelerated ?? false;

    public bool IsAudioHardwareAccelerated => _audio?.IsHardwareAccelerated ?? false;

    internal void Attach(PlaybackPipeline pipeline, IVideoChain video, IAudioChain audio, ISubtitleChain subtitle)
    {
        _video = video;
        _audio = audio;
        _subtitle = subtitle;
        _pipeline = pipeline;
    }

    internal void Detach()
    {
        _pipeline = null;
        _video = null;
        _audio = null;
        _subtitle = null;
    }
}
