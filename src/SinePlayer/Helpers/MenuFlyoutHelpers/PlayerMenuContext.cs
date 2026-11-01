// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using SinePlayer.Common.Actions;
using SinePlayer.Common.Misc;
using SinePlayer.Common.Playback.Core;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Data.Models.Playback;

using Windows.Storage;

namespace SinePlayer.Helpers.MenuFlyoutHelpers;

internal sealed class PlayerMenuContext
{
    private const string TAG = nameof(PlayerMenuContext);

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

            (MediaSource Source, SubtitleStreamInfo Stream)? chosen = null;
            foreach (StorageFile file in files)
            {
                MediaSource? source = await AddSubtitleSource(engine, file);
                if (source is null)
                {
                    continue;
                }

                SubtitleStreamInfo? stream = source.Info.SubtitleStreams.FirstOrDefault();
                if (stream is null)
                {
                    Logger.W(TAG, $"'{file.Path}' holds no subtitle track.");
                    continue;
                }

                chosen = (source, stream);
            }

            if (chosen is not { } selection)
            {
                return;
            }

            OperationResult selected = await engine.SelectStream(MediaStreamKind.Subtitle, selection.Stream);
            if (selected.IsSuccessful)
            {
                selected = await engine.Reconcile();
            }

            if (!selected.IsSuccessful)
            {
                PromptInfo?.Invoke(selected.Message);
                return;
            }

            TrackSelection.Save(Media, MediaStreamKind.Subtitle, engine.PlaybackInfo.Sources, selection.Source, selection.Stream);
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

    private static async Task<MediaSource?> AddSubtitleSource(CoreEngine engine, StorageFile file)
    {
        OperationResult<MediaSource> added = await engine.AddSource(file.Path);
        if (!added.IsSuccessful)
        {
            Logger.W(TAG, $"'{file.Path}' could not be added: {added.Message}");
            return null;
        }

        return added.Result;
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
