// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

using SinePlayer.Common.Playback.Core;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Hosting;

namespace SinePlayer.Data.Models.Misc;

internal sealed class AudioSettingsModel : PresetSettingsModel<AudioSettingsModel, AudioSettingsModel.JsonModel>
{
    public string Decoder { get; set; } = PlaybackComponents.AUTO;
    public string Renderer { get; set; } = PlaybackComponents.AUTO;
    public double Sync { get; set; }
    public AudioSpeedAlgorithm StretchMethod { get; set; } = AudioSpeedAlgorithm.WSOLA;
    public Dictionary<ChannelLayout, Dictionary<string, int>> RemixLevels { get; set; } = [];
    public Dictionary<ChannelLayout, Dictionary<string, int>> DownmixLevels { get; set; } = [];

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
        MergeLevels(model.RemixLevels, RemixLevels);
        MergeLevels(model.DownmixLevels, DownmixLevels);
    }

    protected override JsonModel ToJsonModel()
    {
        return new()
        {
            Decoder = Decoder,
            Renderer = Renderer,
            Sync = Sync,
            StretchMethod = StretchMethod.ToString(),
            RemixLevels = ToJsonLevels(RemixLevels),
            DownmixLevels = ToJsonLevels(DownmixLevels),
        };
    }

    private static Dictionary<string, Dictionary<string, int?>?> ToJsonLevels(Dictionary<ChannelLayout, Dictionary<string, int>> levels)
    {
        Dictionary<string, Dictionary<string, int?>?> result = [];
        foreach (KeyValuePair<ChannelLayout, Dictionary<string, int>> arrangement in levels)
        {
            Dictionary<string, int?> groups = [];
            foreach (KeyValuePair<string, int> group in arrangement.Value)
            {
                groups[group.Key] = group.Value;
            }

            result[arrangement.Key.ToString()] = groups;
        }

        return result;
    }

    private static void MergeLevels(Dictionary<string, Dictionary<string, int?>?>? saved, Dictionary<ChannelLayout, Dictionary<string, int>> levels)
    {
        if (saved is null)
        {
            return;
        }

        foreach (KeyValuePair<string, Dictionary<string, int?>?> arrangement in saved)
        {
            if (arrangement.Value is not { } savedGroups || !TryParseLayout(arrangement.Key, out ChannelLayout layout))
            {
                continue;
            }

            Dictionary<string, int> groups = [];
            foreach (KeyValuePair<string, int?> group in savedGroups)
            {
                if (group.Value is int level)
                {
                    groups[group.Key] = level;
                }
            }

            if (groups.Count == 0)
            {
                continue;
            }

            if (!levels.TryGetValue(layout, out Dictionary<string, int>? existing))
            {
                existing = [];
                levels[layout] = existing;
            }

            foreach (KeyValuePair<string, int> group in groups)
            {
                existing[group.Key] = group.Value;
            }
        }
    }

    private static bool TryParseLayout(string value, out ChannelLayout layout)
    {
        return Enum.TryParse(value, ignoreCase: true, out layout) && Enum.IsDefined(layout);
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

        [JsonPropertyName("RemixLevels")]
        public Dictionary<string, Dictionary<string, int?>?>? RemixLevels { get; set; }

        [JsonPropertyName("DownmixLevels")]
        public Dictionary<string, Dictionary<string, int?>?>? DownmixLevels { get; set; }
    }
}
