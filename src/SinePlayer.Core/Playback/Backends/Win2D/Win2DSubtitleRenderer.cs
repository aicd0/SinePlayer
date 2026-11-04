// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Numerics;
using System.Runtime.InteropServices;

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;

using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Subtitle;

using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using Windows.UI.Text;

namespace SinePlayer.Core.Playback.Backends.Win2D;

public sealed partial class Win2DSubtitleRenderer : ISubtitleRenderer
{
    #region Constructor

    private const string TAG = nameof(Win2DSubtitleRenderer);

    private const ushort DefaultFontWeight = 400;
    private const float FontSizeFraction = 0.045f;
    private const float MinimumFontSize = 12.0f;
    private const float MaximumFontSize = 72.0f;
    private const float MarginFraction = 0.03f;
    private const double MaximumInsetFraction = 0.9;
    private const float StackGapFraction = 0.15f;

    private bool _disposed;
    private readonly SubtitleSurface _surface = new();

    public string Name => "Win2D";

    #endregion

    #region Public API

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearOutlines();
        _surface.Dispose();
    }

    public OperationResult Configure(SubtitleStreamInfo stream, IVideoRenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(target);

        if (_disposed)
        {
            return OperationResult.Failure("The subtitle renderer has already been disposed.");
        }

        _surface.BindTarget(target);
        return _surface.EnsureSurface();
    }

    public OperationResult Show(IReadOnlyList<SubtitleCue> cues, TimeSpan position, SubtitlePresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(cues);

        if (_disposed)
        {
            return OperationResult.Failure("The subtitle renderer has already been disposed.");
        }

        // The surface may have been resized since the last drawing, so the size is read every time rather than
        // remembered from Configure: a caller redraws after a resize precisely so this can take effect.
        OperationResult sized = _surface.EnsureSurface();
        if (!sized.IsSuccessful)
        {
            return sized;
        }

        SubtitleSurface surface = _surface;
        return surface.DrawAndPresent(session => DrawCues(session, cues, presentation, surface.Width, surface.Height, surface.CompositionScale));
    }

    /// <summary>
    /// The device text is laid out with, built on first use. A test that wants to check the drawing against an offscreen
    /// target uses this rather than a swap chain, which cannot be read back after it has presented.
    /// </summary>
    internal CanvasDevice EnsureCanvasDevice()
    {
        return _surface.EnsureCanvasDevice();
    }

    internal void DrawCues(CanvasDrawingSession session, IReadOnlyList<SubtitleCue> cues, SubtitlePresentation presentation, int width, int height, double scale = 1.0)
    {
        if (cues.Count == 0 || width <= 0 || height <= 0)
        {
            // A cleared frame is a fully transparent one, which is what shows the picture and nothing else.
            return;
        }

        // An explicit size is in device independent pixels, so it is scaled to the surface it lands on; the automatic one
        // stays a share of the surface's own pixels, which is what it has always been.
        float fontSize = presentation.FontSize > 0.0
            ? (float)(presentation.FontSize * scale)
            : Math.Clamp(height * FontSizeFraction, MinimumFontSize, MaximumFontSize);
        float margin = height * MarginFraction;
        float gap = fontSize * StackGapFraction;
        SubtitleArea area = AreaOf(presentation, scale, width, height);

        // How much of each band is taken, by the alignment's row: bottom, middle, top. Several events can be up at once —
        // the same speaker's two lines, or a sign beside dialogue — and they share a band far more often than not. Each is
        // moved away from the edge its alignment names by what is already there, so they stack instead of landing on the
        // very spot the one before them took.
        float[] taken = new float[3];

        using CanvasTextFormat format = new()
        {
            FontFamily = string.IsNullOrEmpty(presentation.FontFamily) ? SubtitleFonts.DefaultFontFamily : presentation.FontFamily,
            FontSize = fontSize,
            FontStyle = presentation.FontStyle,
            FontWeight = presentation.FontWeight.Weight > 0 ? presentation.FontWeight : new FontWeight { Weight = DefaultFontWeight },
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        foreach (SubtitleCue cue in cues)
        {
            if (cue.Image is { } image)
            {
                DrawImage(session, image, presentation, area, width, height, scale);
                continue;
            }

            var text = AssSubtitleText.Parse(cue.Text);
            if (text.IsEmpty)
            {
                continue;
            }

            string body = string.Join("\n", text.Lines);
            using CanvasTextLayout layout = new(EnsureCanvasDevice(), body, format, width, height);

            int row = RowOf(text.Alignment);
            float stacked = taken[row];
            taken[row] += (float)layout.LayoutBounds.Height + gap;

            Vector2 origin = Anchor(layout, row, text.Alignment, area, margin);

            // The bottom and middle bands grow upwards, the top band downwards: the first cue keeps the place its alignment
            // asked for, and the ones after it make room above or below rather than covering it.
            origin.Y += row == 2 ? stacked : -stacked;

            origin += new Vector2((float)(presentation.OffsetX * scale), (float)(presentation.OffsetY * scale));
            origin = KeepInsideArea(origin, layout, area);

            if (presentation.OutlineWidth > 0.0)
            {
                // The stroke runs down the middle of the glyph's edge, so half of it would be covered by the words. Twice
                // the width is drawn and the words fill over the inner half, which leaves the border growing outwards by
                // exactly the width asked for.
                CanvasGeometry? outline = EnsureOutlineGeometry(body, format, width, height);
                if (outline is not null)
                {
                    session.DrawGeometry(outline, origin, OutlineColor, (float)(2.0 * presentation.OutlineWidth * scale), OutlineStrokeStyle);
                }
            }

            session.DrawTextLayout(layout, origin, Color.FromArgb(255, 255, 255, 255));
        }
    }

    #endregion

    #region Bitmap

    private void DrawImage(CanvasDrawingSession session, SubtitleImage image, SubtitlePresentation presentation, SubtitleArea area, int width, int height, double scale)
    {
        ISubtitleImageBuffer buffer = image.Buffer;
        if (buffer.Width <= 0 || buffer.Height <= 0)
        {
            return;
        }

        byte[] pixels = new byte[buffer.Stride * buffer.Height];
        Marshal.Copy(buffer.Pixels, pixels, 0, pixels.Length);

        using var bitmap = CanvasBitmap.CreateFromBytes(
            EnsureCanvasDevice(),
            pixels,
            buffer.Width,
            buffer.Height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            96.0f,
            CanvasAlphaMode.Premultiplied);

        // The image's own geometry is a fraction of the frame it was authored against, which is the whole surface; the
        // presentation's offset is in pixels, so it is added after that geometry is scaled.
        double x = (image.Left * width) + (presentation.OffsetX * scale);
        double y = (image.Top * height) + (presentation.OffsetY * scale);
        double scaledWidth = image.Width * width;
        double scaledHeight = image.Height * height;

        // Kept in the free part of the surface for the same reason the text is: an adjustment that pushes a cue out of it
        // is cut back rather than obeyed, and a surface that later changes must not strand it.
        double maxX = Math.Max(area.Left, area.Left + area.Width - scaledWidth);
        double maxY = Math.Max(area.Top, area.Top + area.Height - scaledHeight);
        x = Math.Clamp(x, area.Left, maxX);
        y = Math.Clamp(y, area.Top, maxY);

        session.DrawImage(bitmap, new Rect(x, y, scaledWidth, scaledHeight));
    }

    #endregion

    #region Outline

    private const int OutlineCacheLimit = 64;

    private static readonly Color OutlineColor = Color.FromArgb(255, 0, 0, 0);
    private static readonly CanvasStrokeStyle OutlineStrokeStyle = new() { LineJoin = CanvasLineJoin.Round };

    private readonly Dictionary<OutlineKey, CanvasGeometry> _outlines = [];
    private CanvasDevice? _outlineDevice;

    /// <summary>
    /// The outline of one cue's words, as the path the glyphs draw, kept for as long as the words and the style that shape
    /// it stay the same. The geometry follows the whole layout, so the granularity is one cue — a line or a word cannot be
    /// taken out on its own without laying it out again, and laying text out per letter would lose the kerning and shaping
    /// that make it look right.
    /// </summary>
    private CanvasGeometry? EnsureOutlineGeometry(string body, CanvasTextFormat format, int width, int height)
    {
        CanvasDevice device = EnsureCanvasDevice();
        if (_outlineDevice != device)
        {
            // A geometry belongs to the device that made it, so one made by a device that is gone cannot be drawn with.
            ClearOutlines();
            _outlineDevice = device;
        }

        OutlineKey key = new(body, format.FontFamily, format.FontSize, (int)format.FontStyle, format.FontWeight.Weight, width, height);
        if (_outlines.TryGetValue(key, out CanvasGeometry? cached))
        {
            return cached;
        }

        if (_outlines.Count >= OutlineCacheLimit)
        {
            ClearOutlines();
        }

        try
        {
            using CanvasTextLayout layout = new(device, body, format, width, height);
            var outline = CanvasGeometry.CreateText(layout);
            _outlines[key] = outline;
            return outline;
        }
        catch (Exception ex)
        {
            Logger.E(TAG, "The outline of a cue could not be built", ex);
            return null;
        }
    }

    private void ClearOutlines()
    {
        foreach (CanvasGeometry outline in _outlines.Values)
        {
            outline.Dispose();
        }

        _outlines.Clear();
    }

    /// <summary>What shapes one cue's outline: the words, and the style they are drawn in at the size of the surface.</summary>
    private readonly record struct OutlineKey(string Text, string Family, float Size, int Style, ushort Weight, int Width, int Height);

    #endregion

    #region Area calculation

    private static SubtitleArea AreaOf(SubtitlePresentation presentation, double scale, int width, int height)
    {
        float left = (float)Math.Clamp(presentation.Insets.Left * scale, 0.0, width * MaximumInsetFraction);
        float top = (float)Math.Clamp(presentation.Insets.Top * scale, 0.0, height * MaximumInsetFraction);
        float right = (float)Math.Clamp(presentation.Insets.Right * scale, 0.0, width * MaximumInsetFraction);
        float bottom = (float)Math.Clamp(presentation.Insets.Bottom * scale, 0.0, height * MaximumInsetFraction);

        return new SubtitleArea(left, top, Math.Max(1.0f, width - left - right), Math.Max(1.0f, height - top - bottom));
    }

    /// <summary>
    /// Which band of the surface an alignment belongs to: 0 bottom, 1 middle, 2 top. The three alignments in a band share a
    /// height, so they are what has to stack together.
    /// </summary>
    private static int RowOf(int alignment)
    {
        return (Math.Clamp(alignment, 1, 9) - 1) / 3;
    }

    /// <summary>The rectangle of the surface the cues are laid out in, in physical pixels.</summary>
    private readonly record struct SubtitleArea(float Left, float Top, float Width, float Height);

    /// <summary>
    /// Where a block of text goes before any adjustment: the ASS anchor, laid out against the free part of the surface
    /// with a margin from the edge it is anchored to. The layout is measured first, because centring needs its width.
    /// </summary>
    private static Vector2 Anchor(CanvasTextLayout layout, int row, int alignment, SubtitleArea area, float margin)
    {
        int column = (Math.Clamp(alignment, 1, 9) - 1) % 3;

        float x = column switch
        {
            0 => margin,
            2 => area.Width - (float)layout.LayoutBounds.Width - margin,
            _ => (area.Width - (float)layout.LayoutBounds.Width) / 2.0f,
        };

        float y = row switch
        {
            0 => area.Height - (float)layout.LayoutBounds.Height - margin,
            2 => margin,
            _ => (area.Height - (float)layout.LayoutBounds.Height) / 2.0f,
        };

        return new Vector2(area.Left + x, area.Top + y);
    }

    /// <summary>
    /// Keeps the text where it can be read. An adjustment that would push it out of the free part of the surface is cut
    /// back, because the point of adjusting subtitles is still to read them — and the player's own chrome, which moves as
    /// it is used, must not strand them.
    /// </summary>
    private static Vector2 KeepInsideArea(Vector2 origin, CanvasTextLayout layout, SubtitleArea area)
    {
        double maxX = Math.Max(area.Left, area.Left + area.Width - layout.LayoutBounds.Width);
        double maxY = Math.Max(area.Top, area.Top + area.Height - layout.LayoutBounds.Height);
        return new Vector2((float)Math.Clamp(origin.X, (double)area.Left, maxX), (float)Math.Clamp(origin.Y, (double)area.Top, maxY));
    }

    #endregion
}
