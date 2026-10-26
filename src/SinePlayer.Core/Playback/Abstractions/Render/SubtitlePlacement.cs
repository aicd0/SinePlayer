// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Render;

/// <summary>
/// How far subtitles are moved from where they would otherwise be drawn. It is a viewing adjustment the player offers,
/// like zooming the picture, and not a description of where the picture is: turning or mirroring a picture must not
/// turn or mirror text, so nothing here is derived from <see cref="VideoTransform"/>.
/// <para>
/// The offsets are fractions of the surface, so they mean the same thing at every window size: an offset of
/// <c>0.1</c> down moves the text by a tenth of the display's height whatever that height is. A renderer clamps the
/// result, because the point of adjusting subtitles is still to read them — text pushed off the edge is of no use, and
/// a surface that later shrinks must not leave it stranded.
/// </para>
/// </summary>
/// <param name="OffsetX">Fraction of the surface's width to move right (negative moves left).</param>
/// <param name="OffsetY">Fraction of the surface's height to move down (negative moves up).</param>
public readonly record struct SubtitlePlacement(double OffsetX, double OffsetY)
{
    /// <summary>Where the renderer would have put the text anyway.</summary>
    public static SubtitlePlacement Identity => default;

    public bool IsIdentity => OffsetX == 0.0 && OffsetY == 0.0;
}
