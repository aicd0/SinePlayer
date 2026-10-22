// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using SinePlayer.Common.Localization;
using SinePlayer.Common.Playback.Core;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Media;

namespace SinePlayer.Data.Models.Misc;

internal class PlayerSettingsModel
{
    public const string PRESET_KEY_DEFAULT = "###default###";
    public const string PRESET_KEY_CUSTOM = "###custom###";
    private const string TAG = nameof(PlayerSettingsModel);

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
            VideoDecoder = model.VideoDecoder ?? fallbackModel.VideoDecoder,
            AudioDecoder = model.AudioDecoder ?? fallbackModel.AudioDecoder,
            VideoRenderer = model.VideoRenderer ?? fallbackModel.VideoRenderer,
            AudioRenderer = model.AudioRenderer ?? fallbackModel.AudioRenderer,
            VideoPreferHardwareDecoding = model.VideoPreferHardwareDecoding ?? fallbackModel.VideoPreferHardwareDecoding,
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

    public string VideoDecoder { get; set; } = PlaybackComponents.AUTO;
    public string AudioDecoder { get; set; } = PlaybackComponents.AUTO;
    public string VideoRenderer { get; set; } = PlaybackComponents.AUTO;
    public string AudioRenderer { get; set; } = PlaybackComponents.AUTO;
    public bool VideoPreferHardwareDecoding { get; set; } = true;

    private PlayerSettingsModel() { }

    public PlayerSettingsModel Clone()
    {
        return (PlayerSettingsModel)MemberwiseClone();
    }

    public PlaybackComponents ToComponents()
    {
        return new(VideoDecoder, AudioDecoder, VideoRenderer, AudioRenderer, VideoPreferHardwareDecoding);
    }

    public JsonModel ToJsonModel()
    {
        return new()
        {
            PresetName = PresetName,
            VideoDecoder = VideoDecoder,
            AudioDecoder = AudioDecoder,
            VideoRenderer = VideoRenderer,
            AudioRenderer = AudioRenderer,
            VideoPreferHardwareDecoding = VideoPreferHardwareDecoding,
        };
    }

    public void SaveToMedia(MediaModel media)
    {
        JsonModel? jsonModel = ToJsonModel();

        PlayerSettingsModel? diffModel = LoadFromPreset(PresetKey);
        if (diffModel is not null)
        {
            var diffJsonModel = diffModel.ToJsonModel();
            jsonModel = NullOutMatchingFields(jsonModel, diffJsonModel);
        }

        string customSettingsJson = JsonSerializer.Serialize(jsonModel);
        media.SetExt(MediaExt.PLAYER_SETTING_PRESET_KEY, PresetKey);
        media.SetExt(MediaExt.CUSTOM_PLAYER_SETTINGS, customSettingsJson);
        CoroutineUtils.Run(media.FlushExt);
    }

    public static T? NullOutMatchingFields<T>(T? first, T? second) where T : class
    {
        bool Diff(JsonNode? first, JsonNode? second)
        {
            if (first is null || second is null)
            {
                return first == second;
            }

            if (first is JsonValue && second is JsonValue)
            {
                return JsonNode.DeepEquals(first, second);
            }

            if (first is JsonObject firstObj && second is JsonObject secondObj)
            {
                bool allEqual = true;

                foreach (KeyValuePair<string, JsonNode?> kvp in firstObj.ToList())
                {
                    if (!secondObj.TryGetPropertyValue(kvp.Key, out JsonNode? secondValue))
                    {
                        allEqual = false;
                        continue;
                    }

                    JsonNode? firstValue = kvp.Value;

                    bool equal = Diff(firstValue, secondValue);

                    if (equal)
                    {
                        firstObj[kvp.Key] = null;
                    }
                    else
                    {
                        allEqual = false;
                    }
                }

                return allEqual;
            }

            if (first is JsonArray firstArr && second is JsonArray secondArr)
            {
                if (firstArr.Count != secondArr.Count)
                {
                    return false;
                }

                for (int i = 0; i < firstArr.Count; i++)
                {
                    bool equal = Diff(firstArr[i], secondArr[i]);

                    if (!equal)
                    {
                        return false;
                    }
                }

                return true;
            }

            return false;
        }

        if (first is null || second is null)
        {
            return first;
        }

        JsonNode firstNode = JsonSerializer.SerializeToNode(first)!;
        JsonNode secondNode = JsonSerializer.SerializeToNode(second)!;

        if (Diff(firstNode, secondNode))
        {
            return null;
        }

        return firstNode.Deserialize<T>()!;
    }

    public class JsonModel
    {
        [JsonPropertyName("PresetName")]
        public string? PresetName { get; set; }

        [JsonPropertyName("VideoDecoder")]
        public string? VideoDecoder { get; set; }

        [JsonPropertyName("AudioDecoder")]
        public string? AudioDecoder { get; set; }

        [JsonPropertyName("VideoRenderer")]
        public string? VideoRenderer { get; set; }

        [JsonPropertyName("AudioRenderer")]
        public string? AudioRenderer { get; set; }

        [JsonPropertyName("VideoPreferHardwareDecoding")]
        public bool? VideoPreferHardwareDecoding { get; set; }
    }
}
