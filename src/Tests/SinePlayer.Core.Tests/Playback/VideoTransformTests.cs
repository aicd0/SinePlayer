// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Numerics;

using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Where a picture lands is arithmetic, so it is checked as arithmetic: the square a renderer draws is taken
/// through the placement and what comes out is read back in surface pixels. Fitting, turning, mirroring and
/// zooming are one transform, so they are checked as one.
/// </summary>
[TestFixture]
public class VideoTransformTests
{
    [Test]
    public void FitsAWideFrameIntoATallerSurface()
    {
        (double x, double y, double width, double height) = Box(VideoTransform.Identity, 1920, 1080, 1600, 1200);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(width, Is.EqualTo(1600).Within(0.01), "the width is what limits it");
            Assert.That(height, Is.EqualTo(900).Within(0.01));
            Assert.That(x, Is.EqualTo(0).Within(0.01));
            Assert.That(y, Is.EqualTo(150).Within(0.01), "the bars are shared between top and bottom");
        }
    }

    [Test]
    public void FitsATallFrameIntoAWiderSurface()
    {
        (double x, double y, double width, double height) = Box(VideoTransform.Identity, 1080, 1920, 1600, 1200);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(height, Is.EqualTo(1200).Within(0.01), "the height is what limits it");
            Assert.That(width, Is.EqualTo(675).Within(0.01));
            Assert.That(x, Is.EqualTo(462.5).Within(0.01), "the bars are shared between left and right");
            Assert.That(y, Is.EqualTo(0).Within(0.01));
        }
    }

    [Test]
    public void FillsASurfaceOfTheSameShape()
    {
        (double x, double y, double width, double height) = Box(VideoTransform.Identity, 1920, 1080, 1920, 1080);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(width, Is.EqualTo(1920).Within(0.01));
            Assert.That(height, Is.EqualTo(1080).Within(0.01));
            Assert.That(x, Is.EqualTo(0).Within(0.01));
            Assert.That(y, Is.EqualTo(0).Within(0.01));
        }
    }

    [Test]
    public void ShrinksToFitASurfaceSmallerThanThePicture()
    {
        (double x, double y, double width, double height) = Box(VideoTransform.Identity, 640, 480, 300, 1000);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(width, Is.EqualTo(300).Within(0.01));
            Assert.That(height, Is.EqualTo(225).Within(0.01));
            Assert.That(x, Is.EqualTo(0).Within(0.01));
            Assert.That(y, Is.EqualTo(387.5).Within(0.01));
        }
    }

    [Test]
    public void CoversTheSurfaceWhenTheFrameHasNoShape()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Box(VideoTransform.Identity, 0, 0, 100, 50), Is.EqualTo((0.0, 0.0, 100.0, 50.0)), "nothing to fit covers the surface");
            Assert.That(Box(VideoTransform.Identity, 100, 50, 0, 0), Is.EqualTo((0.0, 0.0, 0.0, 0.0)), "a surface of no size has nothing to fill");
        }
    }

    [Test]
    public void TurnsAQuarterAndTheBoxIsTheOtherWayRound()
    {
        VideoTransform turned = VideoTransform.Identity with { Orientation = new VideoOrientation(1, false) };

        // A 16:9 picture turned a quarter no longer fits the surface's width, so the height is what limits it —
        // which is the same box a 9:16 picture would have had unturned.
        (double x, double y, double width, double height) = Box(turned, 1920, 1080, 1600, 1200);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(height, Is.EqualTo(1200).Within(0.01));
            Assert.That(width, Is.EqualTo(675).Within(0.01));
            Assert.That(x, Is.EqualTo(462.5).Within(0.01));
            Assert.That(y, Is.EqualTo(0).Within(0.01));
        }
    }

    [Test]
    public void TurnsClockwiseSoTheTopLeftCornerEndsAtTheTopRight()
    {
        VideoTransform turned = VideoTransform.Identity with { Orientation = new VideoOrientation(1, false) };
        (double x, double y, double width, double height) = Box(turned, 1920, 1080, 1600, 1200);

        (double cornerX, double cornerY) = Point(turned, 0, 0, 1920, 1080, 1600, 1200);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cornerX, Is.EqualTo(x + width).Within(0.01), "the source's first column is drawn on the right");
            Assert.That(cornerY, Is.EqualTo(y).Within(0.01), "and its first row on the top");
        }
    }

    [Test]
    public void HalfATurnKeepsTheBoxAndSwapsTheCorners()
    {
        VideoTransform turned = VideoTransform.Identity with { Orientation = new VideoOrientation(2, false) };

        (double x, double y, double width, double height) = Box(turned, 1920, 1080, 1600, 1200);
        (double cornerX, double cornerY) = Point(turned, 0, 0, 1920, 1080, 1600, 1200);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((x, y, width, height), Is.EqualTo(Box(VideoTransform.Identity, 1920, 1080, 1600, 1200)), "half a turn keeps the shape");
            Assert.That(cornerX, Is.EqualTo(x + width).Within(0.01), "the first column is drawn on the right");
            Assert.That(cornerY, Is.EqualTo(y + height).Within(0.01), "and the first row at the bottom");
        }
    }

    [Test]
    public void MirroringKeepsTheBoxAndMovesTheCorners()
    {
        VideoTransform mirrored = VideoTransform.Identity with { Orientation = new VideoOrientation(0, true) };

        (double x, double y, double width, double height) = Box(mirrored, 1920, 1080, 1600, 1200);
        (double cornerX, double cornerY) = Point(mirrored, 0, 0, 1920, 1080, 1600, 1200);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((x, y, width, height), Is.EqualTo(Box(VideoTransform.Identity, 1920, 1080, 1600, 1200)), "mirroring does not change where the picture goes");
            Assert.That(cornerX, Is.EqualTo(x + width).Within(0.01), "the first column is drawn on the right");
            Assert.That(cornerY, Is.EqualTo(y).Within(0.01), "the rows are untouched");
        }
    }

    [Test]
    public void FillsTheSurfaceAndCropsWhatDoesNotFit()
    {
        VideoTransform filled = VideoTransform.Identity with { ResizeMode = VideoResizeMode.Fill };

        (double x, double y, double width, double height) = Box(filled, 1920, 1080, 1600, 1200);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(height, Is.EqualTo(1200).Within(0.01), "the height is what covers it");
            Assert.That(width, Is.EqualTo(2133.33).Within(0.01), "the width then runs past the surface");
            Assert.That(x, Is.EqualTo(-266.67).Within(0.01), "and is cropped evenly on both sides");
        }
    }

    [Test]
    public void StretchesThePictureToTheSurfaceWhateverItsShape()
    {
        VideoTransform stretched = VideoTransform.Identity with { ResizeMode = VideoResizeMode.Stretch };

        (double x, double y, double width, double height) = Box(stretched, 640, 480, 300, 1000);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(x, Is.EqualTo(0).Within(0.01));
            Assert.That(y, Is.EqualTo(0).Within(0.01));
            Assert.That(width, Is.EqualTo(300).Within(0.01), "the picture's own shape is disregarded");
            Assert.That(height, Is.EqualTo(1000).Within(0.01));
        }
    }

    [Test]
    public void ZoomsAboutTheCentre()
    {
        VideoTransform zoomed = VideoTransform.Identity with { Zoom = 2.0 };

        (double x, double y, double width, double height) = Box(zoomed, 1920, 1080, 1600, 1200);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(width, Is.EqualTo(3200).Within(0.01));
            Assert.That(height, Is.EqualTo(1800).Within(0.01));
            Assert.That(x, Is.EqualTo(-800).Within(0.01));
            Assert.That(y, Is.EqualTo(-300).Within(0.01));
        }
    }

    [Test]
    public void PansByAFractionOfTheSurface()
    {
        VideoTransform panned = VideoTransform.Identity with { PanX = 1.0, PanY = -1.0 };

        (double x, double y, _, _) = Box(panned, 1920, 1080, 1600, 1200);
        (double centreX, double centreY, _, _) = Box(VideoTransform.Identity, 1920, 1080, 1600, 1200);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(x, Is.EqualTo(centreX + 800).Within(0.01), "one whole step is half a surface to the right");
            Assert.That(y, Is.EqualTo(centreY - 600).Within(0.01), "and half a surface up");
        }
    }

    [Test]
    public void ZoomIsIgnoredWhenItIsNotANumberWorthUsing()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Box(VideoTransform.Identity with { Zoom = 0.0 }, 1920, 1080, 1600, 1200), Is.EqualTo(Box(VideoTransform.Identity, 1920, 1080, 1600, 1200)));
            Assert.That(Box(VideoTransform.Identity with { Zoom = double.NaN }, 1920, 1080, 1600, 1200), Is.EqualTo(Box(VideoTransform.Identity, 1920, 1080, 1600, 1200)));
        }
    }

    [Test]
    public void AnIdentityTransformIsTheOneThatChangesNothing()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(VideoTransform.Identity.IsIdentity, Is.True);
            Assert.That((VideoTransform.Identity with { Orientation = new VideoOrientation(1, false) }).IsIdentity, Is.False);
            Assert.That((VideoTransform.Identity with { ResizeMode = VideoResizeMode.Fill }).IsIdentity, Is.False);
            Assert.That((VideoTransform.Identity with { Zoom = 1.5 }).IsIdentity, Is.False);
            Assert.That((VideoTransform.Identity with { PanY = 0.25 }).IsIdentity, Is.False);
        }
    }

    [Test]
    public void TurnsAreCountedInQuartersAndReduceToAFullCircle()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new VideoOrientation(5, false), Is.EqualTo(new VideoOrientation(1, false)));
            Assert.That(new VideoOrientation(-1, false), Is.EqualTo(new VideoOrientation(3, false)));
            Assert.That(new VideoOrientation(1, false).RotationDegrees, Is.EqualTo(90));
            Assert.That(new VideoOrientation(1, false).SwapsAxes, Is.True);
            Assert.That(new VideoOrientation(2, false).SwapsAxes, Is.False);
            Assert.That(VideoOrientation.Identity.IsIdentity, Is.True);
        }
    }

    [Test]
    public void TheTwoFlipsBecomeOneMirrorAndATurn()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(VideoOrientation.FromDegrees(90, false, false), Is.EqualTo(new VideoOrientation(1, false)));
            Assert.That(VideoOrientation.FromDegrees(0, true, false), Is.EqualTo(new VideoOrientation(0, true)), "a horizontal flip is the mirror itself");
            Assert.That(VideoOrientation.FromDegrees(0, false, true), Is.EqualTo(new VideoOrientation(2, true)), "a vertical flip is a mirror and half a turn");
            Assert.That(VideoOrientation.FromDegrees(0, true, true), Is.EqualTo(new VideoOrientation(2, false)), "both flips are half a turn");
            Assert.That(VideoOrientation.FromDegrees(180, true, true), Is.EqualTo(new VideoOrientation(0, false)), "half a turn either way cancels out");
        }
    }

    [Test]
    public void RefusesAnAngleThatIsNotAQuarterTurn()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoOrientation.FromDegrees(45, false, false));
    }

    [Test]
    public void ComposesInTheOrderThePictureIsTurned()
    {
        VideoOrientation quarter = new(1, false);
        VideoOrientation half = new(2, false);
        VideoOrientation mirrored = new(0, true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(VideoOrientation.Identity.Combine(quarter), Is.EqualTo(quarter), "nothing before it changes it");
            Assert.That(quarter.Combine(VideoOrientation.Identity), Is.EqualTo(quarter), "and nothing after it either");
            Assert.That(quarter.Combine(quarter), Is.EqualTo(half), "two quarters are half a turn");
            Assert.That(half.Combine(half), Is.EqualTo(VideoOrientation.Identity), "two halves are none");
            Assert.That(mirrored.Combine(mirrored), Is.EqualTo(VideoOrientation.Identity), "mirroring twice is none");

            // A quarter turn after a mirror turns the other way round, which is what makes the group closed.
            Assert.That(mirrored.Combine(quarter), Is.EqualTo(new VideoOrientation(3, true)));
            Assert.That(quarter.Combine(mirrored), Is.EqualTo(new VideoOrientation(1, true)));
        }
    }

    [Test]
    public void ComposedTurnsPlaceThePictureWhereTheSingleOneWould()
    {
        VideoOrientation quarter = new(1, false);
        VideoOrientation composed = quarter.Combine(quarter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(composed, Is.EqualTo(new VideoOrientation(2, false)));
            Assert.That(
                Box(VideoTransform.Identity with { Orientation = composed }, 1920, 1080, 1600, 1200),
                Is.EqualTo(Box(VideoTransform.Identity with { Orientation = new VideoOrientation(2, false) }, 1920, 1080, 1600, 1200)));
        }
    }

    /// <summary>The rectangle the picture occupies, in surface pixels.</summary>
    private static (double X, double Y, double Width, double Height) Box(VideoTransform transform, int frameWidth, int frameHeight, int surfaceWidth, int surfaceHeight)
    {
        (double firstX, double firstY) = Point(transform, 0.0f, 0.0f, frameWidth, frameHeight, surfaceWidth, surfaceHeight);
        (double secondX, double secondY) = Point(transform, 1.0f, 1.0f, frameWidth, frameHeight, surfaceWidth, surfaceHeight);

        double x = Math.Min(firstX, secondX);
        double y = Math.Min(firstY, secondY);
        return (x, y, Math.Abs(secondX - firstX), Math.Abs(secondY - firstY));
    }

    /// <summary>Where one corner of the drawn square lands, in surface pixels.</summary>
    private static (double X, double Y) Point(VideoTransform transform, float u, float v, int frameWidth, int frameHeight, int surfaceWidth, int surfaceHeight)
    {
        Matrix3x2 placement = transform.GetPlacement(frameWidth, frameHeight, surfaceWidth, surfaceHeight);
        var placed = Vector2.Transform(new Vector2(u, v), placement);
        return (placed.X * surfaceWidth, placed.Y * surfaceHeight);
    }
}
