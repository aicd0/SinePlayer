// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Microsoft.Graphics.Canvas;

using NUnit.Framework;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Backends.Win2D;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The Win2D renderers of one target share a layer, which is what makes their cues stack instead of landing on the same
/// spot. The sharing is observed through what a shared layer entails — one canvas device — rather than through the layer
/// itself, and it follows the target a renderer is bound to *now*, because a chain can be pointed at another target at
/// any time.
/// </summary>
[TestFixture]
public class Win2DSubtitleLayerTests
{
    [Test]
    public void SharesOneLayerBetweenTheRenderersOfOneTarget()
    {
        Target target = new();

        using Win2DSubtitleRenderer first = new();
        using Win2DSubtitleRenderer second = new();

        if (!Configure(first, target) || !Configure(second, target))
        {
            Assert.Ignore("This machine has no usable graphics device.");
        }

        Assert.That(first.EnsureCanvasDevice(), Is.SameAs(second.EnsureCanvasDevice()), "one layer means one device for its renderers");
    }

    [Test]
    public void KeepsTwoTargetsApart()
    {
        Target first = new();
        Target second = new();

        using Win2DSubtitleRenderer onFirst = new();
        using Win2DSubtitleRenderer onSecond = new();

        if (!Configure(onFirst, first) || !Configure(onSecond, second))
        {
            Assert.Ignore("This machine has no usable graphics device.");
        }

        Assert.That(onFirst.EnsureCanvasDevice(), Is.Not.SameAs(onSecond.EnsureCanvasDevice()), "a layer belongs to one panel");
    }

    [Test]
    public void FollowsTheRendererToItsNewTarget()
    {
        Target first = new();
        Target second = new();

        using Win2DSubtitleRenderer mover = new();
        using Win2DSubtitleRenderer stays = new();

        if (!Configure(mover, first) || !Configure(stays, first))
        {
            Assert.Ignore("This machine has no usable graphics device.");
        }

        CanvasDevice shared = stays.EnsureCanvasDevice();
        Assert.That(mover.EnsureCanvasDevice(), Is.SameAs(shared));

        bool moved = Configure(mover, second);

        Assert.That(moved, Is.True, "the renderer can be pointed at another target");
        Assert.That(mover.EnsureCanvasDevice(), Is.Not.SameAs(shared), "the renderer left the layer it was sharing");
        Assert.That(stays.EnsureCanvasDevice(), Is.SameAs(shared), "and the one that stayed kept its own");
    }

    private static bool Configure(Win2DSubtitleRenderer renderer, IVideoRenderTarget target)
    {
        SubtitleStreamInfo stream = new() { Index = 2, CodecId = "subrip" };

        OperationResult configured = renderer.Configure(stream, [], target);
        return configured.IsSuccessful;
    }

    private sealed class Target(int width = 320, int height = 180, double scale = 1.0) : IVideoRenderTarget
    {
        public int SurfaceWidth => width;

        public int SurfaceHeight => height;

        public double RasterizationScale => scale;

        public event Action? SurfaceSizeChanged
        {
            add { }
            remove { }
        }

        public void AttachPresentationSurface(nint swapChain)
        {
        }
    }
}
