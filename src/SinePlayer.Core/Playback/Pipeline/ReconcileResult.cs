// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;

namespace SinePlayer.Core.Playback.Pipeline;

public sealed class ReconcileResult
{
    public required IReadOnlyDictionary<IVideoChain, OperationResult> VideoResults { get; init; }

    public required IReadOnlyDictionary<IAudioChain, OperationResult> AudioResults { get; init; }

    public required IReadOnlyDictionary<ISubtitleChain, OperationResult> SubtitleResults { get; init; }
}
