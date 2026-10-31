// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Abstractions.Source;

public sealed class MediaSource
{
    private static int sNextId;

    internal MediaSource(IMediaSource source, MediaLocator locator)
    {
        Id = Interlocked.Increment(ref sNextId);
        Source = source;
        Locator = locator;
        Info = source.Info;
    }

    public int Id { get; }

    public MediaLocator Locator { get; }

    public MediaInfo Info { get; }

    internal IMediaSource Source { get; }

    public override bool Equals(object? obj)
    {
        return obj is MediaSource other && other.Id == Id;
    }

    public override int GetHashCode()
    {
        return Id;
    }

    public static bool operator ==(MediaSource? left, MediaSource? right)
    {
        return left?.Id == right?.Id;
    }

    public static bool operator !=(MediaSource? left, MediaSource? right)
    {
        return !(left == right);
    }
}
