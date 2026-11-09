// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Microsoft.Graphics.Canvas;

using NUnit.Framework;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Subtitle;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The compositor is the panel's, not the pipeline's: one per render target, holding one layer per subtitle renderer, so
/// two chains on one panel are composed and two panels never meet. What is asserted here is that arrangement and that
/// composing several layers actually draws — the pixels of a composition are checked on screen, not here.
/// </summary>
[TestFixture]
public class SubtitleCompositorTests
{
    [Test]
    public void SharesOneCompositorBetweenTheLayersOfOneTarget()
    {
        Target target = new();

        var first = SubtitleCompositor.Acquire(target);
        var second = SubtitleCompositor.Acquire(target);
        try
        {
            Assert.That(second, Is.SameAs(first), "one target is one swap chain, so one compositor");
        }
        finally
        {
            first.Release();
            second.Release();
        }
    }

    [Test]
    public void KeepsTwoTargetsApart()
    {
        Target first = new();
        Target second = new();

        var firstCompositor = SubtitleCompositor.Acquire(first);
        var secondCompositor = SubtitleCompositor.Acquire(second);
        try
        {
            Assert.That(secondCompositor, Is.Not.SameAs(firstCompositor), "two panels have nothing to compose together");
        }
        finally
        {
            firstCompositor.Release();
            secondCompositor.Release();
        }
    }

    [Test]
    public void LetsGoOfACompositorWithItsLastLayer()
    {
        Target target = new();

        var acquired = SubtitleCompositor.Acquire(target);
        acquired.Release();

        var again = SubtitleCompositor.Acquire(target);
        try
        {
            Assert.That(again, Is.Not.SameAs(acquired), "nothing holds the compositor once its last layer is gone");
        }
        finally
        {
            again.Release();
        }
    }

    [Test]
    public void ComposesTheLayersOfOneTarget()
    {
        Target target = new(320, 180, 1.0);

        using SubtitleSurface first = new();
        using SubtitleSurface second = new();

        first.BindTarget(target);
        second.BindTarget(target);

        OperationResult firstSized = first.EnsureSurface();
        OperationResult secondSized = second.EnsureSurface();
        if (!firstSized.IsSuccessful || !secondSized.IsSuccessful)
        {
            Assert.Ignore($"This machine has no usable graphics device: {firstSized.Message}{secondSized.Message}");
        }

        OperationResult firstDrawn = first.DrawAndPresent(session => Fill(session, first));
        OperationResult secondDrawn = second.DrawAndPresent(session => Fill(session, second));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstDrawn.IsSuccessful, Is.True, firstDrawn.Message);
            Assert.That(secondDrawn.IsSuccessful, Is.True, secondDrawn.Message);
            Assert.That(target.Attachments, Is.EqualTo(1), "one swap chain is handed to the panel however many layers draw into it");
        }
    }

    private static void Fill(CanvasDrawingSession session, SubtitleSurface layer)
    {
        session.FillRectangle(0, layer.Height / 2.0f, layer.Width, layer.Height / 2.0f, Windows.UI.Color.FromArgb(255, 255, 255, 255));
    }

    /// <summary>A host panel that records what it was handed, so "one swap chain per target" is observable.</summary>
    private sealed class Target(int width = 320, int height = 240, double scale = 1.0) : IVideoRenderTarget
    {
        public int SurfaceWidth => width;

        public int SurfaceHeight => height;

        public double RasterizationScale => scale;

        public int Attachments { get; private set; }

        public event Action? SurfaceSizeChanged
        {
            add { }
            remove { }
        }

        public void AttachPresentationSurface(nint swapChain)
        {
            Attachments++;
        }
    }
}
