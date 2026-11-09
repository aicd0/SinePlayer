// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Data.Models.Media;

namespace SinePlayer.Common.Playback.Core;

internal static class TrackSelection
{
    public static TrackSelectionModel? Load(MediaModel media)
    {
        string? value = media.GetExt(MediaExt.TRACK_SELECTION);
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TrackSelectionModel>(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static MediaStreamInfo? Resolve(MediaModel media, MediaStreamKind kind, IReadOnlyList<MediaSource> sources)
    {
        TrackSelectionModel? model = Load(media);
        if (model is null)
        {
            return Default(sources, kind);
        }

        if (kind is MediaStreamKind.Audio)
        {
            return model.AudioSelections is { } audios ? ResolveFrom(audios, kind, sources) : Default(sources, kind);
        }

        ViewTrackSelectionModel? view = FirstView(model);
        if (kind is MediaStreamKind.Video)
        {
            SelectedTrackModel? selection = view?.VideoSelection;
            if (selection is null)
            {
                return Default(sources, kind);
            }

            return selection.Enabled ? Find(sources, kind, selection.Path, selection.Index) ?? Default(sources, kind) : null;
        }

        return view?.SubtitleSelections is { } subtitles ? ResolveFrom(subtitles, kind, sources) : Default(sources, kind);
    }

    public static void Save(MediaModel media, TrackSelectionModel model)
    {
        Store(media, model);
    }

    public static bool AllowMultiAudioTracks(MediaModel media)
    {
        return Load(media)?.AllowMultiAudioTracks ?? false;
    }

    public static bool AllowMultiSubtitleTracks(MediaModel media)
    {
        TrackSelectionModel? model = Load(media);
        return model is not null && FirstView(model) is { AllowMultiSubtitleTracks: true };
    }

    public static void SetAllowMultiAudioTracks(MediaModel media, bool allow)
    {
        TrackSelectionModel model = Load(media) ?? new TrackSelectionModel();
        if (model.AllowMultiAudioTracks == allow)
        {
            return;
        }

        model.AllowMultiAudioTracks = allow;
        Store(media, model);
    }

    public static void SetAllowMultiSubtitleTracks(MediaModel media, bool allow)
    {
        TrackSelectionModel model = Load(media) ?? new TrackSelectionModel();
        ViewTrackSelectionModel view = EnsureFirstView(model);
        if (view.AllowMultiSubtitleTracks == allow)
        {
            return;
        }

        view.AllowMultiSubtitleTracks = allow;
        Store(media, model);
    }

    /// <summary>
    /// The sources this media's remembered tracks name, so one that is not part of the media — an external subtitle file —
    /// can be added before anything is resolved.
    /// </summary>
    public static IReadOnlyList<string> RememberedPaths(MediaModel media)
    {
        TrackSelectionModel? model = Load(media);
        if (model is null)
        {
            return [];
        }

        List<string> paths = [];
        if (model.Views is { } views)
        {
            foreach (ViewTrackSelectionModel view in views)
            {
                if (view.VideoSelection is { Enabled: true } video)
                {
                    paths.Add(video.Path);
                }

                if (view.SubtitleSelections is { } subtitles)
                {
                    paths.AddRange(subtitles.Select(entry => entry.Path));
                }
            }
        }

        if (model.AudioSelections is { } audios)
        {
            paths.AddRange(audios.Select(entry => entry.Path));
        }

        return [.. paths.Where(path => !string.IsNullOrEmpty(path)).Distinct()];
    }

    private static MediaStreamInfo? ResolveFrom(List<TrackSelectionEntryModel> entries, MediaStreamKind kind, IReadOnlyList<MediaSource> sources)
    {
        if (entries.Count == 0)
        {
            return null;
        }

        foreach (TrackSelectionEntryModel entry in entries)
        {
            if (Find(sources, kind, entry.Path, entry.Index) is { } stream)
            {
                return stream;
            }
        }

        return Default(sources, kind);
    }

    private static ViewTrackSelectionModel? FirstView(TrackSelectionModel model)
    {
        return model.Views is { Count: > 0 } views ? views[0] : null;
    }

    private static ViewTrackSelectionModel EnsureFirstView(TrackSelectionModel model)
    {
        if (FirstView(model) is { } existing)
        {
            return existing;
        }

        ViewTrackSelectionModel view = new();
        model.Views = [view];
        return view;
    }

    private static void Store(MediaModel media, TrackSelectionModel model)
    {
        string value = JsonSerializer.Serialize(model);
        if (media.GetExt(MediaExt.TRACK_SELECTION) == value)
        {
            return;
        }

        media.SetExt(MediaExt.TRACK_SELECTION, value);
        CoroutineUtils.Run(media.FlushExt);
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

    private static MediaStreamInfo? Find(IReadOnlyList<MediaSource> sources, MediaStreamKind kind, string path, int index)
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
}
