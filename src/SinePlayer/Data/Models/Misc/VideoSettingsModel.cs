// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

using SinePlayer.Common.Playback.Core;
using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Data.Models.Misc;

internal sealed class VideoSettingsModel : PresetSettingsModel<VideoSettingsModel, VideoSettingsModel.JsonModel>
{
    public const double MinimumZoom = 0.1;
    public const double MaximumZoom = 8.0;

    public string Decoder { get; set; } = PlaybackComponents.AUTO;
    public string Renderer { get; set; } = PlaybackComponents.AUTO;
    public double Sync { get; set; }
    public bool PreferHardwareDecoding { get; set; } = true;
    public bool PreferHdr { get; set; } = true;
    public VideoResizeMode ResizeMode { get; set; } = VideoResizeMode.Fit;
    public VideoScalingMode ScalingMode { get; set; } = VideoScalingMode.Bilinear;
    public int RotationDegrees { get; set; } = 0;
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }
    public double Zoom { get; set; } = 1.0;
    public double HorizontalStretch { get; set; }
    public double VerticalStretch { get; set; }
    public double AspectRatio { get; set; }
    public double PanX { get; set; }
    public double PanY { get; set; }

    public VideoTransform ToTransform()
    {
        int quarterTurns = (int)Math.Round(RotationDegrees / 90.0);
        double zoom = double.IsFinite(Zoom) && Zoom > 0.0 ? Zoom : 1.0;
        double offsetX = double.IsFinite(HorizontalStretch) ? HorizontalStretch : 0.0;
        double offsetY = double.IsFinite(VerticalStretch) ? VerticalStretch : 0.0;
        double aspectRatio = double.IsFinite(AspectRatio) && AspectRatio > 0.0 ? AspectRatio : 0.0;
        double panX = double.IsFinite(PanX) ? PanX : 0.0;
        double panY = double.IsFinite(PanY) ? PanY : 0.0;

        return new VideoTransform
        {
            Orientation = VideoOrientation.FromDegrees(quarterTurns * 90, FlipHorizontal, FlipVertical),
            ResizeMode = ResizeMode,
            Zoom = zoom,
            HorizontalStretch = offsetX,
            VerticalStretch = offsetY,
            PanX = panX,
            PanY = panY,
            Scaling = ScalingMode,
            TargetAspectRatio = aspectRatio,
        };
    }

    protected override Dictionary<string, VideoSettingsModel> LoadPresets()
    {
        return AppSettingsModel.VideoSettingPresets;
    }

    protected override void SavePresets(Dictionary<string, VideoSettingsModel> presets)
    {
        AppSettingsModel.VideoSettingPresets = presets;
    }

    protected override string LoadDefaultPresetKey()
    {
        return AppSettingsModel.DefaultVideoSettingPresetKey;
    }

    protected override void ApplyJsonModel(JsonModel model)
    {
        Decoder = model.Decoder ?? Decoder;
        Renderer = model.Renderer ?? Renderer;
        Sync = model.Sync ?? Sync;
        PreferHardwareDecoding = model.PreferHardwareDecoding ?? PreferHardwareDecoding;
        PreferHdr = model.PreferHdr ?? PreferHdr;
        ResizeMode = ParseResizeMode(model.ResizeMode, ResizeMode);
        ScalingMode = ParseScalingMode(model.ScalingMode, ScalingMode);
        RotationDegrees = ParseRotationDegrees(model.RotationDegrees, RotationDegrees);
        FlipHorizontal = model.FlipHorizontal ?? FlipHorizontal;
        FlipVertical = model.FlipVertical ?? FlipVertical;
        Zoom = model.Zoom ?? Zoom;
        HorizontalStretch = model.HorizontalStretch ?? HorizontalStretch;
        VerticalStretch = model.VerticalStretch ?? VerticalStretch;
        PanX = model.PanX ?? PanX;
        PanY = model.PanY ?? PanY;

        if (model.AspectRatio is { } aspectRatio && double.IsFinite(aspectRatio))
        {
            AspectRatio = aspectRatio > 0.0 ? aspectRatio : 0.0;
        }
    }

    protected override JsonModel ToJsonModel()
    {
        return new()
        {
            Decoder = Decoder,
            Renderer = Renderer,
            Sync = Sync,
            PreferHardwareDecoding = PreferHardwareDecoding,
            PreferHdr = PreferHdr,
            ResizeMode = ResizeMode.ToString(),
            ScalingMode = ScalingMode.ToString(),
            RotationDegrees = RotationDegrees,
            FlipHorizontal = FlipHorizontal,
            FlipVertical = FlipVertical,
            Zoom = Zoom,
            HorizontalStretch = HorizontalStretch,
            VerticalStretch = VerticalStretch,
            AspectRatio = AspectRatio,
            PanX = PanX,
            PanY = PanY,
        };
    }

    private static VideoResizeMode ParseResizeMode(string? value, VideoResizeMode fallback)
    {
        if (!string.IsNullOrEmpty(value) && Enum.TryParse(value, ignoreCase: true, out VideoResizeMode mode) && Enum.IsDefined(mode))
        {
            return mode;
        }

        return fallback;
    }

    private static VideoScalingMode ParseScalingMode(string? value, VideoScalingMode fallback)
    {
        if (!string.IsNullOrEmpty(value) && Enum.TryParse(value, ignoreCase: true, out VideoScalingMode mode) && Enum.IsDefined(mode))
        {
            return mode;
        }

        return fallback;
    }

    private static int ParseRotationDegrees(int? value, int fallback)
    {
        return value is 0 or 90 or 180 or 270 ? value.Value : fallback;
    }

    public class JsonModel
    {
        [JsonPropertyName("Decoder")]
        public string? Decoder { get; set; }

        [JsonPropertyName("Renderer")]
        public string? Renderer { get; set; }

        [JsonPropertyName("Sync")]
        public double? Sync { get; set; }

        [JsonPropertyName("PreferHardwareDecoding")]
        public bool? PreferHardwareDecoding { get; set; }

        [JsonPropertyName("PreferHdr")]
        public bool? PreferHdr { get; set; }

        [JsonPropertyName("ResizeMode")]
        public string? ResizeMode { get; set; }

        [JsonPropertyName("ScalingMode")]
        public string? ScalingMode { get; set; }

        [JsonPropertyName("RotationDegrees")]
        public int? RotationDegrees { get; set; }

        [JsonPropertyName("FlipHorizontal")]
        public bool? FlipHorizontal { get; set; }

        [JsonPropertyName("FlipVertical")]
        public bool? FlipVertical { get; set; }

        [JsonPropertyName("Zoom")]
        public double? Zoom { get; set; }

        [JsonPropertyName("HorizontalStretch")]
        public double? HorizontalStretch { get; set; }

        [JsonPropertyName("VerticalStretch")]
        public double? VerticalStretch { get; set; }

        [JsonPropertyName("AspectRatio")]
        public double? AspectRatio { get; set; }

        [JsonPropertyName("PanX")]
        public double? PanX { get; set; }

        [JsonPropertyName("PanY")]
        public double? PanY { get; set; }
    }
}
