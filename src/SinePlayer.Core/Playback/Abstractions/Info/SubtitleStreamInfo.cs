// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Info;

public sealed class SubtitleStreamInfo : MediaStreamInfo
{
    public override MediaStreamKind Kind => MediaStreamKind.Subtitle;

    public bool IsForced { get; init; }

    /// <summary>Whether the subtitle is a bitmap format that cannot be styled as text.</summary>
    public bool IsBitmap { get; init; }

    /// <summary>
    /// The file this track is read from when it is not a stream of the playing file — a sidecar subtitle next to it, or
    /// one the user picked — and null when the track is in the container. An external track is selected the same way as
    /// an embedded one; only where its cues come from differs.
    /// </summary>
    public string? Path { get; init; }

    public bool IsExternal => Path is not null;
}
