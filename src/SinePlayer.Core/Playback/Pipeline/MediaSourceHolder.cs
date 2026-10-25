// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed partial class MediaSourceHolder(IMediaSource source, VideoStreamInfo? videoStream, AudioStreamInfo? audioStream) : IDisposable
{
    public IMediaSource Source { get; } = source;

    public VideoStreamInfo? VideoStream { get; } = videoStream;

    public AudioStreamInfo? AudioStream { get; } = audioStream;

    public void Dispose()
    {
    }
}