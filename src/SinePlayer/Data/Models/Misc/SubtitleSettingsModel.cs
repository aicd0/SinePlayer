// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

using SinePlayer.Common.Playback.Core;
using SinePlayer.Core.Playback.Abstractions.Render;

using Windows.UI.Text;

namespace SinePlayer.Data.Models.Misc;

internal sealed class SubtitleSettingsModel : PresetSettingsModel<SubtitleSettingsModel, SubtitleSettingsModel.JsonModel>
{
    public string Decoder { get; set; } = PlaybackComponents.AUTO;
    public string Renderer { get; set; } = PlaybackComponents.AUTO;
    public int PanX { get; set; }
    public int PanY { get; set; }
    public string Family { get; set; } = string.Empty;
    public double Size { get; set; }
    public string Style { get; set; } = nameof(FontStyle.Normal);
    public int Weight { get; set; }
    public double OutlineWidth { get; set; } = 2.0;

    public SubtitlePresentation ToPresentation()
    {
        return new(
            PanX,
            PanY,
            FontFamily: Family,
            FontSize: Size,
            FontStyle: ToFontStyle(),
            FontWeight: ToFontWeight(),
            OutlineWidth: OutlineWidth);
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
        PanX = model.PanX ?? PanX;
        PanY = model.PanY ?? PanY;
        Family = model.FontFamily ?? Family;
        Size = model.FontSize ?? Size;
        Style = model.FontStyle ?? Style;
        Weight = model.FontWeight ?? Weight;
        OutlineWidth = model.OutlineWidth ?? OutlineWidth;
    }

    protected override JsonModel ToJsonModel()
    {
        return new()
        {
            Decoder = Decoder,
            Renderer = Renderer,
            PanX = PanX,
            PanY = PanY,
            FontFamily = Family,
            FontSize = Size,
            FontStyle = Style,
            FontWeight = Weight,
            OutlineWidth = OutlineWidth,
        };
    }

    public class JsonModel
    {
        [JsonPropertyName("Decoder")]
        public string? Decoder { get; set; }

        [JsonPropertyName("Renderer")]
        public string? Renderer { get; set; }

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

        [JsonPropertyName("OutlineWidth")]
        public double? OutlineWidth { get; set; }
    }
}
