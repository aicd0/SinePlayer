// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using Microsoft.Graphics.Canvas;

using SinePlayer.Core.Common.AppEnvironment;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Backends.Win2D;

using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace SinePlayer.Core.Playback.Backends.Libass;

/// <summary>
/// Draws ASS and SSA subtitles with libass, composited into the same surface the other renderer draws into.
/// <para>
/// libass decides everything about the picture — styles, positions, collision between events, vector drawings, animation —
/// and hands back one coverage bitmap per colour. This class does the two things libass cannot: it says what moment to
/// draw (the pipeline's position, not libass's own idea of time, of which it has none), and it puts the bitmaps on the
/// screen. That is the whole contract, and it is why the pipeline schedules redraws from the cue's own change windows
/// rather than from anything libass reports.
/// </para>
/// <para>
/// The script is rebuilt when the set of cues changes and not otherwise, so an animation running inside one cue costs a
/// rasterisation rather than a parse and a new track.
/// </para>
/// </summary>
public sealed partial class AssSubtitleRenderer : ISubtitleRenderer
{
    /// <summary>The family a script's missing font falls back to, which every Windows installation has.</summary>
    private const string DefaultFontFamily = "Arial";

    private readonly SubtitleSurface _surface = new();

    private IntPtr _library;
    private IntPtr _renderer;
    private IntPtr _track;

    /// <summary>What the track was built from, so that the same cues are not parsed again on every redraw.</summary>
    private string? _builtScript;
    private string? _header;
    private byte[] _pixels = [];
    private bool _disposed;

    public string Name => "libass";

    /// <summary>
    /// The frame the last <see cref="Show"/> composited, before it went to the surface — what a test reads to say something
    /// about the pixels without a window to read them back from.
    /// </summary>
    internal ReadOnlySpan<byte> CompositedFrame => _pixels;

    public OperationResult Configure(SubtitleStreamInfo stream, IVideoRenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(stream);
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

        _header = stream.ScriptHeader;
        _surface.BindTarget(target);

        OperationResult sized = _surface.EnsureSurface();
        if (!sized.IsSuccessful)
        {
            return sized;
        }

        return EnsureRenderer();
    }

    public OperationResult Show(IReadOnlyList<SubtitleCue> cues, TimeSpan position, SubtitlePlacement placement)
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

        // The frame is the surface, and the host's chrome is not given to libass. Its margins do not mean "keep clear of
        // this strip": they say the window is bigger than the video, and the script is scaled to what is left over —
        // measured on 0.17.5, a 1080-high frame with a 200px bottom margin draws an 81px glyph 65px high, because
        // 880/1080 is the scale it derives. An ASS script places its own events, so a bar passing over a line is a better
        // answer than words that change size with the bar.
        LibassNative.ass_set_frame_size(_renderer, width, height);

        long now = (long)position.TotalMilliseconds;
        IntPtr images = LibassNative.ass_render_frame(_renderer, _track, now, out _);

        Composite(images, width, height);

        // The placement's offsets are pixels of the surface's own coordinate space, so they are scaled by the composition
        // scale and nothing else: the frame is already the surface, so "ten pixels to the right" means ten of them.
        double scale = _surface.CompositionScale;
        float offsetX = (float)(placement.OffsetX * scale);
        float offsetY = (float)(placement.OffsetY * scale);
        return DrawPixels(offsetX, offsetY, width, height);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
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

        _surface.Dispose();
    }

    /// <summary>
    /// Builds the library and renderer once, and tells libass where fonts come from. The provider is the system's own —
    /// DirectWrite — because the build shipped here has no fontconfig: the fonts installed on the machine are the ones a
    /// script naming "Arial" is asking for.
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

        _renderer = LibassNative.ass_renderer_init(_library);
        if (_renderer == IntPtr.Zero)
        {
            LibassNative.ass_library_done(_library);
            _library = IntPtr.Zero;
            return OperationResult.Failure("libass could not create a renderer.");
        }

        LibassNative.ass_set_fonts(_renderer, IntPtr.Zero, DefaultFontFamily, LibassNative.FontProviderDirectWrite, IntPtr.Zero, 1);
        return OperationResult.Success();
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

    /// <summary>
    /// Blends libass's bitmaps, in the order it gives them, onto a transparent premultiplied frame: source over
    /// destination, one coverage bitmap per colour. The frame is the surface, so nothing is scaled here.
    /// </summary>
    private void Composite(IntPtr images, int width, int height)
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
                int destinationY = image.Top + y;
                if (destinationY < 0 || destinationY >= height)
                {
                    continue;
                }

                Marshal.Copy(image.Bitmap + (y * image.Stride), row, 0, image.Width);

                int startX = Math.Max(0, image.Left);
                int endX = Math.Min(width, image.Left + image.Width);
                int index = ((destinationY * width) + startX) * 4;

                for (int x = startX; x < endX; x++, index += 4)
                {
                    int coverage = row[x - image.Left];
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
        CanvasDevice device = _surface.EnsureCanvasDevice();
        using var bitmap = CanvasBitmap.CreateFromBytes(
            device,
            _pixels,
            width,
            height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            96.0f,
            CanvasAlphaMode.Premultiplied);

        Rect bounds = new(offsetX, offsetY, width, height);
        return _surface.DrawAndPresent(session => session.DrawImage(bitmap, bounds));
    }
}
