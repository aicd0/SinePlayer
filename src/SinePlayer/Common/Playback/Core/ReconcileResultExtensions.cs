// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;
using System.Linq;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Common.Playback.Core;

internal static class ReconcileResultExtensions
{
    public static OperationResult? Video(this ReconcileResult result, PlayerView view)
    {
        return view.VideoChain is { } chain ? result[chain] : null;
    }

    public static OperationResult? Audio(this ReconcileResult result)
    {
        return Combine(result, MediaStreamKind.Audio);
    }

    public static OperationResult? Subtitle(this ReconcileResult result, PlayerView view)
    {
        return view.SubtitleChains.Count == 0 ? null : Combine(result, view.SubtitleChains);
    }

    private static OperationResult? Combine(ReconcileResult result, MediaStreamKind kind)
    {
        List<OperationResult> failures =
            [.. result.Results.Where(entry => entry.Key.Kind == kind).Select(entry => entry.Value).Where(it => !it.IsSuccessful)];
        return failures.Count == 0 ? OperationResult.Success() : failures[0];
    }

    private static OperationResult? Combine(ReconcileResult result, IReadOnlyList<ISubtitleChain> chains)
    {
        List<OperationResult> failures =
            [.. chains.Select(chain => result.Results.TryGetValue(chain, out OperationResult? single) ? single : null)
                .OfType<OperationResult>()
                .Where(it => !it.IsSuccessful)];
        return failures.Count == 0 ? OperationResult.Success() : failures[0];
    }
}
