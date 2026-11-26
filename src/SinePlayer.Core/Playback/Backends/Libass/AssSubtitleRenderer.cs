// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using Microsoft.Graphics.Canvas;

using SinePlayer.Core.Common.AppEnvironment;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Subtitle;
using SinePlayer.Core.Playback.Utils;

using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace SinePlayer.Core.Playback.Backends.Libass;

internal sealed partial class AssSubtitleRenderer : ISubtitleRenderer
{
    #region Constructor

    private const string TAG = nameof(AssSubtitleRenderer);

    private const string DefaultFontFamily = "Arial";

    private bool _disposed;
    private readonly SubtitleSurface _surface = new();

    private IntPtr _library;
    private IntPtr _renderer;
    private IntPtr _track;

    private List<MediaAttachment> _attachments = [];
    private bool _fontsConfigured;

    private string? _builtScript;
    private string? _header;
    private byte[] _pixels = [];

    #endregion

    #region Public API

    public string Name => "libass";

    internal ReadOnlySpan<byte> CompositedFrame => _pixels;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseRenderer();
        _surface.Dispose();
    }

    public OperationResult Configure(SubtitleStreamInfo stream, IReadOnlyList<MediaAttachment> attachments, IVideoRenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(target);

        if (_disposed)
        {
            return OperationResult.Failure("The subtitle renderer has already been disposed.");
        }

        if (_header is not null && !string.Equals(_header, stream.ScriptHeader, StringComparison.Ordinal))
        {
            // A different stream's styles: the track built from the old header describes cues that are not this track's.
            ReleaseTrack();
        }

        // libass takes the fonts it is given when its font provider is built, once, as the renderer is created: a set that
        // does not match the one this renderer holds means a renderer built for a different file.
        if (!SameAttachments(attachments))
        {
            ReleaseRenderer();
        }

        _attachments = [.. attachments];
        _header = stream.ScriptHeader;
        _surface.BindTarget(target);

        OperationResult sized = _surface.EnsureSurface();
        if (!sized.IsSuccessful)
        {
            return sized;
        }

        return EnsureRenderer();
    }

    public OperationResult Show(IReadOnlyList<SubtitleCue> cues, TimeSpan position, SubtitlePresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(cues);

        if (_disposed)
        {
            return OperationResult.Failure("The subtitle renderer has already been disposed.");
        }

        OperationResult sized = _surface.EnsureSurface();
        if (!sized.IsSuccessful)
        {
            return sized;
        }

        OperationResult prepared = EnsureRenderer();
        if (!prepared.IsSuccessful)
        {
            return prepared;
        }

        int width = _surface.Width;
        int height = _surface.Height;

        OperationResult tracked = EnsureTrack(cues);
        if (!tracked.IsSuccessful)
        {
            return tracked;
        }

        SubtitleFrame frame = presentation.Frame.IsEmpty ? SubtitleFrame.Full : presentation.Frame;
        int pictureX = (int)Math.Round(frame.Left * width);
        int pictureY = (int)Math.Round(frame.Top * height);
        int pictureWidth = Math.Max(1, (int)Math.Round(frame.Width * width));
        int pictureHeight = Math.Max(1, (int)Math.Round(frame.Height * height));
        double pictureCenterX = pictureX + (pictureWidth / 2.0);
        double pictureCenterY = pictureY + (pictureHeight / 2.0);

        // libass draws the script at the size of the frame it is given, so it is given the picture scaled by the zoom: that
        // is how the words come out at the zoomed size. The picture itself is what the words belong to, and stays what the
        // safe region is measured from.
        double zoom = presentation.EffectiveZoom;
        int renderX = pictureX;
        int renderY = pictureY;
        int renderWidth = pictureWidth;
        int renderHeight = pictureHeight;
        if (zoom != 1.0)
        {
            renderWidth = Math.Max(1, (int)Math.Round(pictureWidth * zoom));
            renderHeight = Math.Max(1, (int)Math.Round(pictureHeight * zoom));
            renderX = (int)Math.Round(pictureCenterX - (renderWidth / 2.0));
            renderY = (int)Math.Round(pictureCenterY - (renderHeight / 2.0));
        }

        LibassNative.ass_set_frame_size(_renderer, renderWidth, renderHeight);

        long now = (long)position.TotalMilliseconds;
        IntPtr images = LibassNative.ass_render_frame(_renderer, _track, now, out _);

        // The presentation's offsets are pixels of the surface's own coordinate space, so they are scaled by the composition
        // scale and nothing else. They are applied here, among the bitmaps, rather than to the finished picture: they are
        // part of where the words are, so what is drawn is what the safe region answers and what the surface clips — a
        // group moved out of a strip by the host's own placement needs no room made for it, and one moved off the surface
        // is cut where it is drawn rather than where it was laid out.
        double scale = _surface.CompositionScale;
        List<Glyph> glyphs = ReadImages(images, renderX, renderY);
        List<GlyphGroup> groups = GroupGlyphs(glyphs);
        ZoomGroups(groups, zoom, pictureCenterX, pictureCenterY);
        PanGroups(groups, presentation.OffsetX * scale, presentation.OffsetY * scale);
        KeepClearOfInsets(groups, presentation.Insets, pictureX, pictureY, pictureWidth, pictureHeight, width, height, scale);
        MoveGlyphs(glyphs, groups);
        Composite(glyphs, width, height);

        _surface.SetOrder(presentation.Order);
        float opacity = (float)Math.Clamp(presentation.Opacity ?? 1.0, 0.0, 1.0);
        return DrawPixels(width, height, opacity);
    }

    #endregion

    #region Libass

    /// <summary>
    /// Builds the library and renderer once, and tells libass where fonts come from. The provider is the system's own —
    /// DirectWrite — because the build shipped here has no fontconfig: the fonts installed on the machine are the ones a
    /// script naming "Arial" is asking for, and the ones the file carries are added to the library before this is told.
    /// </summary>
    private OperationResult EnsureRenderer()
    {
        if (_renderer != IntPtr.Zero)
        {
            return OperationResult.Success();
        }

        OperationResult loaded = NativeLibraryLoader.EnsureLibass();
        if (!loaded.IsSuccessful)
        {
            return loaded;
        }

        _library = LibassNative.ass_library_init();
        if (_library == IntPtr.Zero)
        {
            return OperationResult.Failure("libass could not be initialised.");
        }

        AddAttachedFonts();

        _renderer = LibassNative.ass_renderer_init(_library);
        if (_renderer == IntPtr.Zero)
        {
            LibassNative.ass_library_done(_library);
            _library = IntPtr.Zero;
            return OperationResult.Failure("libass could not create a renderer.");
        }

        LibassNative.ass_set_fonts(_renderer, IntPtr.Zero, DefaultFontFamily, LibassNative.FontProviderDirectWrite, IntPtr.Zero, 1);
        _fontsConfigured = true;
        return OperationResult.Success();
    }

    private void ReleaseRenderer()
    {
        ReleaseTrack();

        if (_renderer != IntPtr.Zero)
        {
            LibassNative.ass_renderer_done(_renderer);
            _renderer = IntPtr.Zero;
        }

        if (_library != IntPtr.Zero)
        {
            LibassNative.ass_library_done(_library);
            _library = IntPtr.Zero;
        }

        _fontsConfigured = false;
        _attachments = [];
    }

    /// <summary>
    /// Makes the track the cues the pipeline just handed over, rebuilding it only when that set changed. The comparison is
    /// on the script itself, which is the honest one: two snapshots of the same cues produce the same text, and any change
    /// of text or timing produces a different one.
    /// </summary>
    private OperationResult EnsureTrack(IReadOnlyList<SubtitleCue> cues)
    {
        string script = AssSubtitleScript.Compose(_header, cues);
        if (_track != IntPtr.Zero && string.Equals(script, _builtScript, StringComparison.Ordinal))
        {
            return OperationResult.Success();
        }

        ReleaseTrack();

        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(script);
        _track = LibassNative.ass_read_memory(_library, bytes, (UIntPtr)(ulong)bytes.Length, IntPtr.Zero);
        if (_track == IntPtr.Zero)
        {
            return OperationResult.Failure("libass could not read the subtitle script.");
        }

        _builtScript = script;
        return OperationResult.Success();
    }

    private void ReleaseTrack()
    {
        if (_track != IntPtr.Zero)
        {
            LibassNative.ass_free_track(_track);
            _track = IntPtr.Zero;
        }

        _builtScript = null;
    }

    #endregion

    #region Draw

    private void Composite(List<Glyph> glyphs, int width, int height)
    {
        int length = width * height * 4;
        if (_pixels.Length != length)
        {
            _pixels = new byte[length];
        }
        else
        {
            Array.Clear(_pixels);
        }

        byte[] row = [];
        foreach (Glyph glyph in glyphs)
        {
            LibassNative.AssImage image = glyph.Image;

            // The colour is 0xRRGGBBAA and the alpha is transparency, as in the script: 0 is opaque.
            uint colour = image.Colour;
            int red = (int)((colour >> 24) & 0xFF);
            int green = (int)((colour >> 16) & 0xFF);
            int blue = (int)((colour >> 8) & 0xFF);
            int opacity = 255 - (int)(colour & 0xFF);
            if (opacity <= 0)
            {
                continue;
            }

            if (row.Length < image.Width)
            {
                row = new byte[image.Width];
            }

            for (int y = 0; y < image.Height; y++)
            {
                int destinationY = image.Top + y;
                if (destinationY < 0 || destinationY >= height)
                {
                    continue;
                }

                Marshal.Copy(image.Bitmap + (y * image.Stride), row, 0, image.Width);

                int left = image.Left;
                int startX = Math.Max(0, left);
                int endX = Math.Min(width, left + image.Width);
                int index = ((destinationY * width) + startX) * 4;

                for (int x = startX; x < endX; x++, index += 4)
                {
                    int coverage = row[x - left];
                    if (coverage == 0)
                    {
                        continue;
                    }

                    int alpha = (coverage * opacity) / 255;
                    if (alpha == 0)
                    {
                        continue;
                    }

                    int inverse = 255 - alpha;
                    _pixels[index] = (byte)(((blue * alpha) + (_pixels[index] * inverse)) / 255);
                    _pixels[index + 1] = (byte)(((green * alpha) + (_pixels[index + 1] * inverse)) / 255);
                    _pixels[index + 2] = (byte)(((red * alpha) + (_pixels[index + 2] * inverse)) / 255);
                    _pixels[index + 3] = (byte)(alpha + ((_pixels[index + 3] * inverse) / 255));
                }
            }
        }
    }

    /// <summary>
    /// Puts the composited frame on the surface. The opacity is the whole frame's, which fades the words and their borders
    /// together rather than one bitmap at a time, because libass has already finished deciding where things go.
    /// </summary>
    private OperationResult DrawPixels(int width, int height, float opacity)
    {
        CanvasDevice device = _surface.CanvasDevice ??
            throw new InvalidOperationException("The subtitle surface is not bound to a target, so it has no device.");
        using var bitmap = CanvasBitmap.CreateFromBytes(
            device,
            _pixels,
            width,
            height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            96.0f,
            CanvasAlphaMode.Premultiplied);

        Rect bounds = new(0.0f, 0.0f, width, height);
        return _surface.DrawAndPresent((session, _, _) => session.DrawImage(bitmap, bounds, bitmap.Bounds, opacity));
    }

    #endregion

    #region Attachment

    /// <summary>
    /// Hands libass the fonts the file carries, before it builds the font provider that will look them up: a font added
    /// afterwards is one the provider never saw. The font a script asks for is the family inside the file, not the name it
    /// is attached under, which is why the bytes matter rather than the name.
    /// </summary>
    private void AddAttachedFonts()
    {
        // A script may carry its own fonts in its [Fonts] section, which is the other half of "the file has what it needs".
        LibassNative.ass_set_extract_fonts(_library, 1);

        int added = 0;
        foreach (MediaAttachment attachment in _attachments)
        {
            if (!IsFont(attachment))
            {
                PlaybackLogger.W(TAG, $"The attachment '{attachment.Name}' is not a font, so libass was not given it");
                continue;
            }

            try
            {
                LibassNative.ass_add_font(_library, attachment.Name, attachment.Data.ToArray(), attachment.Data.Length);
                added++;
            }
            catch (Exception ex)
            {
                PlaybackLogger.W(TAG, $"The attached font '{attachment.Name}' could not be given to libass: {ex.Message}");
            }
        }

        if (added > 0)
        {
            PlaybackLogger.I(TAG, $"{added} attached font(s) are available to the script");
        }
    }

    private static bool IsFont(MediaAttachment attachment)
    {
        string? mimeType = attachment.MimeType;
        if (!string.IsNullOrEmpty(mimeType))
        {
            return mimeType.Contains("truetype", StringComparison.OrdinalIgnoreCase) ||
                mimeType.Contains("opentype", StringComparison.OrdinalIgnoreCase) ||
                mimeType.Contains("font", StringComparison.OrdinalIgnoreCase);
        }

        string extension = Path.GetExtension(attachment.Name);
        return extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".otf", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".otc", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the renderer holds exactly the fonts it was built with, which is what makes it reusable.</summary>
    private bool SameAttachments(IReadOnlyList<MediaAttachment> attachments)
    {
        if (!_fontsConfigured)
        {
            // Never built, so nothing was given anything yet and whatever arrives is what it will be built from.
            return true;
        }

        if (_attachments.Count != attachments.Count)
        {
            return false;
        }

        for (int i = 0; i < attachments.Count; i++)
        {
            if (!string.Equals(_attachments[i].Name, attachments[i].Name, StringComparison.Ordinal) ||
                _attachments[i].Data.Length != attachments[i].Data.Length)
            {
                return false;
            }
        }

        return true;
    }

    #endregion

    #region Placement

    /// <summary>
    /// The bitmaps libass produced, moved into the surface's own coordinates, each with the part of it that carries ink.
    /// Ones with nothing to draw are dropped here, so a bitmap left in the list is ink the surface is going to get.
    /// </summary>
    private static List<Glyph> ReadImages(IntPtr images, int originX, int originY)
    {
        List<Glyph> glyphs = [];
        byte[] row = [];
        for (IntPtr node = images; node != IntPtr.Zero;)
        {
            LibassNative.AssImage image = Marshal.PtrToStructure<LibassNative.AssImage>(node);
            node = image.Next;

            if (image.Bitmap == IntPtr.Zero || image.Width <= 0 || image.Height <= 0 || (image.Colour & 0xFF) == 0xFF)
            {
                continue;
            }

            if (row.Length < image.Width)
            {
                row = new byte[image.Width];
            }

            image.Left += originX;
            image.Top += originY;

            GlyphBox ink = InkOf(image, row);
            if (ink.IsEmpty)
            {
                continue;
            }

            glyphs.Add(new Glyph { Image = image, Ink = ink });
        }

        return glyphs;
    }

    /// <summary>
    /// The part of a bitmap that carries any coverage. libass sizes a bitmap by the font's box — its descent and leading
    /// included — and only what is actually drawn may push a group anywhere, or the words would sit further from the
    /// chrome than the colour they are painted in.
    /// </summary>
    private static GlyphBox InkOf(LibassNative.AssImage image, byte[] row)
    {
        int left = int.MaxValue;
        int top = int.MaxValue;
        int right = -1;
        int bottom = -1;
        for (int y = 0; y < image.Height; y++)
        {
            Marshal.Copy(image.Bitmap + (y * image.Stride), row, 0, image.Width);

            int first = -1;
            for (int x = 0; x < image.Width; x++)
            {
                if (row[x] != 0)
                {
                    first = x;
                    break;
                }
            }

            if (first < 0)
            {
                continue;
            }

            int last = first;
            for (int x = image.Width - 1; x > first; x--)
            {
                if (row[x] != 0)
                {
                    last = x;
                    break;
                }
            }

            left = Math.Min(left, first);
            right = Math.Max(right, last);
            top = Math.Min(top, y);
            bottom = y;
        }

        return right < left ? GlyphBox.None : new GlyphBox(image.Left + left, image.Top + top, image.Left + right, image.Top + bottom);
    }

    /// <summary>
    /// The bitmaps of a frame, grouped into the runs that belong together: the colours of a line of words, and the lines
    /// of one cue. libass gives a bitmap per colour rather than one per cue, so this geometry is the only grouping there
    /// is, and a group is what the zoom and the safe region each act on.
    /// </summary>
    private static List<GlyphGroup> GroupGlyphs(List<Glyph> glyphs)
    {
        int[] order = new int[glyphs.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        // Only the order the groups are found in is decided here; the bitmaps themselves are composited in the order
        // libass drew them, which is what a colour laid over another depends on.
        Array.Sort(order, (a, b) => glyphs[a].Ink.Top != glyphs[b].Ink.Top
            ? glyphs[a].Ink.Top.CompareTo(glyphs[b].Ink.Top)
            : glyphs[a].Ink.Left.CompareTo(glyphs[b].Ink.Left));

        List<GlyphGroup> groups = [];
        int start = 0;
        while (start < order.Length)
        {
            int end = GroupEnd(glyphs, order, start);
            groups.Add(new GlyphGroup(BoundsOf(glyphs, order, start, end), order[start..end]));
            start = end;
        }

        return groups;
    }

    /// <summary>
    /// The zoom, group by group. Rendering the script at a larger frame draws the words at the zoomed size but also lays
    /// them out around the frame's centre, and taking that displacement back is what grows a group about its own centre:
    /// it keeps the place the script gave it. A group near an edge therefore grows past it and is cut by it, exactly as a
    /// picture the surface cannot hold is cut — the zoom changes the size, not where the words were put.
    /// </summary>
    private static void ZoomGroups(List<GlyphGroup> groups, double zoom, double pictureCenterX, double pictureCenterY)
    {
        if (zoom == 1.0)
        {
            return;
        }

        double factor = (1.0 / zoom) - 1.0;
        foreach (GlyphGroup group in groups)
        {
            double centerX = (group.Bounds.Left + group.Bounds.Right) / 2.0;
            double centerY = (group.Bounds.Top + group.Bounds.Bottom) / 2.0;
            group.Move((int)Math.Round((centerX - pictureCenterX) * factor), (int)Math.Round((centerY - pictureCenterY) * factor));
        }
    }

    /// <summary>
    /// The strips the host's chrome covers, group by group: a group a strip covers is moved clear of it, and a group beside
    /// it keeps its place. libass's own margins would mean "the window is bigger than the video" and answer by scaling the
    /// script down, and a glyph that changes size with a bar is worse than one that sits higher.
    /// </summary>
    private static void KeepClearOfInsets(List<GlyphGroup> groups, SubtitleInsets insets, int pictureX, int pictureY, int pictureWidth, int pictureHeight, int width, int height, double scale)
    {
        if (insets.IsNone)
        {
            return;
        }

        // The strips are measured from the surface's edges, because that is where the host's chrome is; only the part of a
        // strip that overlaps the picture can push words anywhere.
        int pictureRight = pictureX + pictureWidth;
        int pictureBottom = pictureY + pictureHeight;
        int left = Math.Max(pictureX, (int)Math.Round(insets.Left * scale));
        int top = Math.Max(pictureY, (int)Math.Round(insets.Top * scale));
        int right = Math.Min(pictureRight, width - (int)Math.Round(insets.Right * scale));
        int bottom = Math.Min(pictureBottom, height - (int)Math.Round(insets.Bottom * scale));
        if (right <= left || bottom <= top)
        {
            // Chrome that covers the whole picture cannot be kept clear of: the script's own placement is left alone.
            return;
        }

        foreach (GlyphGroup group in groups)
        {
            group.Move(
                (int)Math.Round(ShiftFor(group.Bounds.Left, group.Bounds.Right, left, right, pictureX, pictureRight)),
                (int)Math.Round(ShiftFor(group.Bounds.Top, group.Bounds.Bottom, top, bottom, pictureY, pictureBottom)));
        }
    }

    /// <summary>Moves every group of words by the host's own placement of them, in pixels of the surface.</summary>
    private static void PanGroups(List<GlyphGroup> groups, double offsetX, double offsetY)
    {
        int x = double.IsFinite(offsetX) ? (int)Math.Round(offsetX) : 0;
        int y = double.IsFinite(offsetY) ? (int)Math.Round(offsetY) : 0;
        foreach (GlyphGroup group in groups)
        {
            group.Move(x, y);
        }
    }

    private static void MoveGlyphs(List<Glyph> glyphs, List<GlyphGroup> groups)
    {
        foreach (GlyphGroup group in groups)
        {
            if (group.X == 0 && group.Y == 0)
            {
                continue;
            }

            foreach (int index in group.Indices)
            {
                Glyph glyph = glyphs[index];
                glyph.Move(group.X, group.Y);
                glyphs[index] = glyph;
            }
        }
    }

    /// <summary>
    /// Where the group beginning at <paramref name="start"/> ends: the bitmaps whose bands are close enough to be the words
    /// of one block. The allowance is half the shorter of the two boxes, which holds the lines of one cue together without
    /// reaching far enough to swallow a cue below it.
    /// </summary>
    private static int GroupEnd(List<Glyph> glyphs, int[] order, int start)
    {
        int top = glyphs[order[start]].Ink.Top;
        int bottom = glyphs[order[start]].Ink.Bottom;

        int index = start + 1;
        while (index < order.Length)
        {
            GlyphBox ink = glyphs[order[index]].Ink;
            int gap = ink.Top - bottom;
            int allowance = Math.Min(ink.Bottom - ink.Top + 1, bottom - top + 1) / 2;
            if (gap > allowance)
            {
                break;
            }

            bottom = Math.Max(bottom, ink.Bottom);
            index++;
        }

        return index;
    }

    private static GlyphBox BoundsOf(List<Glyph> glyphs, int[] order, int start, int end)
    {
        GlyphBox bounds = GlyphBox.None;
        for (int i = start; i < end; i++)
        {
            bounds = bounds.Union(glyphs[order[i]].Ink);
        }

        return bounds;
    }

    private static float ShiftFor(int inkStart, int inkEnd, int allowedStart, int allowedEnd, int frameStart, int frameEnd)
    {
        float shift;
        if (inkEnd > allowedEnd && inkStart >= allowedStart)
        {
            shift = allowedEnd - inkEnd;
        }
        else if (inkStart < allowedStart && inkEnd <= allowedEnd)
        {
            shift = allowedStart - inkStart;
        }
        else
        {
            return 0.0f;
        }

        float lowest = frameStart - inkStart;
        float highest = frameEnd - inkEnd;
        return Math.Clamp(shift, Math.Min(lowest, highest), Math.Max(lowest, highest));
    }

    /// <summary>A rectangle of the surface, its edges included.</summary>
    private readonly record struct GlyphBox(int Left, int Top, int Right, int Bottom)
    {
        internal static GlyphBox None => new(0, 0, -1, -1);

        internal bool IsEmpty => Right < Left || Bottom < Top;

        internal GlyphBox Offset(int dx, int dy)
        {
            return new GlyphBox(Left + dx, Top + dy, Right + dx, Bottom + dy);
        }

        internal GlyphBox Union(GlyphBox other)
        {
            if (IsEmpty)
            {
                return other;
            }

            return other.IsEmpty
                ? this
                : new GlyphBox(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
        }
    }

    /// <summary>One bitmap libass produced, and the part of it that carries ink, in the surface's own coordinates.</summary>
    private struct Glyph
    {
        internal LibassNative.AssImage Image;
        internal GlyphBox Ink;

        internal void Move(int dx, int dy)
        {
            Image.Left += dx;
            Image.Top += dy;
            Ink = Ink.Offset(dx, dy);
        }
    }

    /// <summary>
    /// A run of bitmaps that belongs together, with the rectangle it covers on the surface and how far it has been moved.
    /// The bitmaps themselves are only names here: they are moved once, when every group has been placed.
    /// </summary>
    private sealed class GlyphGroup(GlyphBox bounds, int[] indices)
    {
        internal GlyphBox Bounds { get; private set; } = bounds;

        internal int[] Indices { get; } = indices;

        internal int X { get; private set; }

        internal int Y { get; private set; }

        internal void Move(int dx, int dy)
        {
            if (dx == 0 && dy == 0)
            {
                return;
            }

            X += dx;
            Y += dy;
            Bounds = Bounds.Offset(dx, dy);
        }
    }

    #endregion
}
