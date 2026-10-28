// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Decode;

public sealed partial class SubtitleCue : IDisposable
{
    public required TimeSpan Start { get; init; }

    public required TimeSpan End { get; init; }

    public string? Text { get; init; }

    public SubtitleImage? Image { get; init; }

    public void Dispose()
    {
        Image?.Dispose();
    }
}
