// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Linq;

using SinePlayer.Common.Actions;
using SinePlayer.Common.Misc;
using SinePlayer.Common.Playback.Core;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Data.Models.Playback;
using SinePlayer.Views.Pages.Player;

using Windows.Storage;

namespace SinePlayer.Helpers.MenuFlyoutHelpers;

internal sealed class PlayerMenuContext
{
    public required int WindowId { get; init; }

    public required ActionHandler ActionHandler { get; init; }

    public required MediaModel Media { get; init; }

    public required PlayerSettingsModel Settings { get; init; }

    public required Action<Action<PlayerSettingsModel>> UpdateSettings { get; init; }

    public PlayQueueModel.Builder? PlayQueue { get; init; }

    public PlaybackModel.Builder? Playback { get; init; }

    public CoreEngine? Engine { get; init; }

    public Action<string>? PromptInfo { get; init; }

    public IReadOnlyList<MediaSource> Sources => Engine?.PlaybackInfo.Sources ?? [];

    public IReadOnlyList<MediaStreamInfo>? Tracks(MediaStreamKind kind)
    {
        IReadOnlyList<MediaSource> sources = Sources;
        if (sources.Count == 0)
        {
            return null;
        }

        List<MediaStreamInfo> streams = [];
        foreach (MediaSource source in sources)
        {
            streams.AddRange(StreamsOf(source, kind));
        }

        return streams;
    }

    public MediaStreamInfo? SelectedTrack(MediaStreamKind kind)
    {
        return kind switch
        {
            MediaStreamKind.Video => Engine?.PlaybackInfo.SelectedVideoStream,
            MediaStreamKind.Audio => Engine?.PlaybackInfo.SelectedAudioStream,
            MediaStreamKind.Subtitle => Engine?.PlaybackInfo.SelectedSubtitleStream,
            _ => null,
        };
    }

    public void AddSubtitleTracks()
    {
        CoreEngine? engine = Engine;
        if (engine is null)
        {
            return;
        }

        CoroutineUtils.Run(async () =>
        {
            IReadOnlyList<StorageFile> files = await FilePickerUtils.PickFiles(WindowId, AppInfoProvider.SupportedSubtitleExtensions);
            if (files.Count == 0)
            {
                return;
            }

            OperationResult added = await SubtitleSourceLoader.AddAndSelect(engine, Media, [.. files.Select(file => file.Path)]);
            if (!added.IsSuccessful)
            {
                PromptInfo?.Invoke(added.Message);
            }
        });
    }

    public MediaSource? SourceOf(MediaStreamInfo? stream)
    {
        if (stream is null)
        {
            return null;
        }

        foreach (MediaSource source in Sources)
        {
            if (source.Info.Streams.Any(candidate => candidate.Equals(stream)))
            {
                return source;
            }
        }

        return null;
    }

    public void SelectTrack(MediaStreamKind kind, MediaStreamInfo? stream)
    {
        CoreEngine? engine = Engine;
        if (engine is null)
        {
            return;
        }

        CoroutineUtils.Run(async () =>
        {
            OperationResult selected = await engine.SelectStream(kind, stream);

            if (selected.IsSuccessful)
            {
                selected = await engine.Reconcile();
            }

            if (!selected.IsSuccessful)
            {
                PromptInfo?.Invoke(selected.Message);
                return;
            }

            TrackSelection.Save(Media, kind, engine.PlaybackInfo.Sources, SourceOf(stream), stream);
        });
    }

    private static IReadOnlyList<MediaStreamInfo> StreamsOf(MediaSource source, MediaStreamKind kind)
    {
        return kind switch
        {
            MediaStreamKind.Video => source.Info.VideoStreams,
            MediaStreamKind.Audio => source.Info.AudioStreams,
            MediaStreamKind.Subtitle => source.Info.SubtitleStreams,
            _ => [],
        };
    }
}
