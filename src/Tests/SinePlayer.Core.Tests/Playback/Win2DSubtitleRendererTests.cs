// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using Microsoft.Graphics.Canvas;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Backends.Direct3D11;
using SinePlayer.Core.Playback.Backends.Win2D;
using SinePlayer.Core.Playback.Subtitle;

using Windows.UI;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The subtitle renderer that puts words on screen: Win2D over Direct2D and DirectWrite, into a swap chain of its own. What
/// is checkable without looking at a screen is that it can build its surface, take a cue, take an empty set, survive a
/// resize and hand the host a native swap chain, and how the drawing it hands over lands on the target — the drawing itself
/// is <see cref="Win2DSubtitleLayer"/>'s, so it is driven there.
/// </summary>
[TestFixture]
public class Win2DSubtitleRendererTests
{
    private static CanvasDevice? _detachedDevice;

    private static D3D11Device? _detachedDeviceOwner;
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
            OperationResult<IComponentCreator<ISubtitleRenderer>> text = factory.Create(Stream(), SubtitleCueFormat.Ass);
            Assert.That(text.IsSuccessful, Is.True, text.Message);
            Assert.That(text.Result.Signature, Does.Contain("ass"), "the format is part of what the signature names");

            OperationResult<IComponentCreator<ISubtitleRenderer>> bitmap = factory.Create(Stream(), SubtitleCueFormat.BitmapBgra);
            Assert.That(bitmap.IsSuccessful, Is.True, bitmap.Message);
            Assert.That(bitmap.Result.Signature, Does.Contain("bitmap"), "and a picture is a different renderer to keep");
            Assert.That(bitmap.Result.Signature, Is.Not.EqualTo(text.Result.Signature));

            OperationResult<IComponentCreator<ISubtitleRenderer>> refused = factory.Create(Stream(), (SubtitleCueFormat)99);
            Assert.That(refused.IsSuccessful, Is.False, "a kind of cue this renderer does not know is refused, not guessed at");
        }
    }

    [Test]
    public void BuildsASurfaceAndDrawsIntoIt()
    {
        RecordingTarget target = new(640, 360, 1.0);
        using Win2DSubtitleRenderer renderer = new();

        OperationResult configured = renderer.Configure(Stream(), [], target);
        if (!configured.IsSuccessful)
        {
            Assert.Ignore($"This machine has no usable graphics device: {configured.Message}");
        }

        Assert.That(target.Attached, Is.Not.Zero, "the host has to be handed a native swap chain to compose");

        SubtitleCue cue = new()
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(2),
            Text = "0,Default,,0,0,0,,Two\\Nlines",
        };

        OperationResult drawn;
        try
        {
            drawn = renderer.Show([cue], TimeSpan.Zero, SubtitlePresentation.Identity);
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

        OperationResult configured = renderer.Configure(Stream(), [], target);
        if (!configured.IsSuccessful)
        {
            Assert.Ignore($"This machine has no usable graphics device: {configured.Message}");
        }

        OperationResult cleared = renderer.Show([], TimeSpan.Zero, SubtitlePresentation.Identity);
        Assert.That(cleared.IsSuccessful, Is.True, cleared.Message);
    }

    [Test]
    public void TakesANewSurfaceSizeWhilePlaying()
    {
        RecordingTarget target = new(640, 360, 1.0);
        using Win2DSubtitleRenderer renderer = new();

        OperationResult configured = renderer.Configure(Stream(), [], target);
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
            Text = "0,Default,,0,0,0,,After a resize",
        };

        OperationResult drawn;
        try
        {
            drawn = renderer.Show([cue], TimeSpan.Zero, SubtitlePresentation.Identity);
        }
        finally
        {
            cue.Dispose();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(drawn.IsSuccessful, Is.True, drawn.Message);
            Assert.That(target.Attached, Is.Not.EqualTo(attached), "a chain Win2D has drawn into cannot be resized, so the panel is handed a new one");
        }
    }

    [Test]
    public void CarriesAPlacementWithoutLeavingTheSurface()
    {
        // An adjustment big enough to push the text off the edge is cut back rather than obeyed: subtitles are adjusted
        // to be read, and a surface that later shrinks must not strand them.
        RecordingTarget target = new(400, 200, 1.0);
        using Win2DSubtitleRenderer renderer = new();

        OperationResult configured = renderer.Configure(Stream(), [], target);
        if (!configured.IsSuccessful)
        {
            Assert.Ignore($"This machine has no usable graphics device: {configured.Message}");
        }

        SubtitleCue cue = new()
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(2),
            Text = "0,Default,,0,0,0,,Off the edge",
        };

        OperationResult drawn;
        try
        {
            drawn = renderer.Show([cue], TimeSpan.Zero, new SubtitlePresentation(-50.0, 50.0));
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
        // The point of the whole backend: words become pixels, and an empty set leaves none. A call that succeeds while
        // drawing nothing looks exactly like a working one from the outside, which is why the pixels are read back here —
        // against an offscreen target, since a presented swap chain cannot be read after the fact.
        CanvasRenderTarget? target = null;
        try
        {
            CanvasDevice device;
            try
            {
                device = EnsureDetachedDevice();
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
                Text = "0,Default,,0,0,0,,Visible words",
            };

            try
            {
                using (CanvasDrawingSession session = target.CreateDrawingSession())
                {
                    session.Clear(default(Color));
                    Win2DSubtitleLayer.DrawCues(session, device, [cue], SubtitlePresentation.Identity, width, height);
                }

                Assert.That(DrawnPixels(target.GetPixelBytes()), Is.GreaterThan(0), "the cue has to reach the target as pixels");

                using (CanvasDrawingSession session = target.CreateDrawingSession())
                {
                    session.Clear(default(Color));
                    Win2DSubtitleLayer.DrawCues(session, device, [], SubtitlePresentation.Identity, width, height);
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
    /// A host that asks for a size gets that size: the same words cover more rows of the surface when the number grows,
    /// which is the whole of what the setting means.
    /// </summary>
    [Test]
    public void DrawsTheTextAtTheSizeItIsGiven()
    {
        CanvasDevice device;
        try
        {
            device = EnsureDetachedDevice();
        }
        catch (Exception exception)
        {
            Assert.Ignore($"This machine has no usable graphics device: {exception.Message}");
            return;
        }

        int small = InkRows(device, 480, 240, new SubtitlePresentation(0.0, 0.0, default, null, 12.0));
        int large = InkRows(device, 480, 240, new SubtitlePresentation(0.0, 0.0, default, null, 48.0));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(small, Is.GreaterThan(0), "the size asked for draws words");
            Assert.That(large, Is.GreaterThan(small * 2), "and four times the size covers several times the rows");
        }
    }

    /// <summary>
    /// Zero means "work it out from the surface", which is what the drawing did before a size could be asked for: an
    /// explicit size of the value it works out for itself has to draw the same picture.
    /// </summary>
    [Test]
    public void KeepsTheSizeItWorksOutForItselfWhenToldNone()
    {
        CanvasDevice device;
        try
        {
            device = EnsureDetachedDevice();
        }
        catch (Exception exception)
        {
            Assert.Ignore($"This machine has no usable graphics device: {exception.Message}");
            return;
        }

        const int height = 300;
        double automatic = Math.Clamp(height * 0.045, 12.0, 72.0);

        int unspecified = InkRows(device, 480, height, SubtitlePresentation.Identity);
        int sameSize = InkRows(device, 480, height, new SubtitlePresentation(0.0, 0.0, default, null, automatic));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unspecified, Is.GreaterThan(0), "an automatic size still draws");
            Assert.That(unspecified, Is.EqualTo(sameSize).Within(1), "and it is the size the surface alone gives");
        }
    }

    /// <summary>How many rows of the surface carry ink, drawing one cue with the presentation given.</summary>
    private static int InkRows(CanvasDevice device, int width, int height, SubtitlePresentation presentation)
    {
        byte[] pixels = RenderInk(device, width, height, presentation);
        int rows = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (AlphaAt(pixels, width, x, y) == 0)
                {
                    continue;
                }

                rows++;
                break;
            }
        }

        return rows;
    }

    /// <summary>How many pixels carry ink, drawing one cue with the presentation given.</summary>
    private static int InkPixels(CanvasDevice device, int width, int height, SubtitlePresentation presentation)
    {
        byte[] pixels = RenderInk(device, width, height, presentation);
        int count = 0;
        for (int index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] != 0)
            {
                count++;
            }
        }

        return count;
    }

    private static byte[] RenderInk(CanvasDevice device, int width, int height, SubtitlePresentation presentation)
    {
        return RenderInk(device, width, height, presentation, "0,Default,,0,0,0,,Visible words");
    }

    private static byte[] RenderInk(CanvasDevice device, int width, int height, SubtitlePresentation presentation, string text)
    {
        using CanvasRenderTarget target = new(device, width, height, 96.0f);
        using SubtitleCue cue = Cue(text);

        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(default(Color));
            Win2DSubtitleLayer.DrawCues(session, device, [cue], presentation, width, height);
        }

        return target.GetPixelBytes();
    }

    /// <summary>
    /// A border is drawn as a stroke around the glyph outlines, twice the width asked for so that filling the words over
    /// it leaves the width wanted outside them: the picture therefore holds more ink, and more rows of it, once a width
    /// is set.
    /// </summary>
    [Test]
    public void DrawsABorderAroundTheWordsWhenAWidthIsGiven()
    {
        CanvasDevice device;
        try
        {
            device = EnsureDetachedDevice();
        }
        catch (Exception exception)
        {
            Assert.Ignore($"This machine has no usable graphics device: {exception.Message}");
            return;
        }

        SubtitlePresentation plain = new(0.0, 0.0, default, null, 24.0);
        SubtitlePresentation bordered = plain with { OutlineWidth = 6.0 };

        int plainPixels = InkPixels(device, 480, 240, plain);
        int borderedPixels = InkPixels(device, 480, 240, bordered);
        int plainRows = InkRows(device, 480, 240, plain);
        int borderedRows = InkRows(device, 480, 240, bordered);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plainPixels, Is.GreaterThan(0), "the words alone are drawn");
            Assert.That(borderedPixels, Is.GreaterThan(plainPixels), "a border adds ink around them");
            Assert.That(borderedRows, Is.GreaterThan(plainRows), "and it reaches above and below them");
        }
    }

    /// <summary>
    /// This drawing has no picture-relative coordinates: it lays lines out itself against the surface, so the frame a
    /// script would be placed in changes nothing about where the words go.
    /// </summary>
    [Test]
    public void LaysTheWordsOutAgainstTheSurfaceWhateverThePictureIs()
    {
        CanvasDevice device;
        try
        {
            device = EnsureDetachedDevice();
        }
        catch (Exception exception)
        {
            Assert.Ignore($"This machine has no usable graphics device: {exception.Message}");
            return;
        }

        const int width = 480;
        const int height = 240;
        byte[] whole = RenderInk(device, width, height, SubtitlePresentation.Identity);
        byte[] topHalf = RenderInk(device, width, height, SubtitlePresentation.Identity with
        {
            Frame = new SubtitleFrame(0.0, 0.0, 1.0, 0.5),
        });

        (int left, int top, int right, int bottom) = InkBounds(whole, width, height);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(right, Is.GreaterThanOrEqualTo(left), "the cue is drawn for this to say anything");
            Assert.That(bottom, Is.GreaterThan(height / 2), "the words sit at the surface's bottom, not the picture's");
            Assert.That(topHalf, Is.EqualTo(whole), "and the picture's place changes nothing for this drawing");
        }
    }

    /// <summary>The box the ink of a surface-sized frame covers.</summary>
    private static (int Left, int Top, int Right, int Bottom) InkBounds(byte[] pixels, int width, int height)
    {
        int left = int.MaxValue;
        int top = int.MaxValue;
        int right = -1;
        int bottom = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (AlphaAt(pixels, width, x, y) == 0)
                {
                    continue;
                }

                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        return right >= left && bottom >= top ? (left, top, right, bottom) : (0, 0, -1, -1);
    }

    /// <summary>
    /// A bitmap cue is drawn where its fractions say, scaled to the surface: the picture of a DVD subtitle arrives as an
    /// image positioned inside the frame it was authored against, and it has to land there rather than at the corner.
    /// </summary>
    [Test]
    public void DrawsABitmapCueWhereItsFractionsSay()
    {
        CanvasRenderTarget? target = null;
        try
        {
            CanvasDevice device;
            try
            {
                device = EnsureDetachedDevice();
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
                Win2DSubtitleLayer.DrawCues(session, device, [cue], SubtitlePresentation.Identity, width, height);
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

    /// <summary>
    /// Two events can be up at the same time — the Matrix's title arrives as one SRT event per character — and they share an
    /// alignment band, so each has to make room for the one before it rather than being drawn on the same spot.
    /// </summary>
    [Test]
    public void StacksSimultaneousCuesInsteadOfDrawingThemOnOneAnother()
    {
        CanvasRenderTarget? target = null;
        try
        {
            CanvasDevice device;
            try
            {
                device = EnsureDetachedDevice();
            }
            catch (Exception exception)
            {
                Assert.Ignore($"This machine has no usable graphics device: {exception.Message}");
                return;
            }

            const int width = 480;
            const int height = 200;
            target = new CanvasRenderTarget(device, width, height, 96.0f);

            using SubtitleCue first = Cue("0,Default,,0,0,0,,帝");
            using SubtitleCue second = Cue("0,Default,,0,0,0,,帝国黑客帝国黑客");

            using (CanvasDrawingSession session = target.CreateDrawingSession())
            {
                session.Clear(default(Color));
                Win2DSubtitleLayer.DrawCues(session, device, [first, second], SubtitlePresentation.Identity, width, height);
            }

            byte[] pixels = target.GetPixelBytes();
            List<(int Top, int Bottom, int Left, int Right)> bands = InkBands(pixels, width, height);

            Assert.That(bands, Has.Count.EqualTo(2), "two cues, two separate bands of ink");

            // The bands are listed from the top of the surface down, so the last one is the cue drawn first: it keeps the
            // bottom, where its alignment asked for it, and the cue after it makes room above.
            (int Top, int Bottom, int Left, int Right) lowest = bands[^1];
            (int Top, int Bottom, int Left, int Right) highest = bands[0];
            Assert.That(highest.Bottom, Is.LessThan(lowest.Top), "the second cue sits above the first, with clear space between them");
            Assert.That(lowest.Right - lowest.Left, Is.LessThan(highest.Right - highest.Left), "the wide line is the one that went up");
        }
        finally
        {
            target?.Dispose();
        }
    }

    /// <summary>
    /// Cues that the chrome moves go on stacking from where they ended up, not from where they would have gone: the gap the
    /// second cue makes is measured against the first one's final place, so a bar that lifts both does not fold them onto one
    /// another.
    /// </summary>
    [Test]
    public void StacksCuesThatTheSafeRegionMovesTogether()
    {
        CanvasRenderTarget? target = null;
        try
        {
            CanvasDevice device;
            try
            {
                device = EnsureDetachedDevice();
            }
            catch (Exception exception)
            {
                Assert.Ignore($"This machine has no usable graphics device: {exception.Message}");
                return;
            }

            const int width = 480;
            const int height = 200;
            target = new CanvasRenderTarget(device, width, height, 96.0f);

            using SubtitleCue first = Cue("0,Default,,0,0,0,,帝");
            using SubtitleCue second = Cue("0,Default,,0,0,0,,帝国黑客帝国黑客");

            // A bar along the bottom of the surface, so both cues have to leave the band they were laid out in.
            SubtitlePresentation presentation = SubtitlePresentation.Identity with
            {
                Insets = new SubtitleInsets(0.0, 0.0, 0.0, 100.0),
            };

            using (CanvasDrawingSession session = target.CreateDrawingSession())
            {
                session.Clear(default(Color));
                Win2DSubtitleLayer.DrawCues(session, device, [first, second], presentation, width, height);
            }

            List<(int Top, int Bottom, int Left, int Right)> bands = InkBands(target.GetPixelBytes(), width, height);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(bands, Has.Count.EqualTo(2), "two cues, two separate bands of ink");
                Assert.That(bands[^1].Bottom, Is.LessThanOrEqualTo(height - 100), "the cue that kept the bottom is clear of the bar");
                Assert.That(bands[0].Bottom, Is.LessThan(bands[^1].Top), "and the one above it still sits above it");
            }
        }
        finally
        {
            target?.Dispose();
        }
    }

    /// <summary>
    /// The rows of the surface that hold ink, as one entry per run of them: what tells "stacked" from "drawn on top of each
    /// other" without knowing anything about fonts.
    /// </summary>
    private static List<(int Top, int Bottom, int Left, int Right)> InkBands(byte[] pixels, int width, int height)
    {
        List<(int Top, int Bottom, int Left, int Right)> bands = [];
        bool inBand = false;
        int top = 0;
        for (int y = 0; y < height; y++)
        {
            bool rowHasInk = false;
            for (int x = 0; x < width; x++)
            {
                if (AlphaAt(pixels, width, x, y) == 0)
                {
                    continue;
                }

                rowHasInk = true;
                break;
            }

            if (rowHasInk && !inBand)
            {
                inBand = true;
                top = y;
            }
            else if (!rowHasInk && inBand)
            {
                inBand = false;
                bands.Add((top, y - 1, LeftOf(pixels, width, top, y - 1), RightOf(pixels, width, top, y - 1)));
            }
        }

        if (inBand)
        {
            bands.Add((top, height - 1, LeftOf(pixels, width, top, height - 1), RightOf(pixels, width, top, height - 1)));
        }

        // Top of the surface first, so "above" is the earlier entry.
        return bands;
    }

    private static int LeftOf(byte[] pixels, int width, int top, int bottom)
    {
        for (int x = 0; x < width; x++)
        {
            for (int y = top; y <= bottom; y++)
            {
                if (AlphaAt(pixels, width, x, y) != 0)
                {
                    return x;
                }
            }
        }

        return width;
    }

    private static int RightOf(byte[] pixels, int width, int top, int bottom)
    {
        for (int x = width - 1; x >= 0; x--)
        {
            for (int y = top; y <= bottom; y++)
            {
                if (AlphaAt(pixels, width, x, y) != 0)
                {
                    return x;
                }
            }
        }

        return -1;
    }

    private static SubtitleCue Cue(string text)
    {
        return new SubtitleCue
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(2),
            Text = text,
        };
    }

    /// <summary>
    /// A new size is answered with a new chain, not a resized one. Something Win2D has drawn into cannot be resized: the
    /// wrapper around its back buffer is a WinRT object whose reference DXGI counts, and it lives until its finalizer runs,
    /// so <c>ResizeBuffers</c> fails with <c>DXGI_ERROR_INVALID_CALL</c> — and keeps failing — which is how a window that
    /// changed size came to lose its subtitles with a warning instead.
    /// </summary>
    [Test]
    public void ReplacesItsChainForANewSizeInsteadOfResizingIt()
    {
        RecordingTarget target = new(640, 360, 1.0);
        using Win2DSubtitleRenderer renderer = new();

        OperationResult configured = renderer.Configure(Stream(), [], target);
        if (!configured.IsSuccessful)
        {
            Assert.Ignore($"This machine has no usable graphics device: {configured.Message}");
            return;
        }

        using SubtitleCue cue = new()
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(2),
            Text = "0,Default,,0,0,0,,Growing",
        };

        OperationResult first = renderer.Show([cue], TimeSpan.Zero, SubtitlePresentation.Identity);
        Assert.That(first.IsSuccessful, Is.True, first.Message);
        nint firstChain = target.Attached;
        Assert.That(firstChain, Is.Not.Zero, "the host was handed a chain to compose");

        target.SetSize(800, 450);
        target.RaiseSizeChanged();

        OperationResult grown = renderer.Show([cue], TimeSpan.Zero, SubtitlePresentation.Identity);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(grown.IsSuccessful, Is.True, grown.Message);
            Assert.That(target.Attached, Is.Not.EqualTo(firstChain), "a size change is a new chain, not a resize of the old one");
        }

        target.SetSize(640, 360);
        target.RaiseSizeChanged();

        OperationResult shrunk = renderer.Show([cue], TimeSpan.Zero, SubtitlePresentation.Identity);
        Assert.That(shrunk.IsSuccessful, Is.True, shrunk.Message);
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

    /// <summary>
    /// Drawing offscreen costs nothing: two checks that draw with the fixture's device leave the number of live Direct3D
    /// devices alone, because the drawing takes the device it is handed rather than making one.
    /// </summary>
    [Test]
    public void DrawsWithTheDeviceItIsHandedAndNoOther()
    {
        CanvasDevice device = EnsureDetachedDevice();
        int baseline = D3D11Device.AliveDeviceCount;

        int rows = InkRows(device, 480, 240, SubtitlePresentation.Identity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows, Is.GreaterThan(0), "the drawing happened");
            Assert.That(D3D11Device.AliveDeviceCount, Is.EqualTo(baseline), "and it bought no device of its own");
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

    /// <summary>
    /// The device the offscreen checks draw with, which is the fixture's own: a composed swap chain cannot be read back, so
    /// these tests draw into a render target of their own and pass it the device to draw through.
    /// </summary>
    private static CanvasDevice EnsureDetachedDevice()
    {
        if (_detachedDevice is not null)
        {
            return _detachedDevice;
        }

        try
        {
            OperationResult<D3D11Device> created = D3D11Device.Create();
            Assert.That(created.IsSuccessful, Is.True, created.Message);
            _detachedDeviceOwner = created.Result;
            _detachedDevice = CanvasDevice.CreateFromDirect3D11Device(SubtitleCompositor.CreateWin2DDevice(created.Result));
            return _detachedDevice;
        }
        catch (Exception exception)
        {
            Assert.Ignore($"This machine has no usable graphics device: {exception.Message}");
            throw;
        }
    }

    [OneTimeTearDown]
    public void ReleaseDetachedDevice()
    {
        _detachedDevice?.Dispose();
        _detachedDevice = null;
        _detachedDeviceOwner?.Dispose();
        _detachedDeviceOwner = null;
    }

    private static CanvasDevice EnsureCanvasDevice(Win2DSubtitleLayer drawing)
    {
        try
        {
            return drawing.CanvasDevice;
        }
        catch (InvalidOperationException exception)
        {
            Assert.Ignore($"This machine has no usable graphics device: {exception.Message}");
            throw;
        }
    }

    private static SubtitleStreamInfo Stream()
    {
        return new SubtitleStreamInfo { Index = 2, CodecId = "subrip" };
    }

    /// <summary>
    /// The safe region is not the box the words are laid out in, it is what they must stay inside: a cue the chrome does not
    /// reach is left exactly where the picture puts it. Laying it out against the safe region instead would move every cue the
    /// moment any chrome appeared — the middle band would follow the bar up by half its height, and a bottom band the bar
    /// already clears would still be pushed by the whole of it.
    /// </summary>
    [Test]
    public void LeavesTheWordsWhereTheyAreWhenTheSafeRegionAlreadyHoldsThem()
    {
        CanvasDevice device;
        try
        {
            device = EnsureDetachedDevice();
        }
        catch (Exception exception)
        {
            Assert.Ignore($"This machine has no usable graphics device: {exception.Message}");
            return;
        }

        const int width = 480;
        const int height = 300;

        // Bottom-anchored words, with a strip shorter than the margin they already keep: nothing about them is covered.
        byte[] bare = RenderInk(device, width, height, SubtitlePresentation.Identity);
        byte[] shortStrip = RenderInk(device, width, height, SubtitlePresentation.Identity with
        {
            Insets = new SubtitleInsets(0.0, 0.0, 0.0, 2.0),
        });

        // Middle-anchored words and a bar along the bottom of the surface: the bar is nowhere near the middle.
        byte[] middle = RenderInk(device, width, height, SubtitlePresentation.Identity, "0,Default,,0,0,0,,{\\an5}Visible words");
        byte[] middleWithBar = RenderInk(device, width, height, SubtitlePresentation.Identity with
        {
            Insets = new SubtitleInsets(0.0, 0.0, 0.0, 100.0),
        }, "0,Default,,0,0,0,,{\\an5}Visible words");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DrawnPixels(bare), Is.GreaterThan(0), "the cue is drawn for this to say anything");
            Assert.That(shortStrip, Is.EqualTo(bare), "a strip the words already clear must not move them at all");
            Assert.That(DrawnPixels(middle), Is.GreaterThan(0), "the middle cue is drawn too");
            Assert.That(middleWithBar, Is.EqualTo(middle), "and a bar below it must not move it either");
        }
    }

    /// <summary>
    /// What the safe region costs is the least that puts the words inside it, not a fixed offset of its own size: a bottom
    /// band under a bar moves up by the bar, less the margin it already kept from the edge, so it ends flush with the bar
    /// rather than a margin above it — and a bar twice as tall moves it by twice as much.
    /// </summary>
    [Test]
    public void MovesTheWordsOnlyAsFarAsTheSafeRegionNeeds()
    {
        CanvasDevice device;
        try
        {
            device = EnsureDetachedDevice();
        }
        catch (Exception exception)
        {
            Assert.Ignore($"This machine has no usable graphics device: {exception.Message}");
            return;
        }

        const int width = 480;
        const int height = 300;
        // The margin a cue keeps from the edge of the surface, which is already clear of the chrome.
        double margin = height * 0.03;

        byte[] bare = RenderInk(device, width, height, SubtitlePresentation.Identity);
        byte[] shortBar = RenderInk(device, width, height, SubtitlePresentation.Identity with
        {
            Insets = new SubtitleInsets(0.0, 0.0, 0.0, 60.0),
        });
        byte[] tallBar = RenderInk(device, width, height, SubtitlePresentation.Identity with
        {
            Insets = new SubtitleInsets(0.0, 0.0, 0.0, 120.0),
        });

        (int _, int bareTop, int _, int bareBottom) = InkBounds(bare, width, height);
        (int _, int shortTop, int _, int shortBottom) = InkBounds(shortBar, width, height);
        (int _, int tallTop, int _, int tallBottom) = InkBounds(tallBar, width, height);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(shortBottom, Is.LessThanOrEqualTo(height - 60), "the words are clear of the bar");
            Assert.That(tallBottom, Is.LessThanOrEqualTo(height - 120), "and of the taller one");
            Assert.That(bareBottom - shortBottom, Is.EqualTo(60 - margin).Within(2.0),
                "the words moved by the bar less the margin they already kept");
            Assert.That(shortBottom - tallBottom, Is.EqualTo(60.0).Within(2.0),
                "so a bar twice as tall costs exactly its extra height");
            Assert.That(bareBottom - bareTop, Is.EqualTo(shortBottom - shortTop), "and they are the same size they were");
        }
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
