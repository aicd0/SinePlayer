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
    public string Decoder { get; set; } = PlaybackComponents.AUTO;
    public string Renderer { get; set; } = PlaybackComponents.AUTO;
    public bool PreferHardwareDecoding { get; set; } = true;
    public bool PreferHdr { get; set; } = true;
    public VideoResizeMode ResizeMode { get; set; } = VideoResizeMode.Fit;
    public VideoScalingMode ScalingMode { get; set; } = VideoScalingMode.Bilinear;
    public int RotationDegrees { get; set; } = 0;
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }

    public VideoTransform ToTransform()
    {
        int quarterTurns = (int)Math.Round(RotationDegrees / 90.0);

        return new VideoTransform(
            VideoOrientation.FromDegrees(quarterTurns * 90, FlipHorizontal, FlipVertical),
            ResizeMode,
            1.0,
            0.0,
            0.0,
            ScalingMode);
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
        PreferHardwareDecoding = model.PreferHardwareDecoding ?? PreferHardwareDecoding;
        PreferHdr = model.PreferHdr ?? PreferHdr;
        ResizeMode = ParseResizeMode(model.ResizeMode, ResizeMode);
        ScalingMode = ParseScalingMode(model.ScalingMode, ScalingMode);
        RotationDegrees = ParseRotationDegrees(model.RotationDegrees, RotationDegrees);
        FlipHorizontal = model.FlipHorizontal ?? FlipHorizontal;
        FlipVertical = model.FlipVertical ?? FlipVertical;
    }

    protected override JsonModel ToJsonModel()
    {
        return new()
        {
            Decoder = Decoder,
            Renderer = Renderer,
            PreferHardwareDecoding = PreferHardwareDecoding,
            PreferHdr = PreferHdr,
            ResizeMode = ResizeMode.ToString(),
            ScalingMode = ScalingMode.ToString(),
            RotationDegrees = RotationDegrees,
            FlipHorizontal = FlipHorizontal,
            FlipVertical = FlipVertical,
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
    }
}
