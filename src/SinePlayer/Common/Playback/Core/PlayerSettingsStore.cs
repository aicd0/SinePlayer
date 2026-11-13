// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;

namespace SinePlayer.Common.Playback.Core;

internal static class PlayerSettingsStore
{
    private const string TAG = nameof(PlayerSettingsStore);

    public static string? GetGlobalRecord(MediaModel media, MediaStreamKind kind)
    {
        PlayerSettingsScopeJsonModel? scope = Load(media).Global;
        return kind switch
        {
            MediaStreamKind.Video => ToJson(scope?.Video),
            MediaStreamKind.Audio => ToJson(scope?.Audio),
            MediaStreamKind.Subtitle => ToJson(scope?.Subtitle),
            _ => null,
        };
    }

    public static void SetGlobalRecord(MediaModel media, MediaStreamKind kind, string recordJson)
    {
        PlayerSettingsJsonModel model = Load(media);
        model.Global ??= new();

        switch (kind)
        {
            case MediaStreamKind.Video:
                model.Global.Video = FromJson<VideoSettingsModel.JsonModel>(recordJson);
                break;
            case MediaStreamKind.Audio:
                model.Global.Audio = FromJson<AudioSettingsModel.JsonModel>(recordJson);
                break;
            case MediaStreamKind.Subtitle:
                model.Global.Subtitle = FromJson<SubtitleSettingsModel.JsonModel>(recordJson);
                break;
        }

        Store(media, model);
    }

    public static string? GetTrackRecord(MediaModel media, MediaStreamKind kind, TrackIdentityModel track)
    {
        TrackSettingsJsonModel? tracks = Load(media).Tracks;
        return kind switch
        {
            MediaStreamKind.Video => ToJson(Find(tracks?.Videos, track)),
            MediaStreamKind.Audio => ToJson(Find(tracks?.Audios, track)),
            MediaStreamKind.Subtitle => ToJson(Find(tracks?.Subtitles, track)),
            _ => null,
        };
    }

    public static void SetTrackRecord(MediaModel media, MediaStreamKind kind, TrackIdentityModel track, string recordJson)
    {
        PlayerSettingsJsonModel model = Load(media);
        model.Tracks ??= new();

        switch (kind)
        {
            case MediaStreamKind.Video:
                model.Tracks.Videos ??= [];
                Set(model.Tracks.Videos, track, FromJson<VideoSettingsModel.JsonModel>(recordJson));
                break;
            case MediaStreamKind.Audio:
                model.Tracks.Audios ??= [];
                Set(model.Tracks.Audios, track, FromJson<AudioSettingsModel.JsonModel>(recordJson));
                break;
            case MediaStreamKind.Subtitle:
                model.Tracks.Subtitles ??= [];
                Set(model.Tracks.Subtitles, track, FromJson<SubtitleSettingsModel.JsonModel>(recordJson));
                break;
        }

        Store(media, model);
    }

    public static void ClearTrackRecords(MediaModel media, MediaStreamKind kind)
    {
        PlayerSettingsJsonModel model = Load(media);
        if (model.Tracks is null)
        {
            return;
        }

        switch (kind)
        {
            case MediaStreamKind.Video:
                model.Tracks.Videos = null;
                break;
            case MediaStreamKind.Audio:
                model.Tracks.Audios = null;
                break;
            case MediaStreamKind.Subtitle:
                model.Tracks.Subtitles = null;
                break;
        }

        Store(media, model);
    }

    public static string? GetRecord(MediaModel media, MediaStreamKind kind, TrackIdentityModel? track)
    {
        return track is null ? GetGlobalRecord(media, kind) : GetTrackRecord(media, kind, track) ?? GetGlobalRecord(media, kind);
    }

    private static PlayerSettingsJsonModel Load(MediaModel media)
    {
        string? json = media.GetExt(MediaExt.PLAYER_SETTINGS);
        if (string.IsNullOrEmpty(json))
        {
            return new();
        }

        try
        {
            return JsonSerializer.Deserialize<PlayerSettingsJsonModel>(json) ?? new();
        }
        catch (JsonException ex)
        {
            Logger.E(TAG, ex);
            return new();
        }
    }

    private static void Store(MediaModel media, PlayerSettingsJsonModel model)
    {
        string json = JsonSerializer.Serialize(model);
        if (media.GetExt(MediaExt.PLAYER_SETTINGS) == json)
        {
            return;
        }

        media.SetExt(MediaExt.PLAYER_SETTINGS, json);
        CoroutineUtils.Run(media.FlushExt);
    }

    private static SettingsRecordJsonModel<TJson>? Find<TJson>(List<TrackSettingsEntryJsonModel<TJson>>? entries, TrackIdentityModel track) where TJson : class
    {
        if (entries is null)
        {
            return null;
        }

        foreach (TrackSettingsEntryJsonModel<TJson> entry in entries)
        {
            if (entry.Track.Equals(track))
            {
                return entry.Record;
            }
        }

        return null;
    }

    private static void Set<TJson>(List<TrackSettingsEntryJsonModel<TJson>> entries, TrackIdentityModel track, SettingsRecordJsonModel<TJson>? record) where TJson : class
    {
        foreach (TrackSettingsEntryJsonModel<TJson> entry in entries)
        {
            if (entry.Track.Equals(track))
            {
                entry.Record = record;
                return;
            }
        }

        entries.Add(new() { Track = track, Record = record });
    }

    private static string? ToJson<TJson>(SettingsRecordJsonModel<TJson>? record) where TJson : class
    {
        return record is null ? null : JsonSerializer.Serialize(record);
    }

    private static SettingsRecordJsonModel<TJson>? FromJson<TJson>(string recordJson) where TJson : class
    {
        return JsonSerializer.Deserialize<SettingsRecordJsonModel<TJson>>(recordJson);
    }
}

internal sealed class PlayerSettingsJsonModel
{
    [JsonPropertyName("Global")]
    public PlayerSettingsScopeJsonModel? Global { get; set; }

    [JsonPropertyName("Tracks")]
    public TrackSettingsJsonModel? Tracks { get; set; }
}

internal sealed class PlayerSettingsScopeJsonModel
{
    [JsonPropertyName("Video")]
    public SettingsRecordJsonModel<VideoSettingsModel.JsonModel>? Video { get; set; }

    [JsonPropertyName("Audio")]
    public SettingsRecordJsonModel<AudioSettingsModel.JsonModel>? Audio { get; set; }

    [JsonPropertyName("Subtitle")]
    public SettingsRecordJsonModel<SubtitleSettingsModel.JsonModel>? Subtitle { get; set; }
}

internal sealed class TrackSettingsJsonModel
{
    [JsonPropertyName("Videos")]
    public List<TrackSettingsEntryJsonModel<VideoSettingsModel.JsonModel>>? Videos { get; set; }

    [JsonPropertyName("Audios")]
    public List<TrackSettingsEntryJsonModel<AudioSettingsModel.JsonModel>>? Audios { get; set; }

    [JsonPropertyName("Subtitles")]
    public List<TrackSettingsEntryJsonModel<SubtitleSettingsModel.JsonModel>>? Subtitles { get; set; }
}

internal sealed class TrackSettingsEntryJsonModel<TJson> where TJson : class
{
    [JsonPropertyName("Track")]
    public TrackIdentityModel Track { get; set; } = new();

    [JsonPropertyName("Record")]
    public SettingsRecordJsonModel<TJson>? Record { get; set; }
}
