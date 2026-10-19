// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Info;

/// <summary>
/// Facts about one stream in the container. The source fills in what the container declares and the
/// decoder refines it with what is actually being decoded, so the query surface reports reality
/// rather than the container's claims.
/// </summary>
public abstract class MediaStreamInfo
{
    /// <summary>Index of the stream inside the container.</summary>
    public required int Index { get; init; }

    public abstract MediaStreamKind Kind { get; }

    /// <summary>Stable lower-case identifier used to match decoders, for example <c>h264</c>.</summary>
    public required string CodecId { get; init; }

    /// <summary>Human readable codec name for display, for example <c>H.264 / AVC</c>.</summary>
    public string? CodecName { get; init; }

    public string? Profile { get; init; }

    public string? Language { get; init; }

    public string? Title { get; init; }

    /// <summary>Declared stream bitrate in bits per second, or 0 when unknown.</summary>
    public long Bitrate { get; init; }

    public bool IsDefault { get; init; }
}
