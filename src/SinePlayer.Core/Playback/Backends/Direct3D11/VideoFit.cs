// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Backends.Direct3D11;

/// <summary>
/// Where a frame belongs on a surface that is rarely the same shape as the frame. Fitting it is what keeps
/// the picture in proportion: filling the surface outright stretches whatever does not match, and the
/// surface is a window whose shape the user chooses.
/// </summary>
internal static class VideoFit
{
    /// <summary>
    /// The largest rectangle with the frame's shape that fits the surface, centred in it. It leaves bars
    /// where the shapes differ, which is the honest way to show a picture the window is not shaped for.
    /// </summary>
    internal static (int X, int Y, int Width, int Height) Fit(int frameWidth, int frameHeight, int surfaceWidth, int surfaceHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || surfaceWidth <= 0 || surfaceHeight <= 0)
        {
            return (0, 0, Math.Max(1, surfaceWidth), Math.Max(1, surfaceHeight));
        }

        double scale = Math.Min((double)surfaceWidth / frameWidth, (double)surfaceHeight / frameHeight);
        int width = Math.Max(1, (int)Math.Round(frameWidth * scale));
        int height = Math.Max(1, (int)Math.Round(frameHeight * scale));
        return ((surfaceWidth - width) / 2, (surfaceHeight - height) / 2, width, height);
    }
}
