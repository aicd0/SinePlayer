// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Numerics;

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

using SinePlayer.Core.Common.DebugTools;

using Windows.Foundation;
using Windows.UI;

namespace SinePlayer.UserControls.Text;

/// <summary>
/// Draws the words of a <see cref="TextBlock"/> with an outline around them. WinUI has no outlined text, so the words
/// are turned into glyph geometry, stroked in the outline colour and filled in the block's own foreground, and the
/// compositor draws that in place of the block's text: the foreground is taken over while the outline lives and handed
/// back when it is disposed. The fill is the foreground colour read at that moment, so a foreground that is not a solid
/// colour cannot be outlined and leaves the block as it was. No outline width means no outline: nothing is drawn and
/// the block keeps its words to itself, as a plain <see cref="TextBlock"/> would.
/// </summary>
internal sealed class TextOutline : IDisposable
{
    private const string TAG = nameof(TextOutline);

    private const double DefaultOutlineWidth = 0.0;

    // The width a layout is measured with absorbs the rounding of the width the block was measured with, so a line that
    // fitted in the block still fits here and the line breaks stay the block's own.
    private const double WrapSlack = 0.5;

    private static readonly Color TransparentColor = Color.FromArgb(0, 0, 0, 0);
    private static readonly Color DefaultOutlineColor = Color.FromArgb(255, 0, 0, 0);

    private readonly TextBlock _textBlock;
    private readonly SolidColorBrush _hiddenForeground = new(TransparentColor);
    private readonly long _textToken;
    private readonly long _foregroundToken;
    private readonly long _fontFamilyToken;
    private readonly long _fontSizeToken;
    private readonly long _fontWeightToken;
    private readonly long _fontStyleToken;

    private Compositor? _compositor;
    private ContainerVisual? _root;
    private ShapeVisual? _strokeVisual;
    private ShapeVisual? _fillVisual;
    private CompositionSpriteShape? _strokeShape;
    private CompositionSpriteShape? _fillShape;

    private Brush? _foreground;
    private Color _fillColor = Color.FromArgb(255, 255, 255, 255);
    private Color _outlineColor = DefaultOutlineColor;
    private double _outlineWidth = DefaultOutlineWidth;

    // What the geometry on screen was built from: the text changed, or the box it wrapped in, means a new outline.
    private string? _builtText;
    private double _builtWidth;
    private double _builtHeight;

    // Whether that geometry is on screen painting the words, as opposed to the block's own text being the paint.
    private bool _hasOutline;

    private bool _isTakingForeground;
    private bool _isDisposed;

    public TextOutline(TextBlock textBlock)
    {
        _textBlock = textBlock;

        if (textBlock.Foreground is not SolidColorBrush foreground)
        {
            Logger.W(TAG, "The text has no solid foreground, so it is left without an outline.");
            return;
        }

        _foreground = foreground;
        _fillColor = foreground.Color;

        _textBlock.Loaded += TextBlock_Loaded;
        _textBlock.LayoutUpdated += TextBlock_LayoutUpdated;

        _textToken = _textBlock.RegisterPropertyChangedCallback(TextBlock.TextProperty, TextBlock_PropertyChanged);
        _foregroundToken = _textBlock.RegisterPropertyChangedCallback(TextBlock.ForegroundProperty, TextBlock_PropertyChanged);
        _fontFamilyToken = _textBlock.RegisterPropertyChangedCallback(TextBlock.FontFamilyProperty, TextBlock_PropertyChanged);
        _fontSizeToken = _textBlock.RegisterPropertyChangedCallback(TextBlock.FontSizeProperty, TextBlock_PropertyChanged);
        _fontWeightToken = _textBlock.RegisterPropertyChangedCallback(TextBlock.FontWeightProperty, TextBlock_PropertyChanged);
        _fontStyleToken = _textBlock.RegisterPropertyChangedCallback(TextBlock.FontStyleProperty, TextBlock_PropertyChanged);

        // A block that is already in the tree — a control creates its outline once it loads — is drawn straight away;
        // one that is not is drawn by the Loaded handler.
        Refresh();
    }

    /// <summary>The colour of the border, which grows outwards from the words by <see cref="OutlineWidth"/>.</summary>
    public Color OutlineColor
    {
        get => _outlineColor;
        set
        {
            _outlineColor = value;
            if (_strokeShape is not null && _compositor is not null)
            {
                _strokeShape.StrokeBrush = _compositor.CreateColorBrush(value);
            }
        }
    }

    /// <summary>How far the border reaches outside the words. Zero, the default, is no outline at all.</summary>
    public double OutlineWidth
    {
        get => _outlineWidth;
        set
        {
            _outlineWidth = value > 0.0 ? value : 0.0;
            if (_strokeShape is not null)
            {
                // The stroke runs down the middle of the glyph's edge, so half of it would be covered by the words. Twice
                // the width is drawn and the fill is drawn over the inner half, leaving the border outside by the width
                // asked for.
                _strokeShape.StrokeThickness = (float)(_outlineWidth * 2.0);
            }

            // The border is part of what is built, and turning it off hands the words back to the block.
            _builtText = null;
            Refresh();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }
        _isDisposed = true;

        _textBlock.Loaded -= TextBlock_Loaded;
        _textBlock.LayoutUpdated -= TextBlock_LayoutUpdated;

        _textBlock.UnregisterPropertyChangedCallback(TextBlock.TextProperty, _textToken);
        _textBlock.UnregisterPropertyChangedCallback(TextBlock.ForegroundProperty, _foregroundToken);
        _textBlock.UnregisterPropertyChangedCallback(TextBlock.FontFamilyProperty, _fontFamilyToken);
        _textBlock.UnregisterPropertyChangedCallback(TextBlock.FontSizeProperty, _fontSizeToken);
        _textBlock.UnregisterPropertyChangedCallback(TextBlock.FontWeightProperty, _fontWeightToken);
        _textBlock.UnregisterPropertyChangedCallback(TextBlock.FontStyleProperty, _fontStyleToken);

        GiveBackForeground();
        if (_root is not null)
        {
            _root.Children.RemoveAll();
            _root.Opacity = 0.0f;
        }
    }

    private void Attach()
    {
        if (_root is not null || _isDisposed)
        {
            return;
        }

        Compositor compositor = ElementCompositionPreview.GetElementVisual(_textBlock).Compositor;
        _compositor = compositor;

        _strokeShape = compositor.CreateSpriteShape();
        _strokeShape.StrokeBrush = compositor.CreateColorBrush(_outlineColor);
        _strokeShape.StrokeLineJoin = CompositionStrokeLineJoin.Round;
        _strokeShape.StrokeThickness = (float)(_outlineWidth * 2.0);

        _fillShape = compositor.CreateSpriteShape();
        _fillShape.FillBrush = compositor.CreateColorBrush(_fillColor);

        // The fill has to land over the inner half of the stroke, which is what leaves the border outside the words by
        // the width asked for. A container draws its children in the order they were added, so the stroke goes in first
        // and the fill last, on top of it.
        _strokeVisual = compositor.CreateShapeVisual();
        _strokeVisual.Shapes.Add(_strokeShape);
        _fillVisual = compositor.CreateShapeVisual();
        _fillVisual.Shapes.Add(_fillShape);

        _root = compositor.CreateContainerVisual();
        _root.Children.InsertAtTop(_strokeVisual);
        _root.Children.InsertAtTop(_fillVisual);

        ElementCompositionPreview.SetElementChildVisual(_textBlock, _root);
    }

    /// <summary>
    /// A shape visual clips what it draws to its size, which starts as nothing at all: the visuals are given the whole
    /// of the glyphs plus the room the border takes outside them, and are moved out by the same amount the shapes are
    /// moved back in, so the words land exactly where the block would have drawn them.
    /// </summary>
    private void LayoutVisuals(Rect bounds, double width, double height)
    {
        if (_root is not { } root
            || _strokeVisual is not { } strokeVisual
            || _fillVisual is not { } fillVisual
            || _strokeShape is not { } strokeShape
            || _fillShape is not { } fillShape)
        {
            return;
        }

        float outline = (float)_outlineWidth;
        float left = (float)Math.Min(0.0, bounds.X) - outline;
        float top = (float)Math.Min(0.0, bounds.Y) - outline;
        float right = (float)Math.Max(width, bounds.X + bounds.Width) + outline;
        float bottom = (float)Math.Max(height, bounds.Y + bounds.Height) + outline;

        Vector2 size = new(right - left, bottom - top);
        Vector2 offset = new(-left, -top);

        root.Size = size;
        root.Offset = new Vector3(left, top, 0.0f);
        strokeVisual.Size = size;
        fillVisual.Size = size;
        strokeShape.Offset = offset;
        fillShape.Offset = offset;
    }

    private void Refresh()
    {
        if (_isDisposed || !_textBlock.IsLoaded)
        {
            return;
        }

        string text = _textBlock.Text;
        double width = _textBlock.ActualWidth;
        double height = _textBlock.ActualHeight;
        if (text == _builtText && width == _builtWidth && height == _builtHeight)
        {
            return;
        }
        _builtText = text;
        _builtWidth = width;
        _builtHeight = height;

        if (text.Length == 0 || width <= 0.0 || height <= 0.0 || _outlineWidth <= 0.0)
        {
            GiveBackForeground();
            return;
        }

        Attach();
        if (_strokeShape is null || _fillShape is null || _compositor is null)
        {
            return;
        }

        CanvasGeometry geometry;
        try
        {
            var device = CanvasDevice.GetSharedDevice();
            using CanvasTextFormat format = new()
            {
                FontSize = (float)_textBlock.FontSize,
                FontWeight = _textBlock.FontWeight,
                FontStyle = _textBlock.FontStyle,
                WordWrapping = CanvasWordWrapping.Wrap,
            };

            string family = FirstFontFamily(_textBlock.FontFamily);
            if (family.Length > 0)
            {
                format.FontFamily = family;
            }

            using CanvasTextLayout layout = new(device, text, format, (float)(width + WrapSlack), (float)height);
            geometry = CanvasGeometry.CreateText(layout);
        }
        catch (Exception ex)
        {
            Logger.E(TAG, "The outline of the text could not be built", ex);
            GiveBackForeground();
            return;
        }

        // Nothing to draw means nothing to hide the words for: a geometry without bounds would leave the block blank.
        Rect bounds = geometry.ComputeBounds();
        if (bounds.Width <= 0.0 || bounds.Height <= 0.0)
        {
            Logger.W(TAG, "The glyphs of the text came out empty, so the block keeps drawing its words.");
            geometry.Dispose();
            GiveBackForeground();
            return;
        }

        CompositionPathGeometry pathGeometry = _compositor.CreatePathGeometry(new CompositionPath(geometry));
        _strokeShape.Geometry = pathGeometry;
        _fillShape.Geometry = pathGeometry;
        LayoutVisuals(bounds, width, height);

        // The geometry is not kept to be disposed and never handed back: the composition path built from it holds it,
        // and closing a geometry the compositor still refers to is what crashes on teardown. It goes when the path does.
        _hasOutline = true;
        TakeForeground();
    }

    /// <summary>
    /// The block's family is a list of fallbacks, of which a text format takes one; a block that names none is left to
    /// the format's own.
    /// </summary>
    private static string FirstFontFamily(FontFamily fontFamily)
    {
        string source = fontFamily.Source;
        int comma = source.IndexOf(',');
        return (comma < 0 ? source : source.Substring(0, comma)).Trim();
    }

    private void TakeForeground()
    {
        if (_root is null || !_hasOutline)
        {
            return;
        }

        _isTakingForeground = true;
        _textBlock.Foreground = _hiddenForeground;
        _isTakingForeground = false;

        _root.Opacity = 1.0f;
    }

    private void GiveBackForeground()
    {
        _hasOutline = false;

        if (_root is null || _foreground is null)
        {
            return;
        }

        _root.Opacity = 0.0f;

        _isTakingForeground = true;
        _textBlock.Foreground = _foreground;
        _isTakingForeground = false;
    }

    private void UpdateFillBrush()
    {
        if (_fillShape is not null && _compositor is not null)
        {
            _fillShape.FillBrush = _compositor.CreateColorBrush(_fillColor);
        }
    }

    private void TextBlock_Loaded(object sender, RoutedEventArgs e)
    {
        Refresh();
    }

    private void TextBlock_LayoutUpdated(object? sender, object e)
    {
        Refresh();
    }

    private void TextBlock_PropertyChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (dp == TextBlock.ForegroundProperty)
        {
            if (_isTakingForeground)
            {
                return;
            }

            if (_textBlock.Foreground is SolidColorBrush foreground)
            {
                _foreground = foreground;
                _fillColor = foreground.Color;
                UpdateFillBrush();
            }
            else
            {
                Logger.W(TAG, "The text outline keeps the last solid foreground it was given.");
            }

            TakeForeground();
            return;
        }

        // The words and the font they are set in decide the glyphs: what was built no longer stands for them.
        _builtText = null;
    }
}
