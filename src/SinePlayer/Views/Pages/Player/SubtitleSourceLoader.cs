// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using SinePlayer.Common.Playback.Core;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Pipeline;
using SinePlayer.Data.Models.Media;

namespace SinePlayer.Views.Pages.Player;

internal static class SubtitleSourceLoader
{
    private const string TAG = nameof(SubtitleSourceLoader);

    public static async Task<OperationResult> AddAndSelect(PlayerSession session, PlayerView view, MediaModel media, IReadOnlyList<string> paths)
    {
        List<SubtitleStreamInfo> chosen = [];
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

            chosen.Add(stream);
        }

        if (chosen.Count == 0)
        {
            return OperationResultExt.Failure(TAG, "None of the chosen files holds a subtitle track.");
        }

        if (!TrackSelection.AllowMultiSubtitleTracks(media))
        {
            SubtitleStreamInfo single = chosen[^1];
            chosen = [single];

            foreach (ISubtitleChain chain in view.SubtitleChains)
            {
                if (chain.SelectedStream is not { } previous || previous.Equals(single))
                {
                    continue;
                }

                OperationResult removed = await session.RemoveSubtitleStream(view, previous);
                if (!removed.IsSuccessful)
                {
                    return removed;
                }
            }
        }

        foreach (SubtitleStreamInfo stream in chosen)
        {
            OperationResult selected = await session.AddSubtitleStream(view, stream);
            if (!selected.IsSuccessful)
            {
                return selected;
            }
        }

        OperationResult<ReconcileResult> reconciled = await session.Reconcile();
        if (!reconciled.IsSuccessful)
        {
            return OperationResultExt.Failure(reconciled);
        }

        OperationResult? built = reconciled.Result.Subtitle(view);
        if (built is { IsSuccessful: false })
        {
            return OperationResultExt.Failure(built);
        }

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
