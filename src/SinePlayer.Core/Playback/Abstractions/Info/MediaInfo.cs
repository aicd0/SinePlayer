// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Info;

/// <summary>
/// Everything the engine knows about an opened media file: container level facts plus one entry per
/// stream. This is the backing data for both the playback info query and track selection.
/// </summary>
public sealed class MediaInfo
{
    /// <summary>Stable lower-case identifier of the container, for example <c>matroska</c>.</summary>
    public required string ContainerId { get; init; }

    public required TimeSpan Duration { get; init; }

    /// <summary>Overall bitrate in bits per second, or 0 when unknown.</summary>
    public long Bitrate { get; init; }

    public required IReadOnlyList<MediaStreamInfo> Streams { get; init; }

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
