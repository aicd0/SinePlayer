// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Numerics;

namespace SinePlayer.Core.Playback.Abstractions.Render;

public readonly record struct VideoOrientation
{
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

    private readonly int _quarterTurns;
    private readonly bool _mirrored;

    /// <param name="quarterTurns">Quarter turns clockwise; any whole number, reduced to 0 to 3.</param>
    /// <param name="mirrored">Whether the picture is mirrored left to right.</param>
    public VideoOrientation(int quarterTurns, bool mirrored)
    {
        _quarterTurns = Normalize(quarterTurns);
        _mirrored = mirrored;
    }

    public static VideoOrientation Identity => default;

    public int QuarterTurns => _quarterTurns;

    public bool Mirrored => _mirrored;

    public int RotationDegrees => _quarterTurns * 90;

    public bool IsIdentity => _quarterTurns == 0 && !_mirrored;

    public bool SwapsAxes => (_quarterTurns & 1) == 1;

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

public enum VideoResizeMode
{
    Fit,
    Fill,
    Stretch,
    OriginalSize,
}

public readonly record struct VideoTransform(
    VideoOrientation Orientation,
    VideoResizeMode ResizeMode,
    double Zoom,
    double PanX,
    double PanY)
{
    public static VideoTransform Identity => new(VideoOrientation.Identity, VideoResizeMode.Fit, 1.0, 0.0, 0.0);

    public bool IsIdentity => Orientation.IsIdentity && ResizeMode == VideoResizeMode.Fit && Zoom == 1.0 && PanX == 0.0 && PanY == 0.0;

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
