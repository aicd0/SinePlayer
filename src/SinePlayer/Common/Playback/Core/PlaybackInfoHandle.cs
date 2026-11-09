// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Linq;

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

    public VideoStreamInfo? SelectedVideoStream => _video?.SelectedStream;

    public AudioStreamInfo? SelectedAudioStream => Audio?.SelectedStream;

    public SubtitleStreamInfo? SelectedSubtitleStream => Subtitle?.SelectedStream;

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

    public string? SubtitleDecoderName => Subtitle?.DecoderName;

    public string? SubtitleRendererName => Subtitle?.RendererName;

    public bool IsVideoHardwareAccelerated => _video?.IsHardwareAccelerated ?? false;

    public bool IsAudioHardwareAccelerated => Audio?.IsHardwareAccelerated ?? false;

    private IAudioChain? Audio => _session.AudioChains is [IAudioChain first, ..] ? first : null;

    private ISubtitleChain? Subtitle => _subtitles.FirstOrDefault(chain => chain.PlayingStream is not null) ?? (_subtitles.Length == 0 ? null : _subtitles[0]);

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
