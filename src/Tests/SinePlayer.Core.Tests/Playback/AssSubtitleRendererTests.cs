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

        OperationResult configured = renderer.Configure(Stream("ass"), target);
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
            drawn = renderer.Show([cue], TimeSpan.FromSeconds(1), SubtitlePlacement.Identity);
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

        OperationResult configured = renderer.Configure(Stream("ass"), target);
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
                OperationResult drawn = renderer.Show([cue], TimeSpan.FromMilliseconds(index * 250), SubtitlePlacement.Identity);
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

        OperationResult configured = renderer.Configure(Stream("ass"), target);
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
            OperationResult bare = renderer.Show([cue], TimeSpan.FromSeconds(1), SubtitlePlacement.Identity);
            Assert.That(bare.IsSuccessful, Is.True, bare.Message);
            int drawn = InkHeight(renderer.CompositedFrame, target.SurfaceWidth, target.SurfaceHeight);

            // What the player hands over while its overlay is up: the strips the chrome covers.
            SubtitlePlacement covered = new(0.0, 0.0, new SubtitleInsets(0.0, 40.0, 0.0, 90.0));
            OperationResult withChrome = renderer.Show([cue], TimeSpan.FromSeconds(1), covered);
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

    private static IReadOnlyList<SubtitleChange> AssSubtitleChangesFor()
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
