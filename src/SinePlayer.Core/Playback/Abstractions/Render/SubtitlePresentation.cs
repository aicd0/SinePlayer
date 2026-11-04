// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Windows.UI.Text;

namespace SinePlayer.Core.Playback.Abstractions.Render;

public readonly record struct SubtitlePresentation(
    double OffsetX,
    double OffsetY,
    SubtitleInsets Insets = default,
    string? FontFamily = null,
    double FontSize = 0.0,
    FontStyle FontStyle = FontStyle.Normal,
    FontWeight FontWeight = default,
    double OutlineWidth = 0.0)
{
    public static SubtitlePresentation Identity => default;

    public SubtitleFrame Frame { get; init; }

    public bool IsIdentity =>
        OffsetX == 0.0 &&
        OffsetY == 0.0 &&
        Insets.IsNone &&
        string.IsNullOrEmpty(FontFamily) &&
        FontSize <= 0.0 &&
        FontStyle == FontStyle.Normal &&
        FontWeight.Weight <= 0 &&
        OutlineWidth <= 0.0 &&
        Frame.IsEmpty;
}
