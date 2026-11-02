// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Render;

public readonly record struct SubtitlePlacement(double OffsetX, double OffsetY, SubtitleInsets Insets = default)
{
    public static SubtitlePlacement Identity => default;

    public bool IsIdentity => OffsetX == 0.0 && OffsetY == 0.0 && Insets.IsNone;
}
