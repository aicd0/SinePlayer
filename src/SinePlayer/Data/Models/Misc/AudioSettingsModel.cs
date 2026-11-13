// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Text.Json.Serialization;

using SinePlayer.Common.Playback.Core;

namespace SinePlayer.Data.Models.Misc;

internal sealed class AudioSettingsModel
{
    public static AudioSettingsModel FromDefault()
    {
        return new();
    }

    public static AudioSettingsModel FromJsonModel(JsonModel? model, AudioSettingsModel fallback)
    {
        if (model is null)
        {
            return fallback.Clone();
        }

        return new()
        {
            Decoder = model.Decoder ?? fallback.Decoder,
            Renderer = model.Renderer ?? fallback.Renderer,
        };
    }

    public string Decoder { get; set; } = PlaybackComponents.AUTO;
    public string Renderer { get; set; } = PlaybackComponents.AUTO;

    public AudioSettingsModel Clone()
    {
        return (AudioSettingsModel)MemberwiseClone();
    }

    public JsonModel ToJsonModel()
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
