// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;

using SinePlayer.Common.Actions;
using SinePlayer.Common.ErrorHandling;
using SinePlayer.Common.Playback.Core;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Data.Models.Playback;

namespace SinePlayer.Helpers.MenuFlyoutHelpers;

internal sealed class PlayerMenuContext
{
    public required ActionHandler ActionHandler { get; init; }

    public required MediaModel Media { get; init; }

    public required PlayerSettingsModel Settings { get; init; }

    public required Action<Action<PlayerSettingsModel>> UpdateSettings { get; init; }

    public PlayQueueModel.Builder? PlayQueue { get; init; }

    public PlaybackModel.Builder? Playback { get; init; }

    public CoreEngine? Engine { get; init; }

    public Action<string>? PromptInfo { get; init; }

    public IReadOnlyList<MediaStreamInfo>? Tracks(MediaStreamKind kind)
    {
        MediaInfo? info = Engine?.PlaybackInfo.Media;
        return kind switch
        {
            MediaStreamKind.Video => info?.VideoStreams,
            MediaStreamKind.Audio => info?.AudioStreams,
            _ => null,
        };
    }

    public MediaStreamInfo? SelectedTrack(MediaStreamKind kind)
    {
        return kind switch
        {
            MediaStreamKind.Video => Engine?.PlaybackInfo.SelectedVideoStream,
            MediaStreamKind.Audio => Engine?.PlaybackInfo.SelectedAudioStream,
            _ => null,
        };
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
            ErrorResult selected = await engine.SelectStream(kind, stream);
            if (!selected.IsSuccessful)
            {
                PromptInfo?.Invoke(selected.Message);
                return;
            }

            TrackSelection.Save(Media, kind, stream);
        });
    }
}
