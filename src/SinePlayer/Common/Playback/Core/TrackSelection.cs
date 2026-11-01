// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Text.Json;

using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Data.Models.Media;

namespace SinePlayer.Common.Playback.Core;

internal static class TrackSelection
{
    public static Preference? Load(MediaModel media, MediaStreamKind kind, IReadOnlyList<MediaSource> sources)
    {
        string? value = media.GetExt(KeyOf(kind));
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        SelectedTrackModel? model = Parse(value);
        if (model is null)
        {
            return null;
        }

        return new Preference
        {
            Enabled = model.Enabled,
            Path = model.Path,
            Index = model.Index,
            Stream = model.Enabled ? Find(sources, model.Path, kind, model.Index) : null,
        };
    }

    public static void Save(MediaModel media, MediaStreamKind kind, IReadOnlyList<MediaSource> sources, MediaSource? source, MediaStreamInfo? stream)
    {
        SelectedTrackModel model = stream is null
            ? new() { Enabled = false }
            : new()
            {
                Enabled = true,
                Path = PathOf(sources, source),
                Index = stream.Index,
            };

        string value = JsonSerializer.Serialize(model);
        if (media.GetExt(KeyOf(kind)) == value)
        {
            return;
        }

        media.SetExt(KeyOf(kind), value);
        CoroutineUtils.Run(media.FlushExt);
    }

    private static SelectedTrackModel? Parse(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<SelectedTrackModel>(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MediaStreamInfo? Find(IReadOnlyList<MediaSource> sources, string path, MediaStreamKind kind, int index)
    {
        foreach (MediaSource source in sources)
        {
            if (!string.IsNullOrEmpty(path) ? source.Locator.Value != path : source != sources[0])
            {
                continue;
            }

            foreach (MediaStreamInfo stream in source.Info.Streams)
            {
                if (stream.Kind == kind && stream.Index == index)
                {
                    return stream;
                }
            }
        }

        return null;
    }

    private static string PathOf(IReadOnlyList<MediaSource> sources, MediaSource? source)
    {
        return source is null || (sources.Count > 0 && source == sources[0]) ? string.Empty : source.Locator.Value;
    }

    private static string KeyOf(MediaStreamKind kind)
    {
        return kind switch
        {
            MediaStreamKind.Video => MediaExt.SELECTED_VIDEO_TRACK,
            MediaStreamKind.Audio => MediaExt.SELECTED_AUDIO_TRACK,
            MediaStreamKind.Subtitle => MediaExt.SELECTED_SUBTITLE_TRACK,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only the picture, the sound and the subtitles can be chosen."),
        };
    }

    internal sealed class Preference
    {
        public bool Enabled { get; init; }

        public string Path { get; init; } = string.Empty;

        public int Index { get; init; }

        public MediaStreamInfo? Stream { get; init; }
    }
}
