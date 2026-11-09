// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

using SinePlayer.Common.Constants;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Data.Database;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Data.Models.Playback;
using SinePlayer.SDK.Models;

namespace SinePlayer.Common.Playback.Core;

internal sealed class PlayerSessionRecord
{
    private const string TAG = nameof(PlayerSessionRecord);

    [JsonPropertyName("PlayQueue")]
    public List<PlayQueueModel.PlayQueueItemJsonModel>? PlayQueue { get; set; }

    [JsonPropertyName("Playback")]
    public PlaybackModel.PlaybackJsonModel? Playback { get; set; }

    [JsonPropertyName("TrackSelection")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TrackSelectionModel? TrackSelection { get; set; }

    public static PlayerSessionRecord? Load(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || !AppDB.MainRegistry.TryGetKey(RegistryNames.PLAYER_SESSIONS, out IRegistryKey? key))
        {
            return null;
        }

        if (!key.TryGet(sessionId, out string? serialized) || string.IsNullOrEmpty(serialized))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PlayerSessionRecord>(serialized);
        }
        catch (JsonException ex)
        {
            Logger.E(TAG, ex);
            return null;
        }
    }

    public static void Save(string sessionId, PlayerSessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        string serialized = JsonSerializer.Serialize(record);
        AppDB.MainRegistry.CreateKey(RegistryNames.PLAYER_SESSIONS).Set(sessionId, serialized);
    }
}
