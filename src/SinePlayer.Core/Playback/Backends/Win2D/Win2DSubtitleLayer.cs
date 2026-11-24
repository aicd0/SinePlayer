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
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Subtitle;

using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using Windows.UI.Text;

namespace SinePlayer.Core.Playback.Backends.Win2D;

internal sealed partial class Win2DSubtitleLayer : IDisposable
{
    #region Constants

    private const string TAG = nameof(Win2DSubtitleLayer);

    private const ushort DefaultFontWeight = 400;
    private const float FontSizeFraction = 0.045f;
    private const float MinimumFontSize = 12.0f;
    private const float MaximumFontSize = 72.0f;
    private const float ReferenceSurfaceHeight = 720.0f;
    private const float MarginFraction = 0.03f;
    private const double MaximumInsetFraction = 0.9;
    private const float StackGapFraction = 0.15f;
    private const float DefaultShadowDistanceFraction = 0.06f;

    #endregion

    #region Pool

    private static readonly Lock _poolLock = new();
    private static readonly Dictionary<IVideoRenderTarget, Win2DSubtitleLayer> _pool = [];

    internal static Win2DSubtitleLayer Join(IVideoRenderTarget target, object participant)
    {
        lock (_poolLock)
        {
            if (!_pool.TryGetValue(target, out Win2DSubtitleLayer? layer))
            {
                layer = new Win2DSubtitleLayer(target);
                _pool.Add(target, layer);
            }

            layer.Add(participant);
            return layer;
        }
    }

    internal void Leave(object participant)
    {
        if (!Release(participant))
        {
            return;
        }

        lock (_poolLock)
        {
            lock (_lock)
            {
                if (_participants.Count > 0)
                {
                    return;
                }
            }

            if (_target is { } target)
            {
                _pool.Remove(target);
            }
        }

        Dispose();
    }

    private void Add(object participant)
    {
        lock (_lock)
        {
            if (!_participants.Contains(participant))
            {
                _participants.Add(participant);
            }
        }
    }

    private bool Release(object participant)
    {
        lock (_lock)
        {
            _participants.Remove(participant);
            _frames.Remove(participant);
            return _participants.Count == 0;
        }
    }

    #endregion

    #region Construction

    private readonly Lock _lock = new();

    private readonly IVideoRenderTarget _target;
    private readonly SubtitleSurface _surface;
    private readonly OutlineCache _outlines = new();
    private readonly List<object> _participants = [];
    private readonly Dictionary<object, CueFrame> _frames = [];
    private bool _disposed;

    internal CanvasDevice CanvasDevice => _surface.CanvasDevice ?? throw new InvalidOperationException("The subtitle layer has no device to draw with.");

    private sealed record CueFrame(IReadOnlyList<SubtitleCue> Cues, SubtitlePresentation Presentation);

    private Win2DSubtitleLayer(IVideoRenderTarget target)
    {
        _target = target;
        _surface = new SubtitleSurface();
        _surface.BindTarget(target);
    }

    #endregion

    #region Public API

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _frames.Clear();
            _participants.Clear();
            _outlines.Dispose();
            _surface.Dispose();
        }
    }

    internal void SetCues(object participant, IReadOnlyList<SubtitleCue> cues, SubtitlePresentation presentation)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _frames[participant] = new CueFrame(cues, presentation);
        }
    }

    #endregion

    #region Surface

    internal OperationResult EnsureSurface()
    {
        return _surface.EnsureSurface();
    }

    internal OperationResult Draw()
    {
        OperationResult sized = _surface.EnsureSurface();
        if (!sized.IsSuccessful)
        {
            return sized;
        }

        CueFrame[] frames;
        lock (_lock)
        {
            if (_disposed)
            {
                return OperationResult.Failure("The Win2D subtitle layer has been disposed.");
            }

            frames = [.. _participants
                .Select(participant => _frames.TryGetValue(participant, out CueFrame? frame) ? frame : null)
                .OfType<CueFrame>()
                .OrderByDescending(frame => frame.Presentation.Order)];
        }

        CanvasDevice device = CanvasDevice;
        double scale = _surface.CompositionScale;

        if (frames.Length > 0)
        {
            _surface.SetOrder(frames[^1].Presentation.Order);
        }

        return _surface.DrawAndPresent((session, width, height) =>
        {
            float[] taken = new float[3];
            foreach (CueFrame frame in frames)
            {
                DrawCuesInto(session, device, frame.Cues, frame.Presentation, width, height, scale, taken, _outlines);
            }
        });
    }

    #endregion

    #region Drawing

    internal static void DrawCues(CanvasDrawingSession session, CanvasDevice device, IReadOnlyList<SubtitleCue> cues, SubtitlePresentation presentation, int width, int height, double scale = 1.0)
    {
        using OutlineCache outlines = new();
        DrawCuesInto(session, device, cues, presentation, width, height, scale, new float[3], outlines);
    }

    private static void DrawCuesInto(CanvasDrawingSession session, CanvasDevice device, IReadOnlyList<SubtitleCue> cues, SubtitlePresentation presentation, int width, int height, double scale, float[] taken, OutlineCache outlines)
    {
        if (cues.Count == 0 || width <= 0 || height <= 0)
        {
            // A cleared frame is a fully transparent one, which is what shows the picture and nothing else.
            return;
        }

        float fontSize;
        if (presentation.FixedFontSize)
        {
            fontSize = presentation.FontSize > 0.0
                ? (float)(presentation.FontSize * scale)
                : (float)Math.Clamp(ReferenceSurfaceHeight * scale * FontSizeFraction, MinimumFontSize, MaximumFontSize);
        }
        else
        {
            fontSize = presentation.FontSize > 0.0
                ? (float)Math.Max(1.0, presentation.FontSize * height / ReferenceSurfaceHeight)
                : Math.Clamp(height * FontSizeFraction, MinimumFontSize, MaximumFontSize);
        }

        float margin = height * MarginFraction;
        float gap = fontSize * StackGapFraction;
        SubtitleArea safe = SafeAreaOf(presentation, scale, width, height);
        SubtitleArea output = OutputAreaOf(presentation, width, height);

        // How much of each band is taken, by the alignment's row: bottom, middle, top. Several events can be up at once —
        // the same speaker's two lines, or a sign beside dialogue — and they share a band far more often than not. Each is
        // moved away from the edge its alignment names by what is already there, so they stack instead of landing on the
        // very spot the one before them took. The bands are handed in, so the renderers sharing a layer take room from one
        // another rather than each starting at the edge.

        using CanvasTextFormat format = new()
        {
            FontFamily = string.IsNullOrEmpty(presentation.FontFamily) ? SubtitleFonts.DefaultFontFamily : presentation.FontFamily,
            FontSize = fontSize,
            FontStyle = presentation.FontStyle,
            FontWeight = presentation.FontWeight.Weight > 0 ? presentation.FontWeight : new FontWeight { Weight = DefaultFontWeight },
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        // Opacity is the whole drawing's, not each part's: a layer fades the words and their border together, and a cue that
        // overlaps another does not show through it because both were faded before they met.
        double opacity = Math.Clamp(presentation.Opacity ?? 1.0, 0.0, 1.0);
        using CanvasActiveLayer? faded = opacity < 1.0 ? session.CreateLayer((float)opacity) : null;

        foreach (SubtitleCue cue in cues)
        {
            if (cue.Image is { } image)
            {
                DrawImage(session, device, image, presentation, output, safe, scale);
                continue;
            }

            var text = AssSubtitleText.Parse(cue.Text);
            if (text.IsEmpty)
            {
                continue;
            }

            string body = string.Join("\n", text.Lines);

            int alignment = presentation.TextAlignment is { } chosen ? (int)chosen : text.Alignment;
            int row = RowOf(alignment);
            int column = ColumnOf(alignment);

            // The alignment is the line's, not the block's: the layout is given the width of its widest line, so a line
            // shorter than that is set against the side the column names instead of all of them sharing one edge.
            format.HorizontalAlignment = column switch
            {
                0 => CanvasHorizontalAlignment.Left,
                2 => CanvasHorizontalAlignment.Right,
                _ => CanvasHorizontalAlignment.Center,
            };

            float blockWidth = MeasureBlockWidth(device, body, format, width, height);
            using CanvasTextLayout layout = new(device, body, format, blockWidth, height);

            Vector2 origin = Anchor(layout, row, column, width, height, margin);

            // The bottom and middle bands grow upwards, the top band downwards: the first cue keeps the place its alignment
            // asked for, and the ones after it make room above or below rather than covering it.
            origin.Y += row == 2 ? taken[row] : -taken[row];

            // What the safe region costs is only what it takes to be inside it, and the band grows from where the cue ended
            // up rather than from where it would have gone, so cues of one band stay apart when the chrome moves them.
            Vector2 kept = KeepInsideArea(origin, layout.LayoutBounds.Width, layout.LayoutBounds.Height, safe);
            float moved = kept.Y - origin.Y;
            taken[row] += (row == 2 ? moved : -moved) + (float)layout.LayoutBounds.Height + gap;

            // The placement a chain asks for is that chain's own and moves nothing else: it is applied once the room this
            // cue takes has been counted, so panning one chain's words — including the part of the pan the edge cuts short —
            // leaves the words another chain stacks in the same band exactly where they were.
            origin = KeepInsideArea(kept + new Vector2((float)(presentation.OffsetX * scale), (float)(presentation.OffsetY * scale)), layout.LayoutBounds.Width, layout.LayoutBounds.Height, safe);

            CanvasGeometry? outline = presentation.OutlineWidth > 0.0 ? outlines.Get(device, body, format, blockWidth, height) : null;

            // The stroke runs down the middle of the glyph's edge, so half of it would be covered by the words. Twice the
            // width is drawn and the words fill over the inner half, which leaves the border growing outwards by exactly
            // the width asked for.
            float strokeWidth = (float)(2.0 * presentation.OutlineWidth * scale);

            if (presentation.ShadowEnabled)
            {
                // The shadow is the whole outlined shape again, moved away from the words: the border is part of what casts
                // it, so the shadow follows the silhouette the words are drawn with rather than the bare glyphs, which the
                // border would then cover. Its border and its words overlap, and a shadow colour that is not opaque would be
                // composed twice over that overlap and leave a darker rim, so both are drawn opaque into a layer carrying
                // the colour's own alpha: the shadow as a whole is then composed once.
                Color shadowColor = presentation.ShadowColor ?? DefaultShadowColor;
                using CanvasActiveLayer? shadowLayer = shadowColor.A < byte.MaxValue ? session.CreateLayer(shadowColor.A / 255.0f) : null;
                var ink = Color.FromArgb(byte.MaxValue, shadowColor.R, shadowColor.G, shadowColor.B);
                float offset = fontSize * (float)(presentation.ShadowDistance ?? DefaultShadowDistanceFraction);
                Vector2 shadowOrigin = origin + new Vector2(offset, offset);

                if (outline is not null)
                {
                    session.DrawGeometry(outline, shadowOrigin, ink, strokeWidth, OutlineStrokeStyle);
                }

                session.DrawTextLayout(layout, shadowOrigin, ink);
            }

            if (outline is not null)
            {
                session.DrawGeometry(outline, origin, presentation.OutlineColor ?? DefaultOutlineColor, strokeWidth, OutlineStrokeStyle);
            }

            session.DrawTextLayout(layout, origin, presentation.FontColor ?? DefaultTextColor);
        }
    }

    #endregion

    #region Bitmap

    private static void DrawImage(CanvasDrawingSession session, CanvasDevice device, SubtitleImage image, SubtitlePresentation presentation, SubtitleArea output, SubtitleArea safe, double scale)
    {
        ISubtitleImageBuffer buffer = image.Buffer;
        if (buffer.Width <= 0 || buffer.Height <= 0)
        {
            return;
        }

        byte[] pixels = new byte[buffer.Stride * buffer.Height];
        Marshal.Copy(buffer.Pixels, pixels, 0, pixels.Length);

        using var bitmap = CanvasBitmap.CreateFromBytes(
            device,
            pixels,
            buffer.Width,
            buffer.Height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            96.0f,
            CanvasAlphaMode.Premultiplied);

        double x = output.Left + (image.Left * output.Width) + (presentation.OffsetX * scale);
        double y = output.Top + (image.Top * output.Height) + (presentation.OffsetY * scale);
        double scaledWidth = image.Width * output.Width;
        double scaledHeight = image.Height * output.Height;

        Vector2 kept = KeepInsideArea(new Vector2((float)x, (float)y), scaledWidth, scaledHeight, safe);

        session.DrawImage(bitmap, new Rect(kept.X, kept.Y, scaledWidth, scaledHeight));
    }

    #endregion

    #region Outline

    private static readonly Color DefaultTextColor = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color DefaultOutlineColor = Color.FromArgb(255, 0, 0, 0);
    private static readonly Color DefaultShadowColor = Color.FromArgb(128, 0, 0, 0);
    private static readonly CanvasStrokeStyle OutlineStrokeStyle = new() { LineJoin = CanvasLineJoin.Round };

    private sealed partial class OutlineCache : IDisposable
    {
        private const int Limit = 64;

        private readonly Dictionary<OutlineKey, CanvasGeometry> _outlines = [];
        private CanvasDevice? _device;

        internal CanvasGeometry? Get(CanvasDevice device, string body, CanvasTextFormat format, float width, int height)
        {
            if (_device != device)
            {
                Clear();
                _device = device;
            }

            OutlineKey key = new(body, format.FontFamily, format.FontSize, (int)format.FontStyle, format.FontWeight.Weight, (int)format.HorizontalAlignment, width, height);
            if (_outlines.TryGetValue(key, out CanvasGeometry? cached))
            {
                return cached;
            }

            if (_outlines.Count >= Limit)
            {
                Clear();
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

        public void Dispose()
        {
            Clear();
        }

        private void Clear()
        {
            foreach (CanvasGeometry outline in _outlines.Values)
            {
                outline.Dispose();
            }

            _outlines.Clear();
        }

        /// <summary>
        /// What shapes one cue's outline: the words, the style they are drawn in at the size of the surface, and the side they
        /// are aligned to — which moves the glyphs inside the box and so moves the outline with them.
        /// </summary>
        private readonly record struct OutlineKey(string Text, string Family, float Size, int Style, ushort Weight, int Alignment, float Width, int Height);
    }

    #endregion

    #region Area calculation

    private static SubtitleArea SafeAreaOf(SubtitlePresentation presentation, double scale, int width, int height)
    {
        float left = (float)Math.Clamp(presentation.Insets.Left * scale, 0.0, width * MaximumInsetFraction);
        float top = (float)Math.Clamp(presentation.Insets.Top * scale, 0.0, height * MaximumInsetFraction);
        float right = (float)Math.Clamp(presentation.Insets.Right * scale, 0.0, width * MaximumInsetFraction);
        float bottom = (float)Math.Clamp(presentation.Insets.Bottom * scale, 0.0, height * MaximumInsetFraction);

        return new SubtitleArea(left, top, Math.Max(1.0f, width - left - right), Math.Max(1.0f, height - top - bottom));
    }

    /// <summary>
    /// The box a bitmap cue's own coordinates are read in: the picture's shape, fitted into the surface and sat on the
    /// surface's bottom edge. The cue is authored against the picture, so reading its fractions in the surface's box
    /// instead would scale it one axis at a time whenever the window has another shape than the picture, which is what
    /// stretching looks like. The frame of a picture that is not bound is empty, and then the surface is the only box
    /// there is.
    /// </summary>
    private static SubtitleArea OutputAreaOf(SubtitlePresentation presentation, int width, int height)
    {
        SubtitleFrame frame = presentation.Frame;
        double pictureWidth = frame.Width * width;
        double pictureHeight = frame.Height * height;
        if (!(pictureWidth > 0.0) || !(pictureHeight > 0.0) || !double.IsFinite(pictureWidth) || !double.IsFinite(pictureHeight))
        {
            return new SubtitleArea(0.0f, 0.0f, width, height);
        }

        double scale = Math.Min(width / pictureWidth, height / pictureHeight);
        double outputWidth = Math.Min(pictureWidth * scale, width);
        double outputHeight = Math.Min(pictureHeight * scale, height);
        return new SubtitleArea(
            (float)((width - outputWidth) / 2.0),
            (float)(height - outputHeight),
            (float)outputWidth,
            (float)outputHeight);
    }

    private static int RowOf(int alignment)
    {
        return (Math.Clamp(alignment, 1, 9) - 1) / 3;
    }

    private static int ColumnOf(int alignment)
    {
        return (Math.Clamp(alignment, 1, 9) - 1) % 3;
    }

    /// <summary>
    /// The width of the widest line of the block, which is the width the block is laid out in so that its other lines can be
    /// aligned within it. Measuring costs a layout of its own, and only the width is read from it.
    /// </summary>
    private static float MeasureBlockWidth(CanvasDevice device, string body, CanvasTextFormat format, int width, int height)
    {
        using CanvasTextLayout measured = new(device, body, format, width, height);
        return Math.Max(1.0f, (float)measured.LayoutBounds.Width);
    }

    private readonly record struct SubtitleArea(float Left, float Top, float Width, float Height);

    private static Vector2 Anchor(CanvasTextLayout layout, int row, int column, int width, int height, float margin)
    {
        float x = column switch
        {
            0 => margin,
            2 => width - (float)layout.LayoutBounds.Width - margin,
            _ => (width - (float)layout.LayoutBounds.Width) / 2.0f,
        };

        float y = row switch
        {
            0 => height - (float)layout.LayoutBounds.Height - margin,
            2 => margin,
            _ => (height - (float)layout.LayoutBounds.Height) / 2.0f,
        };

        return new Vector2(x, y);
    }

    /// <summary>
    /// Where a cue of the given size goes inside the safe region: moved no further than it takes to be within it, and
    /// centred across it when it is too wide to be made to fit. Pinning a cue that cannot fit against the left edge would
    /// put the whole of its overflow on the right, which draws a centred line as a left-aligned one.
    /// </summary>
    private static Vector2 KeepInsideArea(Vector2 origin, double width, double height, SubtitleArea safe)
    {
        double x = width > safe.Width
            ? safe.Left + ((safe.Width - width) / 2.0)
            : Math.Clamp(origin.X, (double)safe.Left, Math.Max(safe.Left, safe.Left + safe.Width - width));
        double maxY = Math.Max(safe.Top, safe.Top + safe.Height - height);
        return new Vector2((float)x, (float)Math.Clamp(origin.Y, (double)safe.Top, maxY));
    }

    #endregion
}
