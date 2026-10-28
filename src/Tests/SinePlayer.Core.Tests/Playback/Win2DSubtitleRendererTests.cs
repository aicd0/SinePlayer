// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Microsoft.Graphics.Canvas;

using System.Runtime.InteropServices;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Backends.Win2D;

using Windows.UI;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The subtitle renderer that actually draws: Win2D over Direct2D and DirectWrite, into a swap chain of its own. What is
/// checkable without looking at a screen is that it can build its surface, take a cue, take an empty set, survive a
/// resize and hand the host a native swap chain — the drawing decisions (which words, where) are held by
/// <see cref="AssSubtitleText"/> and the pipeline's tests.
/// </summary>
[TestFixture]
public class Win2DSubtitleRendererTests
{
    /// <summary>
    /// Both kinds of cue are drawn by the same renderer, and the kind is part of the signature: a chain assembled for text
    /// must not be kept when the same track is replaced by one carrying pictures. A format neither knows is refused here
    /// rather than at the first drawing.
    /// </summary>
    [Test]
    public void OffersItselfForTextAndBitmapCues()
    {
        Win2DSubtitleRendererFactory factory = new();

        using (Assert.EnterMultipleScope())
        {
            OperationResult<IComponentCreator<ISubtitleRenderer>> text = factory.Create(SubtitleCueFormat.Ass);
            Assert.That(text.IsSuccessful, Is.True, text.Message);
            Assert.That(text.Result.Signature, Does.Contain("ass"), "the format is part of what the signature names");

            OperationResult<IComponentCreator<ISubtitleRenderer>> bitmap = factory.Create(SubtitleCueFormat.BitmapBgra);
            Assert.That(bitmap.IsSuccessful, Is.True, bitmap.Message);
            Assert.That(bitmap.Result.Signature, Does.Contain("bitmap"), "and a picture is a different renderer to keep");
            Assert.That(bitmap.Result.Signature, Is.Not.EqualTo(text.Result.Signature));

            OperationResult<IComponentCreator<ISubtitleRenderer>> refused = factory.Create((SubtitleCueFormat)99);
            Assert.That(refused.IsSuccessful, Is.False, "a kind of cue this renderer does not know is refused, not guessed at");
        }
    }

    [Test]
    public void BuildsASurfaceAndDrawsIntoIt()
    {
        RecordingTarget target = new(640, 360, 1.0);
        using Win2DSubtitleRenderer renderer = new();

        OperationResult configured = renderer.Configure(Stream(), target);
        if (!configured.IsSuccessful)
        {
            Assert.Ignore($"This machine has no usable graphics device: {configured.Message}");
        }

        Assert.That(target.Attached, Is.Not.Zero, "the host has to be handed a native swap chain to compose");

        SubtitleCue cue = new()
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(2),
            Text = "0,0,Default,,0,0,0,,Two\\Nlines",
        };

        OperationResult drawn;
        try
        {
            drawn = renderer.Show([cue], SubtitlePlacement.Identity);
        }
        finally
        {
            cue.Dispose();
        }

        Assert.That(drawn.IsSuccessful, Is.True, drawn.Message);
    }

    [Test]
    public void DrawsAnEmptySetAsAnEmptyFrame()
    {
        // "Nothing is on screen" is a drawing too: the panel is composed over the picture, so the frame has to be
        // presented transparent rather than simply not drawn.
        RecordingTarget target = new(320, 180, 1.0);
        using Win2DSubtitleRenderer renderer = new();

        OperationResult configured = renderer.Configure(Stream(), target);
        if (!configured.IsSuccessful)
        {
            Assert.Ignore($"This machine has no usable graphics device: {configured.Message}");
        }

        OperationResult cleared = renderer.Show([], SubtitlePlacement.Identity);
        Assert.That(cleared.IsSuccessful, Is.True, cleared.Message);
    }

    [Test]
    public void TakesANewSurfaceSizeWhilePlaying()
    {
        RecordingTarget target = new(640, 360, 1.0);
        using Win2DSubtitleRenderer renderer = new();

        OperationResult configured = renderer.Configure(Stream(), target);
        if (!configured.IsSuccessful)
        {
            Assert.Ignore($"This machine has no usable graphics device: {configured.Message}");
        }

        nint attached = target.Attached;
        target.SetSize(800, 600);
        target.RaiseSizeChanged();

        SubtitleCue cue = new()
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(2),
            Text = "0,0,Default,,0,0,0,,After a resize",
        };

        OperationResult drawn;
        try
        {
            drawn = renderer.Show([cue], SubtitlePlacement.Identity);
        }
        finally
        {
            cue.Dispose();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(drawn.IsSuccessful, Is.True, drawn.Message);
            Assert.That(target.Attached, Is.EqualTo(attached), "resizing replaces the buffers of the same swap chain, so the panel stays bound to it");
        }
    }

    [Test]
    public void CarriesAPlacementWithoutLeavingTheSurface()
    {
        // An adjustment big enough to push the text off the edge is cut back rather than obeyed: subtitles are adjusted
        // to be read, and a surface that later shrinks must not strand them.
        RecordingTarget target = new(400, 200, 1.0);
        using Win2DSubtitleRenderer renderer = new();

        OperationResult configured = renderer.Configure(Stream(), target);
        if (!configured.IsSuccessful)
        {
            Assert.Ignore($"This machine has no usable graphics device: {configured.Message}");
        }

        SubtitleCue cue = new()
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(2),
            Text = "0,0,Default,,0,0,0,,Off the edge",
        };

        OperationResult drawn;
        try
        {
            drawn = renderer.Show([cue], new SubtitlePlacement(-50.0, 50.0));
        }
        finally
        {
            cue.Dispose();
        }

        Assert.That(drawn.IsSuccessful, Is.True, drawn.Message);
    }

    [Test]
    public void PutsPixelsOnTheSurfaceAndTakesThemAwayAgain()
    {
        // The point of the whole renderer: words become pixels, and an empty set leaves none. A call that succeeds while
        // drawing nothing looks exactly like a working one from the outside, which is why the pixels are read back here —
        // against an offscreen target, since a presented swap chain cannot be read after the fact.
        using Win2DSubtitleRenderer renderer = new();

        CanvasRenderTarget? target = null;
        try
        {
            CanvasDevice device;
            try
            {
                device = renderer.EnsureCanvasDevice();
            }
            catch (Exception exception)
            {
                Assert.Ignore($"This machine has no usable graphics device: {exception.Message}");
                return;
            }

            const int width = 480;
            const int height = 200;
            target = new CanvasRenderTarget(device, width, height, 96.0f);

            SubtitleCue cue = new()
            {
                Start = TimeSpan.Zero,
                End = TimeSpan.FromSeconds(2),
                Text = "0,0,Default,,0,0,0,,Visible words",
            };

            try
            {
                using (CanvasDrawingSession session = target.CreateDrawingSession())
                {
                    session.Clear(default(Color));
                    renderer.DrawCues(session, [cue], SubtitlePlacement.Identity, width, height);
                }

                Assert.That(DrawnPixels(target.GetPixelBytes()), Is.GreaterThan(0), "the cue has to reach the target as pixels");

                using (CanvasDrawingSession session = target.CreateDrawingSession())
                {
                    session.Clear(default(Color));
                    renderer.DrawCues(session, [], SubtitlePlacement.Identity, width, height);
                }

                Assert.That(DrawnPixels(target.GetPixelBytes()), Is.Zero, "and an empty set has to leave nothing behind");
            }
            finally
            {
                cue.Dispose();
            }
        }
        finally
        {
            target?.Dispose();
        }
    }

    /// <summary>
    /// A bitmap cue is drawn where its fractions say, scaled to the surface: the picture of a DVD subtitle arrives as an
    /// image positioned inside the frame it was authored against, and it has to land there rather than at the corner.
    /// </summary>
    [Test]
    public void DrawsABitmapCueWhereItsFractionsSay()
    {
        using Win2DSubtitleRenderer renderer = new();

        CanvasRenderTarget? target = null;
        try
        {
            CanvasDevice device;
            try
            {
                device = renderer.EnsureCanvasDevice();
            }
            catch (Exception exception)
            {
                Assert.Ignore($"This machine has no usable graphics device: {exception.Message}");
                return;
            }

            const int width = 400;
            const int height = 200;
            target = new CanvasRenderTarget(device, width, height, 96.0f);

            // A quarter of the width, half the height, with its top-left at the centre of the surface.
            using SubtitleCue cue = BitmapCue(left: 0.5, top: 0.5, imageWidth: 0.25, imageHeight: 0.5);

            using (CanvasDrawingSession session = target.CreateDrawingSession())
            {
                session.Clear(default(Color));
                renderer.DrawCues(session, [cue], SubtitlePlacement.Identity, width, height);
            }

            byte[] pixels = target.GetPixelBytes();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(AlphaAt(pixels, width, 250, 150), Is.EqualTo(255), "inside the cue: drawn");
                Assert.That(AlphaAt(pixels, width, 199, 99), Is.Zero, "just outside it: left alone");
                Assert.That(AlphaAt(pixels, width, 5, 5), Is.Zero, "and the corner it was not placed at is empty");
            }
        }
        finally
        {
            target?.Dispose();
        }
    }

    /// <summary>A cue carrying a small opaque image, positioned and sized as fractions of the frame.</summary>
    private static SubtitleCue BitmapCue(double left, double top, double imageWidth, double imageHeight)
    {
        return new SubtitleCue
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(2),
            Image = new SubtitleImage
            {
                Buffer = FakeImage.Create(4, 2),
                Left = left,
                Top = top,
                Width = imageWidth,
                Height = imageHeight,
            },
        };
    }

    /// <summary>The alpha of one pixel of a blue-green-red-alpha buffer.</summary>
    private static byte AlphaAt(byte[] pixels, int width, int x, int y)
    {
        return pixels[((y * width) + x) * 4 + 3];
    }

    /// <summary>An opaque white image in unmanaged memory, which is what a decoder hands over.</summary>
    private sealed class FakeImage : ISubtitleImageBuffer
    {
        private nint _pixels;

        private FakeImage(nint pixels, int width, int height)
        {
            _pixels = pixels;
            Width = width;
            Height = height;
        }

        public int Width { get; }

        public int Height { get; }

        public int Stride => Width * 4;

        public nint Pixels => _pixels;

        public long Length => Stride * Height;

        internal static FakeImage Create(int width, int height)
        {
            int stride = width * 4;
            nint pixels = Marshal.AllocHGlobal(stride * height);
            for (int index = 0; index < stride * height; index++)
            {
                Marshal.WriteByte(pixels, index, 0xFF);
            }

            return new FakeImage(pixels, width, height);
        }

        public void Dispose()
        {
            nint pixels = _pixels;
            if (pixels == 0)
            {
                return;
            }

            _pixels = 0;
            Marshal.FreeHGlobal(pixels);
        }
    }

    /// <summary>How many pixels carry anything at all, in the blue-green-red-alpha buffer the surface reads back as.</summary>
    private static int DrawnPixels(byte[] pixels)
    {
        int drawn = 0;
        for (int index = 0; index + 3 < pixels.Length; index += 4)
        {
            if (pixels[index] != 0 || pixels[index + 1] != 0 || pixels[index + 2] != 0 || pixels[index + 3] != 0)
            {
                drawn++;
            }
        }

        return drawn;
    }

    private static SubtitleStreamInfo Stream()
    {
        return new SubtitleStreamInfo { Index = 2, CodecId = "subrip" };
    }

    /// <summary>
    /// Stands in for the host's panel: it records the swap chain it was handed and reports the size the renderer asks
    /// for, which is all a renderer needs from a surface to be exercised without a window.
    /// </summary>
    private sealed class RecordingTarget(int width, int height, double scale) : IVideoRenderTarget
    {
        private int _width = width;
        private int _height = height;

        public int SurfaceWidth => _width;

        public int SurfaceHeight => _height;

        public double RasterizationScale => scale;

        public nint Attached { get; private set; }

        public event Action? SurfaceSizeChanged;

        public void AttachPresentationSurface(nint swapChain)
        {
            Attached = swapChain;
        }

        public void SetSize(int newWidth, int newHeight)
        {
            _width = newWidth;
            _height = newHeight;
        }

        public void RaiseSizeChanged()
        {
            SurfaceSizeChanged?.Invoke();
        }
    }
}
