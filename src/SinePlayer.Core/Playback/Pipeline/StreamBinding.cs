// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Pipeline;

internal readonly record struct StreamBinding<T>(MediaSource? Source, T? Stream) where T : MediaStreamInfo
{
    public bool Accepts(MediaSource source, int streamIndex)
    {
        return Stream is not null && ReferenceEquals(Source, source) && Stream.Index == streamIndex;
    }
}
