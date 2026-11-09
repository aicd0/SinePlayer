// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

using SinePlayer.Common.Constants;
using SinePlayer.Data.Database;
using SinePlayer.SDK.Models;

namespace SinePlayer.Common.Playback.Core;

internal sealed class TabPlayerSessionModel
{
    private const string KEY = "PlayerSession";

    public static void Save(string tabId, string sessionId)
    {
        if (string.IsNullOrEmpty(tabId) || string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        string serialized = JsonSerializer.Serialize(new TabPlayerSessionModel() { SessionId = sessionId });
        AppDB.MainRegistry.CreateKey($"{RegistryNames.TAB_RESOURCES}/{tabId}").Set(KEY, serialized);
    }

    public static string? SessionIdOf(string tabId)
    {
        if (string.IsNullOrEmpty(tabId) || !AppDB.MainRegistry.TryGetKey($"{RegistryNames.TAB_RESOURCES}/{tabId}", out IRegistryKey? key))
        {
            return null;
        }

        if (!key.TryGet(KEY, out string? serialized) || string.IsNullOrEmpty(serialized))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TabPlayerSessionModel>(serialized)?.SessionId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static HashSet<string> SessionIdsOf(IEnumerable<string> tabIds)
    {
        HashSet<string> sessionIds = [];
        foreach (string tabId in tabIds)
        {
            if (SessionIdOf(tabId) is { Length: > 0 } sessionId)
            {
                sessionIds.Add(sessionId);
            }
        }

        return sessionIds;
    }

    [JsonPropertyName("SessionId")]
    public string SessionId { get; set; } = string.Empty;
}
