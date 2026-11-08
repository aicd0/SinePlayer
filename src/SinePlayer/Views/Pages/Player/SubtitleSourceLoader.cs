// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using SinePlayer.Common.Playback.Core;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Pipeline;
using SinePlayer.Data.Models.Media;

namespace SinePlayer.Views.Pages.Player;

internal static class SubtitleSourceLoader
{
    private const string TAG = nameof(SubtitleSourceLoader);

    public static async Task<OperationResult> AddAndSelect(PlayerSession session, MediaModel media, IReadOnlyList<string> paths)
    {
        (MediaSource Source, SubtitleStreamInfo Stream)? chosen = null;
        foreach (string path in paths)
        {
            MediaSource? source = await AddSource(session, path);
            if (source is null)
            {
                continue;
            }

            SubtitleStreamInfo? stream = source.Info.SubtitleStreams.FirstOrDefault();
            if (stream is null)
            {
                Logger.W(TAG, $"'{path}' holds no subtitle track.");
                continue;
            }

            chosen = (source, stream);
        }

        if (chosen is not { } selection)
        {
            return OperationResultExt.Failure(TAG, "None of the chosen files holds a subtitle track.");
        }

        OperationResult selected = await session.SelectStream(MediaStreamKind.Subtitle, selection.Stream);
        if (!selected.IsSuccessful)
        {
            return selected;
        }

        OperationResult<ReconcileResult> reconciled = await session.Reconcile();
        if (!reconciled.IsSuccessful)
        {
            return OperationResultExt.Failure(reconciled);
        }

        OperationResult? built = session.ResultOf(reconciled.Result, MediaStreamKind.Subtitle);
        if (built is { IsSuccessful: false })
        {
            return OperationResultExt.Failure(built);
        }

        TrackSelection.Save(media, MediaStreamKind.Subtitle, session.PlaybackInfo.Sources, selection.Source, selection.Stream);
        return OperationResult.Success();
    }

    private static async Task<MediaSource?> AddSource(PlayerSession session, string path)
    {
        OperationResult<MediaSource> added = await session.AddSource(path);
        if (!added.IsSuccessful)
        {
            Logger.W(TAG, $"'{path}' could not be added: {added.Message}");
            return null;
        }

        return added.Result;
    }
}
