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
    public static MediaStreamInfo? Resolve(MediaModel media, MediaStreamKind kind, IReadOnlyList<MediaSource> sources)
    {
        Preference? preference = Load(media, kind, sources);
        if (preference is null)
        {
            return Default(sources, kind);
        }

        return preference.Enabled ? preference.Stream ?? Default(sources, kind) : null;
    }

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

    public static void Save(MediaModel media, MediaStreamKind kind, MediaSource? source, MediaStreamInfo? stream)
    {
        SelectedTrackModel model = stream is null
            ? new() { Enabled = false }
            : new()
            {
                Enabled = true,
                Path = source?.Locator.Value ?? string.Empty,
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

    private static MediaStreamInfo? Default(IReadOnlyList<MediaSource> sources, MediaStreamKind kind)
    {
        MediaStreamInfo? first = null;
        foreach (MediaSource source in sources)
        {
            foreach (MediaStreamInfo stream in source.Info.Streams)
            {
                if (stream.Kind != kind)
                {
                    continue;
                }

                if (stream.IsDefault)
                {
                    return stream;
                }

                first ??= stream;
            }
        }

        return first;
    }

    private static MediaStreamInfo? Find(IReadOnlyList<MediaSource> sources, string path, MediaStreamKind kind, int index)
    {
        foreach (MediaSource source in sources)
        {
            if (source.Locator.Value != path)
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
