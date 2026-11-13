// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

using SinePlayer.Common.Playback.Core;

namespace SinePlayer.Data.Models.Misc;

internal sealed class PlayerPresetSettingsModel : PresetSettingsModel<PlayerPresetSettingsModel, PlayerPresetSettingsModel.JsonModel>
{
    public const double DefaultSpeed = 1.0;

    public double Speed { get; set; } = DefaultSpeed;

    protected override Dictionary<string, PlayerPresetSettingsModel> LoadPresets()
    {
        return AppSettingsModel.PlayerSettingPresets;
    }

    protected override void SavePresets(Dictionary<string, PlayerPresetSettingsModel> presets)
    {
        AppSettingsModel.PlayerSettingPresets = presets;
    }

    protected override string LoadDefaultPresetKey()
    {
        return AppSettingsModel.DefaultPlayerSettingPresetKey;
    }

    protected override void ApplyJsonModel(JsonModel model)
    {
        if (model.Speed is { } speed && double.IsFinite(speed))
        {
            Speed = Math.Clamp(speed, PlayerSession.MinimumPlaybackRate, PlayerSession.MaximumPlaybackRate);
        }
    }

    protected override JsonModel ToJsonModel()
    {
        return new()
        {
            Speed = Speed,
        };
    }

    public class JsonModel
    {
        [JsonPropertyName("Speed")]
        public double? Speed { get; set; }
    }
}
