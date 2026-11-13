// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Text.Json.Serialization;

using SinePlayer.Common.Playback.Core;

using Windows.UI.Text;

namespace SinePlayer.Data.Models.Misc;

internal sealed class SubtitleSettingsModel
{
    public static SubtitleSettingsModel FromDefault()
    {
        return new();
    }

    public static SubtitleSettingsModel FromJsonModel(JsonModel? model, SubtitleSettingsModel fallback)
    {
        if (model is null)
        {
            return fallback.Clone();
        }

        return new()
        {
            Decoder = model.Decoder ?? fallback.Decoder,
            Renderer = model.Renderer ?? fallback.Renderer,
            PanX = model.PanX ?? fallback.PanX,
            PanY = model.PanY ?? fallback.PanY,
            Family = model.FontFamily ?? fallback.Family,
            Size = model.FontSize ?? fallback.Size,
            Style = model.FontStyle ?? fallback.Style,
            Weight = model.FontWeight ?? fallback.Weight,
            OutlineWidth = model.OutlineWidth ?? fallback.OutlineWidth,
        };
    }

    public string Decoder { get; set; } = PlaybackComponents.AUTO;
    public string Renderer { get; set; } = PlaybackComponents.AUTO;
    public int PanX { get; set; }
    public int PanY { get; set; }
    public string Family { get; set; } = string.Empty;
    public double Size { get; set; }
    public string Style { get; set; } = nameof(FontStyle.Normal);
    public int Weight { get; set; }
    public double OutlineWidth { get; set; } = 2.0;

    public SubtitleSettingsModel Clone()
    {
        return (SubtitleSettingsModel)MemberwiseClone();
    }

    public JsonModel ToJsonModel()
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

    public FontStyle ToFontStyle()
    {
        return Enum.TryParse(Style, ignoreCase: true, out FontStyle style) && Enum.IsDefined(style) ? style : FontStyle.Normal;
    }

    public FontWeight ToFontWeight()
    {
        return Weight > 0 ? new FontWeight { Weight = (ushort)Weight } : default;
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
