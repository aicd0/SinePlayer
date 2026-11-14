// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

using SinePlayer.Common.Playback.Core;
using SinePlayer.Core.Playback.Abstractions.Render;

using Windows.UI;
using Windows.UI.Text;

namespace SinePlayer.Data.Models.Misc;

internal sealed class SubtitleSettingsModel : PresetSettingsModel<SubtitleSettingsModel, SubtitleSettingsModel.JsonModel>
{
    public string Decoder { get; set; } = PlaybackComponents.AUTO;
    public string Renderer { get; set; } = PlaybackComponents.AUTO;
    public double Sync { get; set; }
    public int Order { get; set; }
    public int PanX { get; set; }
    public int PanY { get; set; }
    public string Family { get; set; } = string.Empty;
    public double Size { get; set; }
    public string Style { get; set; } = nameof(FontStyle.Normal);
    public int Weight { get; set; }
    public Color FontColor { get; set; } = Color.FromArgb(255, 255, 255, 255);
    public double OutlineWidth { get; set; } = 2.0;
    public Color OutlineColor { get; set; } = Color.FromArgb(255, 0, 0, 0);
    public Color ShadowColor { get; set; } = Color.FromArgb(128, 0, 0, 0);
    public double ShadowDistance { get; set; } = 0.06;
    public bool ShadowEnabled { get; set; } = true;
    public int Opacity { get; set; } = 100;

    public SubtitlePresentation ToPresentation()
    {
        return new()
        {
            Order = Order,
            OffsetX = PanX,
            OffsetY = PanY,
            FontFamily = Family,
            FontSize = Size,
            FontStyle = ToFontStyle(),
            FontWeight = ToFontWeight(),
            FontColor = FontColor,
            OutlineWidth = OutlineWidth,
            OutlineColor = OutlineColor,
            ShadowEnabled = ShadowEnabled,
            ShadowColor = ShadowColor,
            ShadowDistance = ShadowDistance,
            Opacity = Opacity / 100.0,
        };
    }

    public FontStyle ToFontStyle()
    {
        return Enum.TryParse(Style, ignoreCase: true, out FontStyle style) && Enum.IsDefined(style) ? style : FontStyle.Normal;
    }

    public FontWeight ToFontWeight()
    {
        return Weight > 0 ? new FontWeight { Weight = (ushort)Weight } : default;
    }

    protected override Dictionary<string, SubtitleSettingsModel> LoadPresets()
    {
        return AppSettingsModel.SubtitleSettingPresets;
    }

    protected override void SavePresets(Dictionary<string, SubtitleSettingsModel> presets)
    {
        AppSettingsModel.SubtitleSettingPresets = presets;
    }

    protected override string LoadDefaultPresetKey()
    {
        return AppSettingsModel.DefaultSubtitleSettingPresetKey;
    }

    protected override void ApplyJsonModel(JsonModel model)
    {
        Decoder = model.Decoder ?? Decoder;
        Renderer = model.Renderer ?? Renderer;
        Sync = model.Sync ?? Sync;
        Order = model.Order ?? Order;
        PanX = model.PanX ?? PanX;
        PanY = model.PanY ?? PanY;
        Family = model.FontFamily ?? Family;
        Size = model.FontSize ?? Size;
        Style = model.FontStyle ?? Style;
        Weight = model.FontWeight ?? Weight;
        FontColor = model.FontColor is { } value ? FromColorValue(value) : FontColor;
        OutlineWidth = model.OutlineWidth ?? OutlineWidth;
        OutlineColor = model.OutlineColor is { } outlineColor ? FromColorValue(outlineColor) : OutlineColor;
        ShadowColor = model.ShadowColor is { } shadowColor ? FromColorValue(shadowColor) : ShadowColor;
        ShadowDistance = model.ShadowDistance ?? ShadowDistance;
        ShadowEnabled = model.ShadowEnabled ?? ShadowEnabled;
        Opacity = model.Opacity ?? Opacity;
    }

    protected override JsonModel ToJsonModel()
    {
        return new()
        {
            Decoder = Decoder,
            Renderer = Renderer,
            Sync = Sync,
            Order = Order,
            PanX = PanX,
            PanY = PanY,
            FontFamily = Family,
            FontSize = Size,
            FontStyle = Style,
            FontWeight = Weight,
            FontColor = ToColorValue(FontColor),
            OutlineWidth = OutlineWidth,
            OutlineColor = ToColorValue(OutlineColor),
            ShadowColor = ToColorValue(ShadowColor),
            ShadowDistance = ShadowDistance,
            ShadowEnabled = ShadowEnabled,
            Opacity = Opacity,
        };
    }

    private static Color FromColorValue(uint value)
    {
        return Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    private static uint ToColorValue(Color color)
    {
        return ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
    }

    public class JsonModel
    {
        [JsonPropertyName("Decoder")]
        public string? Decoder { get; set; }

        [JsonPropertyName("Renderer")]
        public string? Renderer { get; set; }

        [JsonPropertyName("Sync")]
        public double? Sync { get; set; }

        [JsonPropertyName("Order")]
        public int? Order { get; set; }

        [JsonPropertyName("PanX")]
        public int? PanX { get; set; }

        [JsonPropertyName("PanY")]
        public int? PanY { get; set; }

        [JsonPropertyName("FontFamily")]
        public string? FontFamily { get; set; }

        [JsonPropertyName("FontSize")]
        public double? FontSize { get; set; }

        [JsonPropertyName("FontStyle")]
        public string? FontStyle { get; set; }

        [JsonPropertyName("FontWeight")]
        public int? FontWeight { get; set; }

        [JsonPropertyName("FontColor")]
        public uint? FontColor { get; set; }

        [JsonPropertyName("OutlineWidth")]
        public double? OutlineWidth { get; set; }

        [JsonPropertyName("OutlineColor")]
        public uint? OutlineColor { get; set; }

        [JsonPropertyName("ShadowColor")]
        public uint? ShadowColor { get; set; }

        [JsonPropertyName("ShadowDistance")]
        public double? ShadowDistance { get; set; }

        [JsonPropertyName("ShadowEnabled")]
        public bool? ShadowEnabled { get; set; }

        [JsonPropertyName("Opacity")]
        public int? Opacity { get; set; }
    }
}
