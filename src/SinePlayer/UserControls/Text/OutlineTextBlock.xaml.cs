// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using Windows.UI;

namespace SinePlayer.UserControls.Text;

/// <summary>
/// A text block whose words can be outlined. <see cref="TextBlock"/> is sealed, so this is a control around one: the
/// words, and the text formatting this control already carries as a <see cref="Control"/> (family, size, weight, style
/// and foreground), are copied onto that block, and an outline is drawn with them.
/// <see cref="TextOutline"/> tells how the outline is drawn.
/// </summary>
internal sealed partial class OutlineTextBlock : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(OutlineTextBlock), new PropertyMetadata(string.Empty, OnMirroredPropertyChanged));
    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(nameof(TextWrapping), typeof(TextWrapping), typeof(OutlineTextBlock), new PropertyMetadata(TextWrapping.NoWrap, OnMirroredPropertyChanged));
    public static readonly DependencyProperty OutlineColorProperty = DependencyProperty.Register(nameof(OutlineColor), typeof(Color), typeof(OutlineTextBlock), new PropertyMetadata(Color.FromArgb(255, 0, 0, 0), OnOutlinePropertyChanged));
    public static readonly DependencyProperty OutlineWidthProperty = DependencyProperty.Register(nameof(OutlineWidth), typeof(double), typeof(OutlineTextBlock), new PropertyMetadata(0.0, OnOutlinePropertyChanged));

    private readonly TextOutline _outline;

    public OutlineTextBlock()
    {
        InitializeComponent();

        // The outline lives as long as the block it draws. It is created once and never handed back, so a load and an
        // unload that arrive out of turn cannot leave the words with a dead outline and no way to get another.
        _outline = new TextOutline(InnerText);

        UpdateInnerText();

        RegisterPropertyChangedCallback(FontFamilyProperty, OnFormattingPropertyChanged);
        RegisterPropertyChangedCallback(FontSizeProperty, OnFormattingPropertyChanged);
        RegisterPropertyChangedCallback(FontWeightProperty, OnFormattingPropertyChanged);
        RegisterPropertyChangedCallback(FontStyleProperty, OnFormattingPropertyChanged);
        RegisterPropertyChangedCallback(ForegroundProperty, OnFormattingPropertyChanged);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Whether the words wrap in the width they are given.</summary>
    public TextWrapping TextWrapping
    {
        get => (TextWrapping)GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    /// <summary>The colour of the border around the words.</summary>
    public Color OutlineColor
    {
        get => (Color)GetValue(OutlineColorProperty);
        set => SetValue(OutlineColorProperty, value);
    }

    /// <summary>
    /// How far the border reaches outside the words. Zero, the default, leaves the words to the block and draws exactly
    /// what a <see cref="TextBlock"/> would.
    /// </summary>
    public double OutlineWidth
    {
        get => (double)GetValue(OutlineWidthProperty);
        set => SetValue(OutlineWidthProperty, value);
    }

    private void UpdateInnerText()
    {
        InnerText.Text = Text ?? string.Empty;
        InnerText.TextWrapping = TextWrapping;
        InnerText.FontSize = FontSize;
        InnerText.FontWeight = FontWeight;
        InnerText.FontStyle = FontStyle;

        // A family or a brush that was never set is left for the block to take from the theme.
        if (FontFamily is not null)
        {
            InnerText.FontFamily = FontFamily;
        }
        if (Foreground is not null)
        {
            InnerText.Foreground = Foreground;
        }
    }

    private static void OnMirroredPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        ((OutlineTextBlock)sender).UpdateInnerText();
    }

    private static void OnFormattingPropertyChanged(DependencyObject sender, DependencyProperty dp)
    {
        ((OutlineTextBlock)sender).UpdateInnerText();
    }

    private static void OnOutlinePropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var control = (OutlineTextBlock)sender;
        control._outline.OutlineColor = control.OutlineColor;
        control._outline.OutlineWidth = control.OutlineWidth;
    }
}
