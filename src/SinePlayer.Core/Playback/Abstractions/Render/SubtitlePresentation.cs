// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Windows.UI;
using Windows.UI.Text;

namespace SinePlayer.Core.Playback.Abstractions.Render;

public readonly record struct SubtitlePresentation
{
    public static SubtitlePresentation Identity => default;

    public int Order { get; init; }

    public double OffsetX { get; init; }

    public double OffsetY { get; init; }

    public SubtitleInsets Insets { get; init; }

    public string? FontFamily { get; init; }

    public double FontSize { get; init; }

    public FontStyle FontStyle { get; init; }

    public FontWeight FontWeight { get; init; }

    public Color? FontColor { get; init; }

    public double OutlineWidth { get; init; }

    public double? Opacity { get; init; }

    public SubtitleFrame Frame { get; init; }
}
