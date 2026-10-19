// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Info;

public sealed class SubtitleStreamInfo : MediaStreamInfo
{
    public override MediaStreamKind Kind => MediaStreamKind.Subtitle;

    public bool IsForced { get; init; }

    /// <summary>Whether the subtitle is a bitmap format that cannot be styled as text.</summary>
    public bool IsBitmap { get; init; }
}
