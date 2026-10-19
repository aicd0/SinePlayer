// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

using SinePlayer.Common.Localization;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Media;

namespace SinePlayer.Data.Models.Misc;

internal class PlayerSettingsModel
{
    public const string PRESET_KEY_DEFAULT = "###default###";
    public const string PRESET_KEY_CUSTOM = "###custom###";
    private const string TAG = nameof(PlayerSettingsModel);

    // Actual player settings fields will be added here in the future

    public static PlayerSettingsModel FromDefault()
    {
        return new()
        {
            PresetKey = PRESET_KEY_DEFAULT,
            PresetName = StringResourceProvider.Instance.Default,
        };
    }

    public static PlayerSettingsModel FromJsonModel(string key, JsonModel? model, PlayerSettingsModel? fallbackModel = null)
    {
        fallbackModel ??= new();
        fallbackModel.PresetKey = key;

        if (model is null)
        {
            return fallbackModel;
        }

        return new()
        {
            PresetKey = key,
            PresetName = model.PresetName ?? fallbackModel.PresetName,
        };
    }

    public static PlayerSettingsModel LoadFromMedia(MediaModel media)
    {
        string defaultPresetKey = AppSettingsModel.DefaultPlayerSettingPresetKey;
        if (string.IsNullOrEmpty(defaultPresetKey))
        {
            defaultPresetKey = PRESET_KEY_DEFAULT;
        }

        string? presetKey = media.GetExt(MediaExt.PLAYER_SETTING_PRESET_KEY);
        if (string.IsNullOrEmpty(presetKey))
        {
            presetKey = defaultPresetKey;
        }

        PlayerSettingsModel? presetModel = null;
        if (presetKey != PRESET_KEY_CUSTOM)
        {
            Dictionary<string, PlayerSettingsModel> presets = AppSettingsModel.PlayerSettingPresets;
            if (!presets.TryGetValue(presetKey, out presetModel))
            {
                if (!presets.TryGetValue(defaultPresetKey, out presetModel))
                {
                    foreach (PlayerSettingsModel preset in presets.Values)
                    {
                        presetModel = preset;
                        break;
                    }
                }
            }

            if (presetModel is null)
            {
                // Automatically import missing key, especially for default preset
                presetModel = FromDefault();
                presets[presetKey] = presetModel;
                AppSettingsModel.PlayerSettingPresets = presets;
            }
            else
            {
                presetKey = presetModel.PresetKey;
            }
        }

        JsonModel? jsonModel = null;
        string? customSettingsJson = media.GetExt(MediaExt.CUSTOM_PLAYER_SETTINGS);
        if (!string.IsNullOrEmpty(customSettingsJson))
        {
            try
            {
                jsonModel = JsonSerializer.Deserialize<JsonModel>(customSettingsJson);
            }
            catch (JsonException ex)
            {
                Logger.E(TAG, ex);
            }
        }

        if (jsonModel is not null)
        {
            PlayerSettingsModel model = FromJsonModel(presetKey, jsonModel, presetModel);
            if (presetModel is not null)
            {
                model.PresetName = presetModel.PresetName;
            }

            return model;
        }

        if (presetModel is not null)
        {
            return presetModel;
        }

        return FromDefault();
    }

    public static PlayerSettingsModel? LoadFromPreset(string presetKey)
    {
        if (presetKey == PRESET_KEY_CUSTOM)
        {
            return null;
        }

        Dictionary<string, PlayerSettingsModel> presets = AppSettingsModel.PlayerSettingPresets;
        if (!presets.TryGetValue(presetKey, out PlayerSettingsModel? presetModel))
        {
            return null;
        }

        return presetModel;
    }

    public string PresetKey { get; set; } = string.Empty;
    public string PresetName { get; set; } = "?";

    private PlayerSettingsModel() { }

    public PlayerSettingsModel Clone()
    {
        return (PlayerSettingsModel)MemberwiseClone();
    }

    public JsonModel ToJsonModel()
    {
        return new()
        {
            PresetName = PresetName,
        };
    }

    public void SaveToMedia(MediaModel media)
    {
        string customSettingsJson = JsonSerializer.Serialize(ToJsonModel());
        media.SetExt(MediaExt.PLAYER_SETTING_PRESET_KEY, PresetKey);
        media.SetExt(MediaExt.CUSTOM_PLAYER_SETTINGS, customSettingsJson);
        CoroutineUtils.Run(media.FlushExt);
    }

    public class JsonModel
    {
        [JsonPropertyName("PresetName")]
        public string? PresetName { get; set; }
    }
}
