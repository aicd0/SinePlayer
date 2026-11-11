// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;

namespace SinePlayer.Core.Playback.Pipeline;

public sealed class ReconcileResult
{
    public required IReadOnlyDictionary<IPlaybackChain, OperationResult> Results { get; init; }

    public OperationResult this[IPlaybackChain chain] => Results[chain];
}
