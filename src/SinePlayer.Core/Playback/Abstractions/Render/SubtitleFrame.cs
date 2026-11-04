// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Render;

public readonly record struct SubtitleFrame(double Left, double Top, double Width, double Height)
{
    public static SubtitleFrame Full => new(0.0, 0.0, 1.0, 1.0);

    public bool IsEmpty => Width <= 0.0 || Height <= 0.0;
}
