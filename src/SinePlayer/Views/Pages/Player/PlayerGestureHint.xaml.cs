// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Globalization;

using Microsoft.UI.Xaml;

using SinePlayer.Common.BaseUI;
using SinePlayer.Common.Utils;

namespace SinePlayer.Views.Pages.Player;

/// <summary>What a drag is doing, read out over the picture while it is going.</summary>
internal sealed partial class PlayerGestureHint : BaseUserControl
{
    private const string BrightnessGlyph = "\uE706";
    private const string MutedGlyph = "\uE74F";
    private const string SpeedGlyph = "\uEB9D";
    private const string ZoomGlyph = "\uE71E";

    public PlayerGestureHint()
    {
        InitializeComponent();
    }

    public void ShowVolume(int volume, bool isMuted)
    {
        EffectIcon.Glyph = isMuted ? MutedGlyph : volume switch
        {
            <= 0 => "\uE992",
            <= 33 => "\uE993",
            <= 66 => "\uE994",
            _ => "\uE995",
        };
        EffectIcon.Visibility = Visibility.Visible;
        EffectText.Text = $"{volume}%";
        Visibility = Visibility.Visible;
    }

    public void ShowBrightness(int level)
    {
        EffectIcon.Glyph = BrightnessGlyph;
        EffectIcon.Visibility = Visibility.Visible;
        EffectText.Text = $"{level}%";
        Visibility = Visibility.Visible;
    }

    public void ShowSeek(long positionMs, long offsetMs)
    {
        EffectIcon.Visibility = Visibility.Collapsed;
        EffectText.Text = $"{StringUtils.FormatTime(positionMs)} ({FormatOffset(offsetMs)})";
        Visibility = Visibility.Visible;
    }

    public void ShowSpeed(double rate)
    {
        EffectIcon.Glyph = SpeedGlyph;
        EffectIcon.Visibility = Visibility.Visible;
        EffectText.Text = $"{rate.ToString("0.0", CultureInfo.InvariantCulture)}x";
        Visibility = Visibility.Visible;
    }

    public void ShowZoom(double zoom)
    {
        EffectIcon.Glyph = ZoomGlyph;
        EffectIcon.Visibility = Visibility.Visible;
        EffectText.Text = $"{zoom.ToString("0.##", CultureInfo.InvariantCulture)}x";
        Visibility = Visibility.Visible;
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
    }

    private static string FormatOffset(long offsetMs)
    {
        string sign = offsetMs < 0 ? "-" : "+";
        double seconds = Math.Abs(offsetMs) / 1000.0;
        return $"{sign}{seconds.ToString("0.0", CultureInfo.InvariantCulture)}s";
    }
}
