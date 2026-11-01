// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Info;

public abstract class MediaStreamInfo
{
    private static int sNextId;

    protected MediaStreamInfo()
    {
        Id = Interlocked.Increment(ref sNextId);
    }

    private int Id { get; }

    public required int Index { get; init; }

    public abstract MediaStreamKind Kind { get; }

    public required string CodecId { get; init; }

    public string? CodecName { get; init; }

    public string? Profile { get; init; }

    public string? Language { get; init; }

    public string? Title { get; init; }

    public TimeSpan Duration { get; init; }

    public long Bitrate { get; init; }

    public bool IsDefault { get; init; }

    public override bool Equals(object? obj)
    {
        if (obj is not MediaStreamInfo other)
        {
            return false;
        }

        return Id == other.Id;
    }

    public override int GetHashCode()
    {
        return Id;
    }

    public static bool operator ==(MediaStreamInfo? left, MediaStreamInfo? right)
    {
        return left?.Id == right?.Id;
    }

    public static bool operator !=(MediaStreamInfo? left, MediaStreamInfo? right)
    {
        return !(left == right);
    }
}
