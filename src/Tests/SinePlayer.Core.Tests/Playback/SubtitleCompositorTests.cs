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

        OperationResult firstDrawn = first.DrawAndPresent((session, _, _) => Fill(session, first));
        OperationResult secondDrawn = second.DrawAndPresent((session, _, _) => Fill(session, second));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstDrawn.IsSuccessful, Is.True, firstDrawn.Message);
            Assert.That(secondDrawn.IsSuccessful, Is.True, secondDrawn.Message);
            Assert.That(target.Attachments, Is.EqualTo(1), "one swap chain is handed to the panel however many layers draw into it");
        }
    }

    /// <summary>
    /// Several chains of one panel draw on their own render threads and share the panel's device, and a resize makes them all
    /// redraw at once. The bitmap of a layer is made and unmade with that device, so its lifetime is ordered by the same lock
    /// that orders a frame: a resize on one thread must never dispose what another is drawing with. What that would do is a
    /// fatal WinRT failure rather than a managed exception, which is why this is checked by doing it rather than by asserting.
    /// </summary>
    [Test]
    public void DrawsAndResizesFromSeveralThreadsAtOnce()
    {
        Target target = new(320, 180, 1.0);

        using SubtitleSurface first = new();
        using SubtitleSurface second = new();
        first.BindTarget(target);
        second.BindTarget(target);

        OperationResult sized = first.EnsureSurface();
        if (!sized.IsSuccessful)
        {
            Assert.Ignore($"This machine has no usable graphics device: {sized.Message}");
        }

        using CancellationTokenSource stop = new();
        List<string> failures = [];

        void Record(OperationResult result)
        {
            if (result.IsSuccessful)
            {
                return;
            }

            lock (failures)
            {
                failures.Add(result.Message);
            }
        }

        void Draw(SubtitleSurface layer)
        {
            while (!stop.IsCancellationRequested)
            {
                Record(layer.EnsureSurface());
                Record(layer.DrawAndPresent((session, _, _) => Fill(session, layer)));
            }
        }

        Thread resizing = new(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                target.Resize(target.Width == 320 ? 400 : 320, target.Height == 180 ? 240 : 180);
                Thread.Sleep(1);
            }
        });

        Thread drawingFirst = new(() => Draw(first));
        Thread drawingSecond = new(() => Draw(second));
        resizing.Start();
        drawingFirst.Start();
        drawingSecond.Start();

        Thread.Sleep(1000);
        stop.Cancel();
        resizing.Join();
        drawingFirst.Join();
        drawingSecond.Join();

        OperationResult drawn = first.DrawAndPresent((session, _, _) => Fill(session, first));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failures, Is.Empty, $"every frame of the storm is a frame: {string.Join("; ", failures.Distinct())}");
            Assert.That(drawn.IsSuccessful, Is.True, "and the surface still draws once it is over");
        }
    }

    /// <summary>
    /// Letting go of a layer waits for a frame in flight and then leaves nothing to draw with: a chain disposed while another
    /// thread is drawing must not leave that thread drawing into a bitmap that has been released.
    /// </summary>
    [Test]
    public void LetsGoOfItsBitmapWhenItIsDisposed()
    {
        Target target = new(320, 180, 1.0);

        using CancellationTokenSource stop = new();
        List<Exception> thrown = [];
        int drawn = 0;

        SubtitleSurface surface = new();
        surface.BindTarget(target);

        OperationResult sized = surface.EnsureSurface();
        if (!sized.IsSuccessful)
        {
            surface.Dispose();
            Assert.Ignore($"This machine has no usable graphics device: {sized.Message}");
        }

        Thread drawing = new(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (surface.DrawAndPresent((session, _, _) => Fill(session, surface)).IsSuccessful)
                    {
                        Interlocked.Increment(ref drawn);
                    }
                }
                catch (Exception exception)
                {
                    lock (thrown)
                    {
                        thrown.Add(exception);
                    }
                }
            }
        });

        drawing.Start();
        Thread.Sleep(50);
        surface.Dispose();
        stop.Cancel();
        drawing.Join();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.Empty, "a disposed layer refuses a frame rather than throwing at the caller");
            Assert.That(drawn, Is.GreaterThan(0), "frames were drawn before it was let go");
            Assert.That(surface.DrawAndPresent((session, _, _) => Fill(session, surface)).IsSuccessful, Is.False,
                "and it draws nothing once it is gone, because the compositor retired its bitmap");
        }
    }

    private static void Fill(CanvasDrawingSession session, SubtitleSurface layer)
    {
        session.FillRectangle(0, layer.Height / 2.0f, layer.Width, layer.Height / 2.0f, Windows.UI.Color.FromArgb(255, 255, 255, 255));
    }

    /// <summary>A host panel that records what it was handed, so "one swap chain per target" is observable.</summary>
    private sealed class Target(int width = 320, int height = 240, double scale = 1.0) : IVideoRenderTarget
    {
        private int _width = width;

        private int _height = height;

        public int Width => Volatile.Read(ref _width);

        public int Height => Volatile.Read(ref _height);

        public int SurfaceWidth => Volatile.Read(ref _width);

        public int SurfaceHeight => Volatile.Read(ref _height);

        public double RasterizationScale => scale;

        public int Attachments { get; private set; }

        public event Action? SurfaceSizeChanged
        {
            add { }
            remove { }
        }

        internal void Resize(int width, int height)
        {
            Volatile.Write(ref _width, width);
            Volatile.Write(ref _height, height);
        }

        public void AttachPresentationSurface(nint swapChain)
        {
            Attachments++;
        }
    }
}
