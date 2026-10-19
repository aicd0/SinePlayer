// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Decode;

/// <summary>
/// One subtitle event: either text or a bitmap. Subtitle rendering is deferred, so this type only
/// needs to carry enough for a future renderer.
/// </summary>
public sealed class SubtitleCue : IDisposable
{
    public required TimeSpan Start { get; init; }

    public required TimeSpan End { get; init; }

    public string? Text { get; init; }

    public IMediaBuffer? Image { get; init; }

    public void Dispose()
    {
        Image?.Dispose();
    }
}
