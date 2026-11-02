// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Render;

public readonly record struct SubtitleInsets(double Left, double Top, double Right, double Bottom)
{
    public static SubtitleInsets None => default;

    public bool IsNone => Left <= 0.0 && Top <= 0.0 && Right <= 0.0 && Bottom <= 0.0;
}
