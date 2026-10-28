// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

/// <summary>
/// Turns what libavcodec hands over for a bitmap subtitle into the one shape a renderer accepts: premultiplied BGRA.
/// <para>
/// Two layouts exist and they are told apart by the palette. A DVD, DVB or teletext subtitle is a byte per pixel of
/// palette <em>indices</em>, with the palette beside it as <c>0xAARRGGBB</c> words — which on a little-endian machine are
/// already the four bytes blue, green, red, alpha in that order. A Blu-ray (PGS) subtitle arrives as 32-bit pixels with
/// no palette at all, in the same byte order. So both become the same thing, and the only real work is the palette lookup
/// and the multiplication of colour by alpha that the compositor expects of a surface drawn over the picture.
/// </para>
/// <para>
/// Written over spans so a test can pin the arithmetic with plain arrays, and the decoder can point a span at libavcodec's
/// own memory without copying it first.
/// </para>
/// </summary>
internal static class SubtitleBitmap
{
    /// <summary>Expands palette indices into premultiplied BGRA.</summary>
    /// <param name="palette">The colours, as <c>0xAARRGGBB</c>: the same words libavcodec stores.</param>
    internal static void ExpandIndices(ReadOnlySpan<byte> indices, int indexStride, ReadOnlySpan<uint> palette, int width, int height, Span<byte> destination, int destinationStride)
    {
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = indices.Slice(y * indexStride, width);
            Span<byte> target = destination.Slice(y * destinationStride, width * 4);

            for (int x = 0; x < width; x++)
            {
                int index = row[x];
                if (index >= palette.Length)
                {
                    // An index the palette does not have is nothing at all, not whatever the neighbouring colour is.
                    target.Slice(x * 4, 4).Clear();
                    continue;
                }

                WritePremultiplied(palette[index], target.Slice(x * 4, 4));
            }
        }
    }

    /// <summary>Copies 32-bit pixels that are already in blue-green-red-alpha order, premultiplying them.</summary>
    internal static void CopyBgra(ReadOnlySpan<byte> source, int sourceStride, int width, int height, Span<byte> destination, int destinationStride)
    {
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = source.Slice(y * sourceStride, width * 4);
            Span<byte> target = destination.Slice(y * destinationStride, width * 4);

            for (int x = 0; x < width * 4; x += 4)
            {
                int alpha = row[x + 3];
                target[x] = Premultiply(row[x], alpha);
                target[x + 1] = Premultiply(row[x + 1], alpha);
                target[x + 2] = Premultiply(row[x + 2], alpha);
                target[x + 3] = (byte)alpha;
            }
        }
    }

    private static void WritePremultiplied(uint colour, Span<byte> target)
    {
        int alpha = (int)(colour >> 24);
        target[0] = Premultiply((byte)(colour & 0xFF), alpha);
        target[1] = Premultiply((byte)((colour >> 8) & 0xFF), alpha);
        target[2] = Premultiply((byte)((colour >> 16) & 0xFF), alpha);
        target[3] = (byte)alpha;
    }

    private static byte Premultiply(byte colour, int alpha)
    {
        // Rounded rather than truncated, so a fully opaque sample stays exactly the colour it was: 255 × 255 + 127
        // over 255 is 255 again, and nothing drifts brighter or darker along an edge.
        return (byte)(((colour * alpha) + 127) / 255);
    }
}
