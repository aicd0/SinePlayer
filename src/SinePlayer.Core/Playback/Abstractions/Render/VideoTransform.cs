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

public enum VideoScalingMode
{
    Point,
    Bilinear,
}

internal readonly record struct PlacedPicture(double Width, double Height, double CenterX, double CenterY);

public readonly record struct VideoTransform(
    VideoOrientation Orientation,
    VideoResizeMode ResizeMode,
    double Zoom,
    double PanX,
    double PanY,
    VideoScalingMode Scaling = VideoScalingMode.Bilinear)
{
    public static VideoTransform Identity => new(VideoOrientation.Identity, VideoResizeMode.Fit, 1.0, 0.0, 0.0);

    public bool IsIdentity => Orientation.IsIdentity && ResizeMode == VideoResizeMode.Fit && Zoom == 1.0 && PanX == 0.0 && PanY == 0.0;

    /// <summary>
    /// The matrix that draws the picture's own square onto the surface: the picture's coordinates are 0 to 1 along each of
    /// its axes, and the surface's are 0 to 1 across and down. It is what the picture is drawn with.
    /// </summary>
    public Matrix3x2 GetPictureToSurfaceMatrix(int frameWidth, int frameHeight, int surfaceWidth, int surfaceHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || surfaceWidth <= 0 || surfaceHeight <= 0)
        {
            return Matrix3x2.Identity;
        }

        PlacedPicture picture = GetPlacedPicture(frameWidth, frameHeight, surfaceWidth, surfaceHeight);

        var placement = Matrix3x2.CreateTranslation(-0.5f, -0.5f);
        placement *= Matrix3x2.CreateScale((float)picture.Width, (float)picture.Height);
        placement *= Matrix3x2.CreateRotation((float)(Orientation.QuarterTurns * Math.PI / 2.0));
        if (Orientation.Mirrored)
        {
            placement *= Matrix3x2.CreateScale(-1.0f, 1.0f);
        }

        placement *= Matrix3x2.CreateTranslation((float)picture.CenterX, (float)picture.CenterY);

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

    /// <summary>
    /// The picture as this transform places it on the surface, before the surface crops it. The matrix the picture is
    /// drawn with is built from this, so the two cannot disagree about what the transform does.
    /// </summary>
    internal PlacedPicture GetPlacedPicture(int frameWidth, int frameHeight, int surfaceWidth, int surfaceHeight)
    {
        (double pictureWidth, double pictureHeight) = GetPictureSize(frameWidth, frameHeight);

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
        bool swapsAxes = Orientation.SwapsAxes;
        double squareWidth = swapsAxes ? height : width;
        double squareHeight = swapsAxes ? width : height;

        double centerX = surfaceWidth / 2.0 + PanX * surfaceWidth / 2.0;
        double centerY = surfaceHeight / 2.0 + PanY * surfaceHeight / 2.0;

        return new PlacedPicture(squareWidth, squareHeight, centerX, centerY);
    }

    /// <summary>
    /// The picture's own size in pixels: the one the container holds, turned by the orientation. Its shape is what the
    /// words are laid out in, since a script scaled onto another shape is drawn stretched.
    /// </summary>
    internal (double Width, double Height) GetPictureSize(int frameWidth, int frameHeight)
    {
        return Orientation.SwapsAxes ? (frameHeight, frameWidth) : (frameWidth, frameHeight);
    }
}
