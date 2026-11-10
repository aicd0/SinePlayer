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

public sealed partial class AssSubtitleRenderer : ISubtitleRenderer
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
        int frameX = (int)Math.Round(frame.Left * width);
        int frameY = (int)Math.Round(frame.Top * height);
        int frameWidth = Math.Max(1, (int)Math.Round(frame.Width * width));
        int frameHeight = Math.Max(1, (int)Math.Round(frame.Height * height));

        LibassNative.ass_set_frame_size(_renderer, frameWidth, frameHeight);

        long now = (long)position.TotalMilliseconds;
        IntPtr images = LibassNative.ass_render_frame(_renderer, _track, now, out _);

        Composite(images, width, height, frameX, frameY);

        // The presentation's offsets are pixels of the surface's own coordinate space, so they are scaled by the composition
        // scale and nothing else.
        double scale = _surface.CompositionScale;
        float offsetX = (float)(presentation.OffsetX * scale);
        float offsetY = (float)(presentation.OffsetY * scale);

        // Then the strips the host's chrome covers are kept clear of the words, by moving the picture rather than reshaping
        // it: libass's own margins mean "the window is bigger than the video" and answer by scaling the script down, and a
        // glyph that changes size with a bar is worse than one that sits higher.
        (float shiftX, float shiftY) = SafeShift(presentation, frameX, frameY, frameWidth, frameHeight, width, height, scale);
        ShiftPixels((int)Math.Round(shiftX), (int)Math.Round(shiftY), width, height);

        return DrawPixels(offsetX, offsetY, width, height);
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

    private void Composite(IntPtr images, int width, int height, int originX, int originY)
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
        for (IntPtr node = images; node != IntPtr.Zero;)
        {
            LibassNative.AssImage image = Marshal.PtrToStructure<LibassNative.AssImage>(node);
            node = image.Next;

            if (image.Bitmap == IntPtr.Zero || image.Width <= 0 || image.Height <= 0)
            {
                continue;
            }

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
                int destinationY = image.Top + originY + y;
                if (destinationY < 0 || destinationY >= height)
                {
                    continue;
                }

                Marshal.Copy(image.Bitmap + (y * image.Stride), row, 0, image.Width);

                int left = image.Left + originX;
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
    /// Puts the composited frame on the surface. The offset is the host's own adjustment of where subtitles sit, applied to
    /// the whole frame rather than to each bitmap, because libass has already finished deciding where things go.
    /// </summary>
    private OperationResult DrawPixels(float offsetX, float offsetY, int width, int height)
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

        Rect bounds = new(offsetX, offsetY, width, height);
        return _surface.DrawAndPresent((session, _, _) => session.DrawImage(bitmap, bounds));
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

    #region Shift

    private void ShiftPixels(int dx, int dy, int width, int height)
    {
        if (dx == 0 && dy == 0)
        {
            return;
        }

        int stride = width * 4;
        if (dy != 0)
        {
            int rows = height - Math.Abs(dy);
            if (rows <= 0)
            {
                Array.Clear(_pixels);
                return;
            }

            int sourceRow = dy > 0 ? 0 : -dy;
            int targetRow = dy > 0 ? dy : 0;
            Array.Copy(_pixels, sourceRow * stride, _pixels, targetRow * stride, rows * stride);
            if (dy > 0)
            {
                Array.Clear(_pixels, 0, dy * stride);
            }
            else
            {
                Array.Clear(_pixels, rows * stride, -dy * stride);
            }
        }

        if (dx == 0)
        {
            return;
        }

        int bytes = (width - Math.Abs(dx)) * 4;
        if (bytes <= 0)
        {
            Array.Clear(_pixels);
            return;
        }

        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            if (dx > 0)
            {
                Array.Copy(_pixels, row, _pixels, row + (dx * 4), bytes);
                Array.Clear(_pixels, row, dx * 4);
            }
            else
            {
                Array.Copy(_pixels, row - (dx * 4), _pixels, row, bytes);
                Array.Clear(_pixels, row + bytes, -dx * 4);
            }
        }
    }

    private (float X, float Y) SafeShift(SubtitlePresentation presentation, int frameX, int frameY, int frameWidth, int frameHeight, int width, int height, double scale)
    {
        SubtitleInsets insets = presentation.Insets;
        if (insets.IsNone)
        {
            return (0.0f, 0.0f);
        }

        if (!TryGetInkBounds(width, height, out int inkLeft, out int inkTop, out int inkRight, out int inkBottom))
        {
            return (0.0f, 0.0f);
        }

        // The strips are measured from the surface's edges, because that is where the host's chrome is; only the part of a
        // strip that overlaps the picture can push words anywhere.
        int pictureRight = frameX + frameWidth;
        int pictureBottom = frameY + frameHeight;
        int left = Math.Max(frameX, (int)Math.Round(insets.Left * scale));
        int top = Math.Max(frameY, (int)Math.Round(insets.Top * scale));
        int right = Math.Min(pictureRight, width - (int)Math.Round(insets.Right * scale));
        int bottom = Math.Min(pictureBottom, height - (int)Math.Round(insets.Bottom * scale));
        if (right <= left || bottom <= top)
        {
            // Chrome that covers the whole picture cannot be kept clear of: the script's own placement is left alone.
            return (0.0f, 0.0f);
        }

        float shiftX = ShiftFor(inkLeft, inkRight, left, right, frameX, pictureRight);
        float shiftY = ShiftFor(inkTop, inkBottom, top, bottom, frameY, pictureBottom);
        return (shiftX, shiftY);
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

    private bool TryGetInkBounds(int width, int height, out int left, out int top, out int right, out int bottom)
    {
        left = int.MaxValue;
        top = int.MaxValue;
        right = -1;
        bottom = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (_pixels[((y * width) + x) * 4 + 3] == 0)
                {
                    continue;
                }

                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        return right >= left && bottom >= top;
    }

    #endregion
}
