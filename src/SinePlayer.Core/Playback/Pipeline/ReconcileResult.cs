// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;

namespace SinePlayer.Core.Playback.Pipeline;

public sealed class ReconcileResult
{
    public required OperationResult Video { get; init; }

    public required OperationResult Audio { get; init; }

    public required OperationResult Subtitle { get; init; }

    public OperationResult For(MediaStreamKind kind)
    {
        return kind switch
        {
            MediaStreamKind.Video => Video,
            MediaStreamKind.Audio => Audio,
            _ => Subtitle,
        };
    }
}
