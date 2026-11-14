// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Backends.Libass;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Drives the real libass through the renderer: the script is parsed, a moment is drawn, and the bitmaps it produces reach
/// a surface. Every step here has failed at least once during development, and each one fails silently in its own way — a
/// missing font provider draws nothing, a mis-built script draws nothing, and a missing dependency fails to load at all.
/// </summary>
[TestFixture]
public class AssSubtitleRendererTests
{
    [Test]
    public void OffersItselfForAssScriptsOnly()
    {
        AssSubtitleRendererFactory factory = new();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(factory.Create(Stream("ass"), SubtitleCueFormat.Ass).IsSuccessful, Is.True, "an ASS track is this renderer's work");
            Assert.That(factory.Create(Stream("ssa"), SubtitleCueFormat.Ass).IsSuccessful, Is.True, "and so is SSA");
            Assert.That(factory.Create(Stream("subrip"), SubtitleCueFormat.Ass).IsSuccessful, Is.False, "a SubRip track reaches a renderer as ASS cues too, so the format alone would have taken it");
            Assert.That(factory.Create(Stream("ass"), SubtitleCueFormat.BitmapBgra).IsSuccessful, Is.False, "a picture is not a script");
        }
    }

    [Test]
    public void ParsesTheScriptAndDrawsAMoment()
    {
        using AssSubtitleRenderer renderer = new();
        FakeTarget target = new(640, 360);

        OperationResult configured = renderer.Configure(Stream("ass"), [], target);
        if (!configured.IsSuccessful)
        {
            Assert.Ignore($"This machine cannot load or drive libass: {configured.Message}");
        }

        Assert.That(target.Attached, Is.Not.Zero, "the host has to be handed a native swap chain to compose");

        SubtitleCue cue = new()
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(5),
            Text = "0,Default,,0,0,0,,Visible words",
        };

        OperationResult drawn;
        try
        {
            drawn = renderer.Show(new[] { cue }, TimeSpan.FromSeconds(1), SubtitlePresentation.Identity);
        }
        finally
        {
            cue.Dispose();
        }

        Assert.That(drawn.IsSuccessful, Is.True, drawn.Message);
    }

    [Test]
    public void DrawsAnAnimationWithoutRebuildingTheTrack()
    {
        using AssSubtitleRenderer renderer = new();
        FakeTarget target = new(640, 360);

        OperationResult configured = renderer.Configure(Stream("ass"), [], target);
        if (!configured.IsSuccessful)
        {
            Assert.Ignore($"This machine cannot load or drive libass: {configured.Message}");
        }

        // A cue whose picture changes inside its own span is drawn again at a later moment from the same cues: the track is
        // the same script, so it must not be parsed again, and the drawing must still succeed.
        SubtitleCue cue = new()
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(5),
            Text = @"0,Default,,0,0,0,,{\move(0,0,200,0)}Moving",
            Changes = AssSubtitleChangesFor(),
        };

        try
        {
            for (int index = 0; index < 4; index++)
            {
                OperationResult drawn = renderer.Show(new[] { cue }, TimeSpan.FromMilliseconds(index * 250), SubtitlePresentation.Identity);
                Assert.That(drawn.IsSuccessful, Is.True, drawn.Message);
            }
        }
        finally
        {
            cue.Dispose();
        }
    }

    /// <summary>
    /// The host's chrome does not resize the words. libass's margins say "the window is bigger than the video" and scale
    /// the script to what is left over — a 200px bottom margin at 1080p drew an 81px glyph 65px high — so an ASS script is
    /// laid out for the whole surface however much of it a bar covers.
    /// </summary>
    [Test]
    public void DrawsTheWordsTheSameSizeWhateverTheChromeCovers()
    {
        using AssSubtitleRenderer renderer = new();
        FakeTarget target = new(640, 360);

        OperationResult configured = renderer.Configure(Stream("ass"), [], target);
        if (!configured.IsSuccessful)
        {
            Assert.Ignore($"This machine cannot load or drive libass: {configured.Message}");
        }

        SubtitleCue cue = new()
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(5),
            Text = "0,Default,,0,0,0,,Visible words",
        };

        try
        {
            OperationResult bare = renderer.Show(new[] { cue }, TimeSpan.FromSeconds(1), SubtitlePresentation.Identity);
            Assert.That(bare.IsSuccessful, Is.True, bare.Message);
            int drawn = InkHeight(renderer.CompositedFrame, target.SurfaceWidth, target.SurfaceHeight);

            // What the player hands over while its overlay is up: the strips the chrome covers.
            SubtitlePresentation covered = new() { Insets = new SubtitleInsets(0.0, 40.0, 0.0, 90.0) };
            OperationResult withChrome = renderer.Show(new[] { cue }, TimeSpan.FromSeconds(1), covered);
            Assert.That(withChrome.IsSuccessful, Is.True, withChrome.Message);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(drawn, Is.GreaterThan(0), "the cue has to be drawn for this to say anything");
                Assert.That(InkHeight(renderer.CompositedFrame, target.SurfaceWidth, target.SurfaceHeight), Is.EqualTo(drawn), "the chrome covers the surface, it does not shrink the script");
            }
        }
        finally
        {
            cue.Dispose();
        }
    }

    /// <summary>
    /// A file can carry the font its script names, and libass is given it before it builds the font provider that looks
    /// fonts up: the renderer is told what the source holds, and the drawing must be unaffected in the process. No font
    /// installed on the machine is needed — the bytes come from the system only so they are a real font — and a second
    /// configuration with a different set is a different renderer's worth of fonts.
    /// </summary>
    [Test]
    public void TakesTheFontsTheSourceCarries()
    {
        string? fontPath = FindSystemFont();
        if (fontPath is null)
        {
            Assert.Ignore("This machine has no font to read an attachment from.");
            return;
        }

        MediaAttachment attachment = new()
        {
            Name = Path.GetFileName(fontPath),
            MimeType = "application/x-truetype-font",
            Data = File.ReadAllBytes(fontPath),
        };

        using AssSubtitleRenderer renderer = new();
        FakeTarget target = new(640, 360);

        OperationResult configured = renderer.Configure(Stream("ass"), [attachment], target);
        if (!configured.IsSuccessful)
        {
            Assert.Ignore($"This machine cannot load or drive libass: {configured.Message}");
            return;
        }

        using SubtitleCue cue = new()
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(5),
            Text = "0,Default,,0,0,0,,Visible words",
        };

        OperationResult drawn = renderer.Show(new[] { cue }, TimeSpan.FromSeconds(1), SubtitlePresentation.Identity);
        Assert.That(drawn.IsSuccessful, Is.True, drawn.Message);
        int withAttachment = InkHeight(renderer.CompositedFrame, target.SurfaceWidth, target.SurfaceHeight);
        Assert.That(withAttachment, Is.GreaterThan(0), "the cue is drawn with the attached font available");

        // The same renderer, configured with a set of its own: libass keeps the fonts it was built with, so this rebuilds.
        OperationResult reconfigured = renderer.Configure(Stream("ass"), [], target);
        Assert.That(reconfigured.IsSuccessful, Is.True, reconfigured.Message);

        OperationResult drawnAgain = renderer.Show(new[] { cue }, TimeSpan.FromSeconds(1), SubtitlePresentation.Identity);
        Assert.That(drawnAgain.IsSuccessful, Is.True, drawnAgain.Message);
        Assert.That(InkHeight(renderer.CompositedFrame, target.SurfaceWidth, target.SurfaceHeight), Is.GreaterThan(0), "and drawn again without them");
    }

    /// <summary>A font every Windows installation has, as the bytes of a real face rather than a made-up payload.</summary>
    private static string? FindSystemFont()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        foreach (string name in (string[])["arial.ttf", "segoeui.ttf", "tahoma.ttf"])
        {
            string path = Path.Combine(folder, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static int InkHeight(ReadOnlySpan<byte> frame, int width, int height)
    {
        int rows = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = frame.Slice(y * width * 4, width * 4);
            for (int x = 3; x < row.Length; x += 4)
            {
                if (row[x] != 0)
                {
                    rows++;
                    break;
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// A script is laid out against the picture it sits on, so when the picture is a box inside the surface the words are
    /// inside that box too: before this, libass was given the surface and put its lines in the bar under the picture.
    /// </summary>
    [Test]
    public void DrawsInsideThePictureItIsGiven()
    {
        using AssSubtitleRenderer renderer = new();
        FakeTarget target = new(640, 360);

        OperationResult configured = renderer.Configure(Stream("ass"), [], target);
        if (!configured.IsSuccessful)
        {
            Assert.Ignore($"This machine cannot load or drive libass: {configured.Message}");
            return;
        }

        using SubtitleCue cue = new()
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(5),
            Text = "0,Default,,0,0,0,,Visible words",
        };

        SubtitleFrame picture = new(0.25, 0.25, 0.5, 0.5);
        SubtitlePresentation presentation = SubtitlePresentation.Identity with { Frame = picture };

        OperationResult drawn = renderer.Show(new[] { cue }, TimeSpan.FromSeconds(1), presentation);
        Assert.That(drawn.IsSuccessful, Is.True, drawn.Message);

        (int top, int bottom) = InkRows(renderer.CompositedFrame, target.SurfaceWidth, target.SurfaceHeight);
        int frameTop = (int)Math.Floor(picture.Top * target.SurfaceHeight);
        int frameBottom = (int)Math.Ceiling((picture.Top + picture.Height) * target.SurfaceHeight);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bottom, Is.GreaterThan(top), "the cue is drawn for this to say anything");
            Assert.That(top, Is.GreaterThanOrEqualTo(frameTop), "no ink above the picture");
            Assert.That(bottom, Is.LessThanOrEqualTo(frameBottom), "and none below it");
        }
    }

    /// <summary>
    /// A box wider than the surface reaches libass at its own size and is cropped by the surface, as the picture it belongs
    /// to is: a picture filling the surface can be wider than it. Narrowing the box to the surface instead — which is what
    /// a box clamped to the surface means — would give it another shape, and libass scales a script onto the shape it is
    /// given, so the words would come out smaller as well as stretched.
    /// </summary>
    [Test]
    public void UsesABoxWiderThanTheSurfaceAtItsOwnSize()
    {
        using AssSubtitleRenderer renderer = new();
        FakeTarget target = new(640, 360);

        OperationResult configured = renderer.Configure(Stream("ass"), [], target);
        if (!configured.IsSuccessful)
        {
            Assert.Ignore($"This machine cannot load or drive libass: {configured.Message}");
            return;
        }

        using SubtitleCue cue = new()
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(5),
            Text = "0,Default,,0,0,0,,Visible words",
        };

        OperationResult inside = renderer.Show(new[] { cue }, TimeSpan.FromSeconds(1), SubtitlePresentation.Identity with { Frame = new SubtitleFrame(0.0, 0.0, 1.0, 1.0) });
        Assert.That(inside.IsSuccessful, Is.True, inside.Message);
        (int insideTop, int insideBottom) = InkRows(renderer.CompositedFrame, target.SurfaceWidth, target.SurfaceHeight);
        (int insideLeft, int insideRight) = InkColumns(renderer.CompositedFrame, target.SurfaceWidth, target.SurfaceHeight);

        // 840 × 360 is as tall as the surface and wider than it, so the words are the size the box's height gives them and
        // the sides are cut off.
        OperationResult wider = renderer.Show(new[] { cue }, TimeSpan.FromSeconds(1), SubtitlePresentation.Identity with { Frame = new SubtitleFrame(-0.15625, 0.0, 1.3125, 1.0) });
        Assert.That(wider.IsSuccessful, Is.True, wider.Message);
        (int widerTop, int widerBottom) = InkRows(renderer.CompositedFrame, target.SurfaceWidth, target.SurfaceHeight);
        (int widerLeft, int widerRight) = InkColumns(renderer.CompositedFrame, target.SurfaceWidth, target.SurfaceHeight);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(insideBottom, Is.GreaterThan(insideTop), "the cue is drawn for this to say anything");
            Assert.That(widerTop, Is.EqualTo(insideTop).Within(2), "the box is as tall as before, so the words are as tall");
            Assert.That(widerBottom, Is.EqualTo(insideBottom).Within(2));

            // Both boxes are centred on the surface, so the words land on the same spot. A box cut down to the surface's
            // width would draw them from the corner the box starts at instead, off centre by the width that was cut.
            Assert.That((widerLeft + widerRight) / 2.0, Is.EqualTo((insideLeft + insideRight) / 2.0).Within(2.0));
        }
    }

    /// <summary>The first and the last column of the frame that carry ink.</summary>
    private static (int Left, int Right) InkColumns(ReadOnlySpan<byte> frame, int width, int height)
    {
        int left = int.MaxValue;
        int right = -1;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = frame.Slice(y * width * 4, width * 4);
            for (int x = 0; x < width; x++)
            {
                if (row[(x * 4) + 3] == 0)
                {
                    continue;
                }

                left = Math.Min(left, x);
                right = Math.Max(right, x);
            }
        }

        return (left, right);
    }

    /// <summary>The first and the last row of the frame that carry ink.</summary>
    private static (int Top, int Bottom) InkRows(ReadOnlySpan<byte> frame, int width, int height)
    {
        int top = int.MaxValue;
        int bottom = -1;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = frame.Slice(y * width * 4, width * 4);
            for (int x = 3; x < row.Length; x += 4)
            {
                if (row[x] == 0)
                {
                    continue;
                }

                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
                break;
            }
        }

        return (top, bottom);
    }

    /// <summary>
    /// What covers the surface — the player's own chrome — must not cover the words, and libass's margins cannot say so
    /// without shrinking the script. The picture is moved instead: the words end up clear of the strip, at exactly the size
    /// they were laid out at.
    /// </summary>
    [Test]
    public void MovesTheWordsClearOfWhatCoversTheSurface()
    {
        using AssSubtitleRenderer renderer = new();
        FakeTarget target = new(640, 360);

        OperationResult configured = renderer.Configure(Stream("ass"), [], target);
        if (!configured.IsSuccessful)
        {
            Assert.Ignore($"This machine cannot load or drive libass: {configured.Message}");
            return;
        }

        using SubtitleCue cue = new()
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(5),
            Text = "0,Default,,0,0,0,,Visible words",
        };

        OperationResult bare = renderer.Show(new[] { cue }, TimeSpan.FromSeconds(1), SubtitlePresentation.Identity);
        Assert.That(bare.IsSuccessful, Is.True, bare.Message);
        (int bareTop, int bareBottom) = InkRows(renderer.CompositedFrame, target.SurfaceWidth, target.SurfaceHeight);

        const int covered = 100;
        SubtitlePresentation withChrome = SubtitlePresentation.Identity with { Insets = new SubtitleInsets(0.0, 0.0, 0.0, covered) };
        OperationResult shifted = renderer.Show(new[] { cue }, TimeSpan.FromSeconds(1), withChrome);
        Assert.That(shifted.IsSuccessful, Is.True, shifted.Message);
        (int shiftedTop, int shiftedBottom) = InkRows(renderer.CompositedFrame, target.SurfaceWidth, target.SurfaceHeight);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bareBottom, Is.GreaterThan(bareTop), "the cue is drawn for this to say anything");
            Assert.That(shiftedBottom, Is.LessThanOrEqualTo(target.SurfaceHeight - covered), "the words are clear of the strip");
            Assert.That(shiftedTop, Is.LessThanOrEqualTo(bareTop), "they moved up rather than down");
            Assert.That(shiftedBottom - shiftedTop, Is.EqualTo(bareBottom - bareTop), "and they are the same size they were");
        }
    }

    /// <summary>
    /// The strips are where the host's chrome is, which is against the surface's edges, not the picture's: a bar along the
    /// bottom of the surface has nothing to do with a picture that ends above it, and the words stay where the script put
    /// them. Measured from the picture instead, this bar would have dragged them up by its whole height.
    /// </summary>
    [Test]
    public void LeavesTheWordsAloneWhenTheStripIsOutsideThePicture()
    {
        using AssSubtitleRenderer renderer = new();
        FakeTarget target = new(640, 360);

        OperationResult configured = renderer.Configure(Stream("ass"), [], target);
        if (!configured.IsSuccessful)
        {
            Assert.Ignore($"This machine cannot load or drive libass: {configured.Message}");
            return;
        }

        using SubtitleCue cue = new()
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(5),
            Text = "0,Default,,0,0,0,,Visible words",
        };

        // A picture in the top half of the surface, and chrome along the bottom of the surface.
        SubtitlePresentation presentation = SubtitlePresentation.Identity with
        {
            Frame = new SubtitleFrame(0.0, 0.0, 1.0, 0.5),
            Insets = new SubtitleInsets(0.0, 0.0, 0.0, 100.0),
        };

        OperationResult drawn = renderer.Show(new[] { cue }, TimeSpan.FromSeconds(1), presentation);
        Assert.That(drawn.IsSuccessful, Is.True, drawn.Message);

        (int top, int bottom) = InkRows(renderer.CompositedFrame, target.SurfaceWidth, target.SurfaceHeight);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bottom, Is.GreaterThan(top), "the cue is drawn for this to say anything");
            Assert.That(bottom, Is.LessThanOrEqualTo(180), "the words stay in the picture");
            Assert.That(bottom, Is.GreaterThan(140), "and are not pulled up towards its top by a bar that is below it");
        }
    }

    private static SubtitleChange[] AssSubtitleChangesFor()
    {
        return [new SubtitleChange(TimeSpan.Zero, TimeSpan.FromSeconds(5), true)];
    }

    private static SubtitleStreamInfo Stream(string codecId)
    {
        return new SubtitleStreamInfo { Index = 3, CodecId = codecId };
    }

    /// <summary>A render target the engine can hand a swap chain to, without a window.</summary>
    private sealed class FakeTarget : IVideoRenderTarget
    {
        internal FakeTarget(int width, int height)
        {
            SurfaceWidth = width;
            SurfaceHeight = height;
        }

        public int SurfaceWidth { get; }

        public int SurfaceHeight { get; }

        public double RasterizationScale => 1.0;

        public nint Attached { get; private set; }

        public event Action? SurfaceSizeChanged
        {
            add { }
            remove { }
        }

        public void AttachPresentationSurface(nint swapChain)
        {
            Attached = swapChain;
        }
    }
}
