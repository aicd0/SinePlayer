// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Numerics;

namespace SinePlayer.Core.Playback.Abstractions.Render;

/// <summary>
/// How a picture is turned for display: quarter turns clockwise, and whether it is mirrored. The four turns and
/// the mirror are the whole of the group — a vertical flip is a mirror plus half a turn — so any combination of
/// the two composes into another value of this type, which is what makes one transform buildable out of several.
/// </summary>
public readonly record struct VideoOrientation
{
    private readonly int _quarterTurns;
    private readonly bool _mirrored;

    /// <param name="quarterTurns">Quarter turns clockwise; any whole number, reduced to 0 to 3.</param>
    /// <param name="mirrored">Whether the picture is mirrored left to right.</param>
    public VideoOrientation(int quarterTurns, bool mirrored)
    {
        _quarterTurns = Normalize(quarterTurns);
        _mirrored = mirrored;
    }

    /// <summary>The picture as it was decoded.</summary>
    public static VideoOrientation Identity => default;

    public int QuarterTurns => _quarterTurns;

    public bool Mirrored => _mirrored;

    public int RotationDegrees => _quarterTurns * 90;

    public bool IsIdentity => _quarterTurns == 0 && !_mirrored;

    /// <summary>Whether the turn leaves the picture lying the other way round, so its width and height trade places.</summary>
    public bool SwapsAxes => (_quarterTurns & 1) == 1;

    /// <summary>
    /// What a person asked for, in the terms they asked it: degrees and the two flips a picture can be given.
    /// Mirroring both ways is the same as turning the picture round, so the two flags become one mirror and a turn.
    /// </summary>
    public static VideoOrientation FromDegrees(int degrees, bool flipHorizontal, bool flipVertical)
    {
        if (degrees % 90 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "A quarter turn is all a picture is turned by.");
        }

        int quarterTurns = degrees / 90;
        if (flipVertical)
        {
            quarterTurns += 2;
        }

        return new VideoOrientation(quarterTurns, flipHorizontal ^ flipVertical);
    }

    /// <summary>
    /// This orientation applied after <paramref name="inner"/>. Mirroring commutes a turn into the other
    /// direction, which is the whole of the arithmetic.
    /// </summary>
    public VideoOrientation Combine(VideoOrientation inner)
    {
        int quarterTurns = _mirrored ? _quarterTurns - inner._quarterTurns : _quarterTurns + inner._quarterTurns;
        return new VideoOrientation(quarterTurns, _mirrored ^ inner._mirrored);
    }

    private static int Normalize(int quarterTurns)
    {
        return ((quarterTurns % 4) + 4) % 4;
    }
}

/// <summary>How a picture of one shape is made to occupy a surface of another.</summary>
public enum VideoResizeMode
{
    /// <summary>All of the picture inside the surface, keeping its shape; what does not reach the edges is bars.</summary>
    Fit,

    /// <summary>All of the surface covered, keeping the picture's shape; what does not fit is cropped.</summary>
    Fill,

    /// <summary>The surface filled outright, whatever that does to the picture's shape.</summary>
    Stretch,

    /// <summary>
    /// One source pixel per surface pixel, centred: the picture is neither enlarged nor shrunk, and whatever runs
    /// past the surface is cropped. The surface is measured in physical pixels, so the host's display scale does
    /// not change what "one pixel" means.
    /// </summary>
    OriginalSize,
}

/// <summary>
/// Where the picture lands on the surface, as one value: its orientation, how it is fitted, and how far it is
/// zoomed and panned. Everything a renderer needs to place a frame is here, so a renderer that can place a
/// picture needs no per-feature support for turning, mirroring or fitting it.
/// <para>
/// The placement is derived from the frame's size and the surface's size rather than stored as a matrix: a
/// surface changes size whenever the window does, and a finished matrix cannot be recomputed from itself. A
/// renderer turns this value into a matrix at the moment it draws, which is where both sizes are known.
/// </para>
/// </summary>
public readonly record struct VideoTransform(
    VideoOrientation Orientation,
    VideoResizeMode ResizeMode,
    double Zoom,
    double PanX,
    double PanY)
{
    /// <summary>Untouched: the picture fitted to the surface, keeping its shape.</summary>
    public static VideoTransform Identity => new(VideoOrientation.Identity, VideoResizeMode.Fit, 1.0, 0.0, 0.0);

    public bool IsIdentity => Orientation.IsIdentity && ResizeMode == VideoResizeMode.Fit && Zoom == 1.0 && PanX == 0.0 && PanY == 0.0;

    /// <summary>
    /// The transform that places the unit square a renderer draws (0 to 1 across and down) onto the surface,
    /// itself given in normalized surface coordinates. Divided into what the picture is and where it goes, so
    /// turning it is applying this to a square and not to its pixels.
    /// </summary>
    public Matrix3x2 GetPlacement(int frameWidth, int frameHeight, int surfaceWidth, int surfaceHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || surfaceWidth <= 0 || surfaceHeight <= 0)
        {
            // Nothing to place: with no shape to fit, the picture covers the surface, which is the identity here.
            return Matrix3x2.Identity;
        }

        bool swapsAxes = Orientation.SwapsAxes;
        double pictureWidth = swapsAxes ? frameHeight : frameWidth;
        double pictureHeight = swapsAxes ? frameWidth : frameHeight;

        double zoom = Zoom > 0 && double.IsFinite(Zoom) ? Zoom : 1.0;
        double scaleX = surfaceWidth / pictureWidth;
        double scaleY = surfaceHeight / pictureHeight;

        (double width, double height) = ResizeMode switch
        {
            VideoResizeMode.Fill => (pictureWidth * Math.Max(scaleX, scaleY), pictureHeight * Math.Max(scaleX, scaleY)),
            VideoResizeMode.Stretch => (surfaceWidth, surfaceHeight),
            VideoResizeMode.OriginalSize => (pictureWidth, pictureHeight),
            _ => (pictureWidth * Math.Min(scaleX, scaleY), pictureHeight * Math.Min(scaleX, scaleY)),
        };

        width *= zoom;
        height *= zoom;

        // The square is the picture before it is turned, so its own shape is the turned picture's the other way round.
        double squareWidth = swapsAxes ? height : width;
        double squareHeight = swapsAxes ? width : height;

        double centerX = surfaceWidth / 2.0 + PanX * surfaceWidth / 2.0;
        double centerY = surfaceHeight / 2.0 + PanY * surfaceHeight / 2.0;

        var placement = Matrix3x2.CreateTranslation(-0.5f, -0.5f);
        placement *= Matrix3x2.CreateScale((float)squareWidth, (float)squareHeight);
        placement *= Matrix3x2.CreateRotation((float)(Orientation.QuarterTurns * Math.PI / 2.0));
        if (Orientation.Mirrored)
        {
            placement *= Matrix3x2.CreateScale(-1.0f, 1.0f);
        }

        placement *= Matrix3x2.CreateTranslation((float)centerX, (float)centerY);

        // Normalized by dividing rather than by multiplying with the reciprocal: a picture that fills the surface
        // has to land on 1.0 and not on the nearest float below it, which would leave the last row or column
        // undrawn and show as a black edge.
        return new Matrix3x2(
            placement.M11 / surfaceWidth,
            placement.M12 / surfaceHeight,
            placement.M21 / surfaceWidth,
            placement.M22 / surfaceHeight,
            placement.M31 / surfaceWidth,
            placement.M32 / surfaceHeight);
    }
}
