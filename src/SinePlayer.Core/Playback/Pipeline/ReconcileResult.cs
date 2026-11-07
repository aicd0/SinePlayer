// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;

namespace SinePlayer.Core.Playback.Pipeline;

public sealed class ReconcileResult
{
    public required IReadOnlyDictionary<IVideoChain, OperationResult> VideoResults { get; init; }

    public required IReadOnlyDictionary<IAudioChain, OperationResult> AudioResults { get; init; }

    public required IReadOnlyDictionary<ISubtitleChain, OperationResult> SubtitleResults { get; init; }

    public OperationResult Video => Single(VideoResults);

    public OperationResult Audio => Single(AudioResults);

    public OperationResult Subtitle => Single(SubtitleResults);

    public OperationResult For(MediaStreamKind kind)
    {
        return kind switch
        {
            MediaStreamKind.Video => Video,
            MediaStreamKind.Audio => Audio,
            _ => Subtitle,
        };
    }

    private static OperationResult Single<TChain>(IReadOnlyDictionary<TChain, OperationResult> results) where TChain : notnull
    {
        return results.Count switch
        {
            0 => OperationResult.Success(),
            1 => results.Values.First(),
            _ => throw new InvalidOperationException(
                $"There are {results.Count} chains of this kind, so there is no single result. Ask the chain you mean."),
        };
    }
}
