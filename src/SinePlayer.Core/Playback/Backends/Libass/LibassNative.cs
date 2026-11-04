// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

namespace SinePlayer.Core.Playback.Backends.Libass;

/// <summary>
/// The libass entry points this engine calls, and the two structures it reads back.
/// <para>
/// The library is a rasteriser and nothing else: it owns no clock, no surface and no queue. A caller hands it a script and
/// a moment, and gets a list of bitmaps for that moment — which is why the pipeline, not libass, decides when to redraw.
/// </para>
/// </summary>
internal static class LibassNative
{
    /// <summary>The file the imports name. Loading it by this name is what lets a preloaded copy answer them.</summary>
    internal const string Library = "ass-9.dll";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ass_library_init();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ass_library_done(IntPtr library);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ass_renderer_init(IntPtr library);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ass_renderer_done(IntPtr renderer);

    /// <param name="defaultFont">A font file to use when the script asks for something unavailable; null is fine.</param>
    /// <param name="defaultFamily">The family to fall back to, which is what a script naming a missing font resolves to.</param>
    /// <param name="provider">Asking the system through DirectWrite, which a Windows build without fontconfig offers.</param>
    /// <param name="update">Whether to rescan the fonts now, which is right because this is called once per renderer.</param>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    internal static extern void ass_set_fonts(IntPtr renderer, IntPtr defaultFont, string defaultFamily, int provider, IntPtr config, int update);

    /// <summary>The size of the picture scripts are laid out in, which is the surface being drawn into.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ass_set_frame_size(IntPtr renderer, int width, int height);

    /// <summary>Room at each edge that belongs to something else, so a script's own margins are measured from inside it.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ass_set_margins(IntPtr renderer, int top, int bottom, int left, int right);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ass_set_use_margins(IntPtr renderer, int use);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ass_read_memory(IntPtr library, byte[] buffer, UIntPtr bufferSize, IntPtr codepage);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ass_free_track(IntPtr track);

    /// <param name="now">The moment to draw, in milliseconds.</param>
    /// <param name="detectChange">
    /// Whether the picture differs from the one the previous call produced: 0 identical, 1 content only, 2 layout too.
    /// It is a verdict about the frame just rendered, not a schedule — nothing in libass can say when to call it again.
    /// </param>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ass_render_frame(IntPtr renderer, IntPtr track, long now, out int detectChange);

    /// <summary>A font the engine supplies itself — one attached to the media file rather than installed on the machine.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    internal static extern void ass_add_font(IntPtr library, string name, byte[] data, int dataSize);

    /// <summary>
    /// Whether the fonts a script carries in its own <c>[Fonts]</c> section are taken out of it and made available, which
    /// is the other way a file brings its own fonts.
    /// </summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ass_set_extract_fonts(IntPtr library, int extract);

    /// <summary>What <see cref="ass_set_fonts"/> asks for when the system is to be consulted.</summary>
    internal const int FontProviderDirectWrite = 4;

    /// <summary>One bitmap of a rendered frame: coverage for one colour, at its own place in the frame.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AssImage
    {
        internal int Width;
        internal int Height;
        internal int Stride;

        /// <summary>One byte per pixel, 255 where the frame is covered. The last row may not be padded to the stride.</summary>
        internal IntPtr Bitmap;

        /// <summary>
        /// The colour and the alpha, packed as 0xRRGGBBAA — verified against 0.17.5 rather than assumed: a script colour
        /// of 0x00123456 arrives as 0x56341200, and 0x80 alpha as 0x80. The alpha is <em>transparency</em>, as in the
        /// script itself: 0 is opaque, 255 is invisible.
        /// </summary>
        internal uint Colour;

        internal int Left;
        internal int Top;

        /// <summary>The next bitmap of the frame, which has to be composited over this one.</summary>
        internal IntPtr Next;

        internal int Type;
    }
}
