// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;

using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Common.Playback.Core;

internal sealed class PlaybackInfoHandle(PlayerSession session)
{
    private readonly PlayerSession _session = session;
    private volatile IVideoChain? _video;
    private volatile ISubtitleChain? _subtitle;

    public MediaInfo? Media => _session.Sources is [MediaSource first, ..] ? first.Info : null;

    public VideoStreamInfo? SelectedVideoStream => _video?.SelectedStream;

    public AudioStreamInfo? SelectedAudioStream => Audio?.SelectedStream;

    public SubtitleStreamInfo? SelectedSubtitleStream => _subtitle?.SelectedStream;

    public MediaEngineState State => _session.State;

    public string? Error => _session.Error;

    public TimeSpan Position => _session.Position;

    public long PresentedFrames => _video?.PresentedFrameCount ?? 0;

    public long DroppedFrames => _video?.DroppedFrameCount ?? 0;

    public TimeSpan PictureLag => _video?.PictureLag ?? TimeSpan.Zero;

    public double AverageVideoDecodeMilliseconds => _video?.AverageDecodeMilliseconds ?? 0;

    public double AverageVideoRenderMilliseconds => _video?.AverageRenderMilliseconds ?? 0;

    public long AudioUnderruns => Audio?.Underruns ?? 0;

    public string? VideoDecoderName => _video?.DecoderName;

    public string? VideoRendererName => _video?.RendererName;

    public string? AudioDecoderName => Audio?.DecoderName;

    public string? AudioRendererName => Audio?.RendererName;

    public string? SubtitleDecoderName => _subtitle?.DecoderName;

    public string? SubtitleRendererName => _subtitle?.RendererName;

    public bool IsVideoHardwareAccelerated => _video?.IsHardwareAccelerated ?? false;

    public bool IsAudioHardwareAccelerated => Audio?.IsHardwareAccelerated ?? false;

    private IAudioChain? Audio => _session.AudioChains is [IAudioChain first, ..] ? first : null;

    internal void Attach(IVideoChain? video, ISubtitleChain? subtitle)
    {
        _video = video;
        _subtitle = subtitle;
    }

    internal void Detach()
    {
        _video = null;
        _subtitle = null;
    }
}
