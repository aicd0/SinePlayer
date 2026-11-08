// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;
using System.Linq;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Common.Playback.Core;

internal static class ReconcileResultExtensions
{
    public static OperationResult? Video(this ReconcileResult result, PlayerView view)
    {
        return view.VideoChain is { } chain ? result.VideoResults[chain] : null;
    }

    public static OperationResult? Audio(this ReconcileResult result)
    {
        if (result.AudioResults.Count == 0)
        {
            return null;
        }

        List<OperationResult> failures = [.. result.AudioResults.Values.Where(it => !it.IsSuccessful)];
        if (failures.Count == 0)
        {
            return OperationResult.Success();
        }

        return failures[0];
    }

    public static OperationResult? Subtitle(this ReconcileResult result, PlayerView view)
    {
        return view.SubtitleChain is { } chain ? result.SubtitleResults[chain] : null;
    }
}
