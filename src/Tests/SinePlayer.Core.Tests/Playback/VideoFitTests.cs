// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayer.Core.Playback.Backends.Direct3D11;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Presentation geometry is arithmetic, so it is checked as arithmetic: the picture has to keep its shape
/// whatever the window is shaped like, and the bars have to be equal on both sides.
/// </summary>
[TestFixture]
public class VideoFitTests
{
    [Test]
    public void FitsAWideFrameIntoATallerSurface()
    {
        (int x, int y, int width, int height) = VideoFit.Fit(1920, 1080, 1600, 1200);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(width, Is.EqualTo(1600), "the width is what limits it");
            Assert.That(height, Is.EqualTo(900));
            Assert.That(x, Is.Zero);
            Assert.That(y, Is.EqualTo(150), "the bars are shared between top and bottom");
        }
    }

    [Test]
    public void FitsATallFrameIntoAWiderSurface()
    {
        (int x, int y, int width, int height) = VideoFit.Fit(1080, 1920, 1600, 1200);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(height, Is.EqualTo(1200), "the height is what limits it");
            Assert.That(width, Is.EqualTo(675));
            Assert.That(x, Is.EqualTo(462), "the bars are shared between left and right");
            Assert.That(y, Is.Zero);
        }
    }

    [Test]
    public void FillsASurfaceOfTheSameShape()
    {
        (int x, int y, int width, int height) = VideoFit.Fit(1920, 1080, 1920, 1080);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(width, Is.EqualTo(1920));
            Assert.That(height, Is.EqualTo(1080));
            Assert.That(x, Is.Zero);
            Assert.That(y, Is.Zero);
        }
    }

    [Test]
    public void ShrinksToFitASurfaceSmallerThanThePicture()
    {
        (int x, int y, int width, int height) = VideoFit.Fit(640, 480, 300, 1000);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(width, Is.EqualTo(300));
            Assert.That(height, Is.EqualTo(225));
            Assert.That(x, Is.Zero);
            Assert.That(y, Is.EqualTo(387));
        }
    }

    [Test]
    public void FallsBackToTheSurfaceWhenTheFrameHasNoShape()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(VideoFit.Fit(0, 0, 100, 50), Is.EqualTo((0, 0, 100, 50)));
            Assert.That(VideoFit.Fit(100, 50, 0, 0), Is.EqualTo((0, 0, 1, 1)));
        }
    }
}
