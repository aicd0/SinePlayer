// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Info;

public sealed class MediaInfo
{
    public required string ContainerId { get; init; }

    public required TimeSpan Duration { get; init; }

    public long Bitrate { get; init; }

    public required IReadOnlyList<MediaStreamInfo> Streams { get; init; }

    public IReadOnlyList<MediaAttachment> Attachments { get; init; } = [];

    public IReadOnlyList<VideoStreamInfo> VideoStreams => [.. Streams.OfType<VideoStreamInfo>()];

    public IReadOnlyList<AudioStreamInfo> AudioStreams => [.. Streams.OfType<AudioStreamInfo>()];

    public IReadOnlyList<SubtitleStreamInfo> SubtitleStreams => [.. Streams.OfType<SubtitleStreamInfo>()];

    public MediaStreamInfo? FindStream(int index)
    {
        foreach (MediaStreamInfo stream in Streams)
        {
            if (stream.Index == index)
            {
                return stream;
            }
        }

        return null;
    }
}
