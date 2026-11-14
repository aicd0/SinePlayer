// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

using SinePlayer.Common.Playback.Core;
using SinePlayer.Core.Playback.Hosting;

namespace SinePlayer.Data.Models.Misc;

internal sealed class AudioSettingsModel : PresetSettingsModel<AudioSettingsModel, AudioSettingsModel.JsonModel>
{
    public string Decoder { get; set; } = PlaybackComponents.AUTO;
    public string Renderer { get; set; } = PlaybackComponents.AUTO;
    public double Sync { get; set; }
    public AudioSpeedAlgorithm StretchMethod { get; set; } = AudioSpeedAlgorithm.WSOLA;
    public Dictionary<string, int> MixLevels { get; set; } = [];

    protected override Dictionary<string, AudioSettingsModel> LoadPresets()
    {
        return AppSettingsModel.AudioSettingPresets;
    }

    protected override void SavePresets(Dictionary<string, AudioSettingsModel> presets)
    {
        AppSettingsModel.AudioSettingPresets = presets;
    }

    protected override string LoadDefaultPresetKey()
    {
        return AppSettingsModel.DefaultAudioSettingPresetKey;
    }

    protected override void ApplyJsonModel(JsonModel model)
    {
        Decoder = model.Decoder ?? Decoder;
        Renderer = model.Renderer ?? Renderer;
        Sync = model.Sync ?? Sync;
        StretchMethod = ParseStretchMethod(model.StretchMethod, StretchMethod);

        if (model.MixLevels is { } levels)
        {
            foreach (KeyValuePair<string, int?> entry in levels)
            {
                if (entry.Value is int level)
                {
                    MixLevels[entry.Key] = level;
                }
            }
        }
    }

    protected override JsonModel ToJsonModel()
    {
        Dictionary<string, int?> levels = [];
        foreach (KeyValuePair<string, int> entry in MixLevels)
        {
            levels[entry.Key] = entry.Value;
        }

        return new()
        {
            Decoder = Decoder,
            Renderer = Renderer,
            Sync = Sync,
            StretchMethod = StretchMethod.ToString(),
            MixLevels = levels,
        };
    }

    private static AudioSpeedAlgorithm ParseStretchMethod(string? value, AudioSpeedAlgorithm fallback)
    {
        if (!string.IsNullOrEmpty(value) && Enum.TryParse(value, ignoreCase: true, out AudioSpeedAlgorithm method) && Enum.IsDefined(method))
        {
            return method;
        }

        return fallback;
    }

    public class JsonModel
    {
        [JsonPropertyName("Decoder")]
        public string? Decoder { get; set; }

        [JsonPropertyName("Renderer")]
        public string? Renderer { get; set; }

        [JsonPropertyName("Sync")]
        public double? Sync { get; set; }

        [JsonPropertyName("StretchMethod")]
        public string? StretchMethod { get; set; }

        [JsonPropertyName("MixLevels")]
        public Dictionary<string, int?>? MixLevels { get; set; }
    }
}
