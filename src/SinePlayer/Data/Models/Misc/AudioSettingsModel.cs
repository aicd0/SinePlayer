// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;
using System.Text.Json.Serialization;

using SinePlayer.Common.Playback.Core;

namespace SinePlayer.Data.Models.Misc;

internal sealed class AudioSettingsModel : PresetSettingsModel<AudioSettingsModel, AudioSettingsModel.JsonModel>
{
    public string Decoder { get; set; } = PlaybackComponents.AUTO;
    public string Renderer { get; set; } = PlaybackComponents.AUTO;

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
    }

    protected override JsonModel ToJsonModel()
    {
        return new()
        {
            Decoder = Decoder,
            Renderer = Renderer,
        };
    }

    public class JsonModel
    {
        [JsonPropertyName("Decoder")]
        public string? Decoder { get; set; }

        [JsonPropertyName("Renderer")]
        public string? Renderer { get; set; }
    }
}
