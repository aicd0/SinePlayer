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

    public static IReadOnlyList<MediaStreamInfo> Resolve(
        TrackSelectionModel? record,
        TrackSelectionModel? media,
        int viewIndex,
        bool opening,
        MediaStreamKind kind,
        IReadOnlyList<MediaSource> sources)
    {
        if (kind is MediaStreamKind.Audio)
        {
            return Choose(record?.AudioSelections ?? media?.AudioSelections, opening, kind, sources);
        }

        ViewTrackSelectionModel? recordView = ViewAt(record, viewIndex);
        ViewTrackSelectionModel? mediaView = ViewAt(media, viewIndex);

        if (kind is MediaStreamKind.Video)
        {
            SelectedTrackModel? selection = recordView?.VideoSelection ?? mediaView?.VideoSelection;
            if (selection is null)
            {
                return opening ? Defaults(sources, kind) : [];
            }

            if (!selection.Enabled)
            {
                return [];
            }

            return selection.Track is { } track && Find(sources, kind, track) is { } stream
                ? [stream]
                : opening ? Defaults(sources, kind) : [];
        }

        return Choose(recordView?.SubtitleSelections ?? mediaView?.SubtitleSelections, opening, kind, sources);
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
    public static IReadOnlyList<string> PathsOf(TrackSelectionModel? model)
    {
        if (model is null)
        {
            return [];
        }

        List<string> paths = [];
        if (model.Views is { } views)
        {
            foreach (ViewTrackSelectionModel view in views)
            {
                if (view.VideoSelection is { Enabled: true, Track: { } video })
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

    /// <summary>
    /// A set of streams: an absent list is no preference, an empty one is "show none", and entries that no longer resolve
    /// are dropped — with the default standing in only when this view opened the media and nothing survived.
    /// </summary>
    private static IReadOnlyList<MediaStreamInfo> Choose(List<TrackIdentityModel>? entries, bool opening, MediaStreamKind kind, IReadOnlyList<MediaSource> sources)
    {
        if (entries is null)
        {
            return opening ? Defaults(sources, kind) : [];
        }

        if (entries.Count == 0)
        {
            return [];
        }

        List<MediaStreamInfo> chosen = [];
        foreach (TrackIdentityModel entry in entries)
        {
            if (Find(sources, kind, entry) is { } stream)
            {
                chosen.Add(stream);
            }
        }

        if (chosen.Count == 0 && opening)
        {
            chosen.AddRange(Defaults(sources, kind));
        }

        return chosen;
    }

    private static IReadOnlyList<MediaStreamInfo> Defaults(IReadOnlyList<MediaSource> sources, MediaStreamKind kind)
    {
        return Default(sources, kind) is { } stream ? [stream] : [];
    }

    private static ViewTrackSelectionModel? ViewAt(TrackSelectionModel? model, int index)
    {
        return model?.Views is { } views && index >= 0 && index < views.Count ? views[index] : null;
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

    private static MediaStreamInfo? Find(IReadOnlyList<MediaSource> sources, MediaStreamKind kind, TrackIdentityModel track)
    {
        foreach (MediaSource source in sources)
        {
            if (source.Locator.Value != track.Path)
            {
                continue;
            }

            foreach (MediaStreamInfo stream in source.Info.Streams)
            {
                if (stream.Kind == kind && stream.Index == track.Index)
                {
                    return stream;
                }
            }
        }

        return null;
    }
}
