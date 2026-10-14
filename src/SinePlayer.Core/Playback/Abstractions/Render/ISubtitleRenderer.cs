// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;

using SinePlayer.Core.Playback.Abstractions.Decode;

namespace SinePlayer.Core.Playback.Abstractions.Render;

/// <summary>
/// Draws subtitles over the video output. Not implemented yet; the contract exists so the pipeline
/// already carries a slot for it and enabling subtitles later is additive.
/// </summary>
public interface ISubtitleRenderer : IDisposable
{
    string Name { get; }

    PlaybackResult Configure();

    /// <summary>Shows a cue, or clears the current one when <paramref name="cue"/> is null.</summary>
    PlaybackResult Render(SubtitleCue? cue);
}
