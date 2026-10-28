// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Backends.FFmpeg;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The arithmetic that turns what libavcodec hands over for a bitmap subtitle into what a compositor accepts. It is pinned
/// on plain arrays because it is the one part of the feature that can be read exactly: a palette is <c>0xAARRGGBB</c>
/// words, the pixels are indices into it, and the output has to be premultiplied blue-green-red-alpha — byte for byte what
/// the surface will hold.
/// </summary>
[TestFixture]
public class SubtitleBitmapTests
{
    /// <summary>
    /// The palette and indices of a real DVD subtitle, taken from a decoded event: a transparent border, an off-white
    /// fill, a grey and black outline.
    /// </summary>
    [Test]
    public void ExpandsPaletteIndicesIntoPremultipliedBgra()
    {
        uint[] palette = [0x00000000, 0xFFF0F0F0, 0xFF999999, 0xFF000000];
        byte[] indices = [0, 1, 2, 3];
        byte[] destination = new byte[4 * 4];

        SubtitleBitmap.ExpandIndices(indices, 4, palette, 4, 1, destination, 16);

        using (Assert.EnterMultipleScope())
        {
            // Index 0: the palette says nothing at all, so no colour survives — not even black with no alpha.
            Assert.That(destination[0..4], Is.EqualTo(new byte[] { 0, 0, 0, 0 }), "transparent");

            // Index 1: off-white, opaque. Blue, green, red, alpha — the order the palette's 0x00RRGGBB gives in memory.
            Assert.That(destination[4..8], Is.EqualTo(new byte[] { 0xF0, 0xF0, 0xF0, 0xFF }), "off-white");

            // Index 2: grey.
            Assert.That(destination[8..12], Is.EqualTo(new byte[] { 0x99, 0x99, 0x99, 0xFF }), "grey");

            // Index 3: opaque black, which is a real colour and must not be treated as transparent.
            Assert.That(destination[12..16], Is.EqualTo(new byte[] { 0, 0, 0, 0xFF }), "black");
        }
    }

    /// <summary>
    /// A partly transparent colour has its channels multiplied by its alpha, because the surface it lands in is composed
    /// over the picture: a straight-alpha sample would arrive far too bright along an edge.
    /// </summary>
    [Test]
    public void MultipliesColourByAlpha()
    {
        uint[] palette = [0x80008000];
        byte[] indices = [0];
        byte[] destination = new byte[4];

        SubtitleBitmap.ExpandIndices(indices, 1, palette, 1, 1, destination, 4);

        using (Assert.EnterMultipleScope())
        {
            // 0x80008000 is alpha 0x80, red 0x00, green 0x80, blue 0x00 — green at half strength is 0x40.
            Assert.That(destination[0], Is.EqualTo(0x00), "blue");
            Assert.That(destination[1], Is.EqualTo(0x40), "green");
            Assert.That(destination[2], Is.EqualTo(0x00), "red");
            Assert.That(destination[3], Is.EqualTo(0x80), "alpha");
        }
    }

    /// <summary>
    /// An index the palette does not have is nothing, rather than whatever happens to be beside it: a broken cue should
    /// leave a hole, not smear the last colour across the row.
    /// </summary>
    [Test]
    public void LeavesAnIndexWithNoColourEmpty()
    {
        uint[] palette = [0xFFFFFFFF];
        byte[] indices = [0, 7];
        byte[] destination = new byte[8];

        SubtitleBitmap.ExpandIndices(indices, 2, palette, 1, 1, destination, 8);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(destination[0..4], Is.EqualTo(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }));
            Assert.That(destination[4..8], Is.EqualTo(new byte[] { 0, 0, 0, 0 }), "an index past the palette draws nothing");
        }
    }

    /// <summary>
    /// Rows are addressed by their own stride, on both sides: libavcodec pads the rows it hands over and the image this
    /// builds is as wide as its own content, so a conversion that assumed the two were equal would shear the picture.
    /// </summary>
    [Test]
    public void WalksRowsByStride()
    {
        uint[] palette = [0xFF000000, 0xFFFFFFFF];

        // Two rows of two pixels, with a gap after each row on the source side.
        byte[] indices = [0, 0, 0xAA, 0xAA, 1, 1, 0xAA, 0xAA];

        // The destination is also two rows of two pixels, and is exactly as wide as its content.
        byte[] destination = new byte[16];

        SubtitleBitmap.ExpandIndices(indices, 4, palette, 2, 2, destination, 8);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(destination[0..8], Is.EqualTo(new byte[] { 0, 0, 0, 0xFF, 0, 0, 0, 0xFF }), "the first row is black");
            Assert.That(destination[8..16], Is.EqualTo(Enumerable.Repeat((byte)0xFF, 8).ToArray()), "and the second row was read from past the padding");
        }
    }

    /// <summary>
    /// A picture with no palette — what a Blu-ray subtitle arrives as — keeps its own channel order and is premultiplied
    /// the same way.
    /// </summary>
    [Test]
    public void CopiesPixelsThatAreAlreadyBgra()
    {
        byte[] source = [0x10, 0x20, 0x30, 0x80, 0x40, 0x50, 0x60, 0xFF];
        byte[] destination = new byte[8];

        SubtitleBitmap.CopyBgra(source, 8, 2, 1, destination, 8);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(destination[0..4], Is.EqualTo(new byte[] { 0x08, 0x10, 0x18, 0x80 }), "half-transparent: each channel halved");
            Assert.That(destination[4..8], Is.EqualTo(new byte[] { 0x40, 0x50, 0x60, 0xFF }), "opaque: unchanged");
        }
    }
}
