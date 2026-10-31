// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed partial class MediaSourceHolder(
    IReadOnlyList<MediaSource> sources,
    VideoStreamInfo? videoStream,
    AudioStreamInfo? audioStream,
    SubtitleStreamInfo? subtitleStream) : IDisposable
{
    private static MediaSource? FindSource(IEnumerable<MediaSource> sources, MediaStreamInfo stream)
    {
        foreach (MediaSource source in sources)
        {
            foreach (MediaStreamInfo candidate in source.Info.Streams)
            {
                if (ReferenceEquals(candidate, stream))
                {
                    return source;
                }
            }
        }

        return null;
    }

    public IReadOnlyList<MediaSource> Sources { get; } = sources;

    public VideoStreamInfo? VideoStream { get; } = videoStream;

    public MediaSource? VideoSource { get; } = videoStream is null ? null :
        (FindSource(sources, videoStream) ?? throw new ArgumentException($"Stream {videoStream.Index} is not part of any source."));

    public AudioStreamInfo? AudioStream { get; } = audioStream;

    public MediaSource? AudioSource { get; } = audioStream is null ? null :
        (FindSource(sources, audioStream) ?? throw new ArgumentException($"Stream {audioStream.Index} is not part of any source."));

    public SubtitleStreamInfo? SubtitleStream { get; } = subtitleStream;

    public MediaSource? SubtitleSource { get; } = subtitleStream is null ? null :
        (FindSource(sources, subtitleStream) ?? throw new ArgumentException($"Stream {subtitleStream.Index} is not part of any source."));

    public MediaSource? TryGetSourceOf(MediaStreamInfo? stream)
    {
        if (stream is null)
        {
            return null;
        }

        return FindSource(Sources, stream);
    }

    public void Dispose()
    {
    }
}