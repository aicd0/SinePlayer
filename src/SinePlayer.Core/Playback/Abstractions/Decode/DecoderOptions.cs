// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Core.Playback.Abstractions.Decode;

/// <summary>
/// Creation settings handed to a decoder that has already been chosen. Which decoder gets chosen is a
/// separate concern handled by component selection, so this type carries only the knobs the chosen
/// implementation needs.
/// </summary>
public sealed class DecoderOptions
{
    public static DecoderOptions Default { get; } = new();

    /// <summary>Threads to decode with; 0 lets the backend decide.</summary>
    public int ThreadCount { get; init; } = 0;
}
