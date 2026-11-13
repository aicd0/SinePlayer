// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using SinePlayer.Common.Localization;
using SinePlayer.Core.Common.DebugTools;

namespace SinePlayer.Data.Models.Misc;

internal abstract class PresetSettingsModel
{
    public const string PRESET_KEY_DEFAULT = "###default###";
    public const string PRESET_KEY_CUSTOM = "###custom###";

    public string PresetKey { get; set; } = string.Empty;
    public string PresetName { get; set; } = "?";
    public bool AllowVariation { get; set; }

    public abstract string ToRecordJson();

    public abstract string ToValuesJson();

    public abstract void ApplyValuesJson(string valuesJson);

    public abstract PresetSettingsModel CreateDefault();

    public abstract PresetSettingsModel? FindPreset(string presetKey);

    public abstract IReadOnlyList<(string Key, string Name)> ListPresets();

    public abstract PresetSettingsModel ResolveRecord(string? recordJson);
}

/// <summary>
/// One category of player settings — the video, audio or subtitle part — with the preset it follows and the
/// difference that was stored over that preset.
/// </summary>
internal abstract class PresetSettingsModel<TSelf, TJson> : PresetSettingsModel
    where TSelf : PresetSettingsModel<TSelf, TJson>, new()
    where TJson : class
{
    private const string TAG = nameof(PresetSettingsModel);

    public static TSelf FromDefault()
    {
        return new()
        {
            PresetKey = PRESET_KEY_DEFAULT,
            PresetName = StringResourceProvider.Instance.Default,
        };
    }

    public static TSelf FromRecordJson(string? json)
    {
        SettingsRecordJsonModel<TJson>? record = null;
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                record = JsonSerializer.Deserialize<SettingsRecordJsonModel<TJson>>(json);
            }
            catch (JsonException ex)
            {
                Logger.E(TAG, ex);
            }
        }

        TSelf resolver = new();
        return resolver.Resolve(record);
    }

    public static TSelf? LoadFromPreset(string presetKey)
    {
        if (presetKey == PRESET_KEY_CUSTOM)
        {
            return null;
        }

        TSelf resolver = new();
        return resolver.LoadPresets().TryGetValue(presetKey, out TSelf? preset) ? preset : null;
    }

    public static TSelf FromPresetJsonModel(string presetKey, PresetJsonModel<TJson>? model)
    {
        TSelf settings = new() { PresetKey = presetKey };
        if (model is null)
        {
            return settings;
        }

        settings.PresetName = model.PresetName ?? settings.PresetName;
        settings.AllowVariation = model.AllowVariation ?? settings.AllowVariation;

        if (model.Settings is { } values)
        {
            settings.ApplyJsonModel(values);
        }

        return settings;
    }

    public TSelf Clone()
    {
        return (TSelf)MemberwiseClone();
    }

    public override string ToValuesJson()
    {
        return JsonSerializer.Serialize(ToJsonModel());
    }

    public override void ApplyValuesJson(string valuesJson)
    {
        if (JsonSerializer.Deserialize<TJson>(valuesJson) is { } model)
        {
            ApplyJsonModel(model);
        }
    }

    public override PresetSettingsModel CreateDefault()
    {
        return FromDefault();
    }

    public override PresetSettingsModel? FindPreset(string presetKey)
    {
        return LoadFromPreset(presetKey);
    }

    public override IReadOnlyList<(string Key, string Name)> ListPresets()
    {
        List<(string Key, string Name)> entries = [];
        foreach (KeyValuePair<string, TSelf> kvp in LoadPresets())
        {
            entries.Add((kvp.Key, kvp.Value.PresetName));
        }

        return entries;
    }

    public override PresetSettingsModel ResolveRecord(string? recordJson)
    {
        return FromRecordJson(recordJson);
    }

    public PresetJsonModel<TJson> ToPresetJsonModel()
    {
        return new()
        {
            PresetName = PresetName,
            AllowVariation = AllowVariation,
            Settings = ToJsonModel(),
        };
    }

    public override string ToRecordJson()
    {
        Dictionary<string, TSelf> presets = LoadPresets();
        TSelf? presetModel = PresetKey != PRESET_KEY_CUSTOM && presets.TryGetValue(PresetKey, out TSelf? preset) ? preset : null;

        if (presetModel is not null && !presetModel.AllowVariation)
        {
            // This preset is the only place its values live, so an edit changes the preset itself and the media
            // keeps nothing but the key.
            TSelf updated = Clone();
            updated.PresetName = presetModel.PresetName;
            updated.AllowVariation = presetModel.AllowVariation;
            presets[updated.PresetKey] = updated;
            SavePresets(presets);
            return JsonSerializer.Serialize(new SettingsRecordJsonModel<TJson>() { PresetKey = PresetKey });
        }

        TJson values = ToJsonModel();
        TJson? difference = presetModel is null ? values : NullOutMatchingFields(values, presetModel.ToJsonModel());
        return JsonSerializer.Serialize(new SettingsRecordJsonModel<TJson>() { PresetKey = PresetKey, Settings = difference });
    }

    protected abstract Dictionary<string, TSelf> LoadPresets();

    protected abstract void SavePresets(Dictionary<string, TSelf> presets);

    protected abstract string LoadDefaultPresetKey();

    protected abstract TJson ToJsonModel();

    protected abstract void ApplyJsonModel(TJson model);

    private TSelf Resolve(SettingsRecordJsonModel<TJson>? record)
    {
        Dictionary<string, TSelf> presets = LoadPresets();
        string defaultPresetKey = LoadDefaultPresetKey();
        if (string.IsNullOrEmpty(defaultPresetKey))
        {
            defaultPresetKey = PRESET_KEY_DEFAULT;
        }

        string presetKey = string.IsNullOrEmpty(record?.PresetKey) ? defaultPresetKey : record.PresetKey;

        TSelf? presetModel = null;
        if (presetKey != PRESET_KEY_CUSTOM)
        {
            if (!presets.TryGetValue(presetKey, out presetModel))
            {
                if (!presets.TryGetValue(defaultPresetKey, out presetModel))
                {
                    foreach (TSelf preset in presets.Values)
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
                presetModel.PresetKey = presetKey;
                presets[presetKey] = presetModel;
                SavePresets(presets);
            }
        }

        TSelf settings;
        if (presetModel is not null)
        {
            settings = presetModel.Clone();
        }
        else
        {
            settings = new()
            {
                PresetKey = PRESET_KEY_CUSTOM,
                PresetName = StringResourceProvider.Instance.Custom,
            };
        }

        // A preset that does not allow per-media configuration leaves no room for a difference, so one left behind
        // by an earlier state of the preset is ignored.
        if (record?.Settings is { } recordSettings && (presetModel is null || presetModel.AllowVariation))
        {
            settings.ApplyJsonModel(recordSettings);
        }

        return settings;
    }

    private static T? NullOutMatchingFields<T>(T? first, T? second) where T : class
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
}

/// <summary>
/// What a media or a track keeps of a category: the preset it follows, and only the values that differ from it.
/// </summary>
internal sealed class SettingsRecordJsonModel<TJson> where TJson : class
{
    [JsonPropertyName("PresetKey")]
    public string? PresetKey { get; set; }

    [JsonPropertyName("Settings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TJson? Settings { get; set; }
}

/// <summary>
/// A stored preset: its name, whether a media may deviate from it, and its values.
/// </summary>
internal sealed class PresetJsonModel<TJson> where TJson : class
{
    [JsonPropertyName("PresetName")]
    public string? PresetName { get; set; }

    [JsonPropertyName("AllowVariation")]
    public bool? AllowVariation { get; set; }

    [JsonPropertyName("Settings")]
    public TJson? Settings { get; set; }
}
