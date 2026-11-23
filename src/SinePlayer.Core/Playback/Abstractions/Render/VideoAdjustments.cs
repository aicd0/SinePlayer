// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Render;

/// <summary>
/// The colour of the picture as the viewer sees it, kept apart from where the picture is placed: the four controls a
/// photo editor offers. Every value is relative to the picture as it was mastered, and all zero leaves it untouched.
/// </summary>
public readonly record struct VideoAdjustments
{
    public static VideoAdjustments Neutral => new();

    /// <summary>
    /// Light added to the encoded picture, from -1 to 1. Contrast is applied before this, so the offset moves what the
    /// contrast left rather than what the picture was.
    /// </summary>
    public float Brightness { get; init; }

    /// <summary>
    /// How hard the picture is pushed away from mid grey, from -1 to 1: -1 flattens it to grey, 0 is unchanged and 1
    /// doubles the distance every sample has from the middle.
    /// </summary>
    public float Contrast { get; init; }

    /// <summary>
    /// How much colour is kept, from -1 to 1: -1 leaves only the luma, 0 is unchanged and 1 doubles the colour. Luma is
    /// preserved, so a grey stays the grey it was.
    /// </summary>
    public float Saturation { get; init; }

    /// <summary>Degrees of hue rotation around the grey axis, from -180 to 180, with 0 unchanged.</summary>
    public float Hue { get; init; }
}
