// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Selection;

namespace SinePlayer.Core.Playback.Pipeline;

public interface IAudioChain : IPlaybackChain<AudioStreamInfo>
{
    AudioComponentResolver Components { get; }

    AudioFilterChain Filters { get; }

    long Underruns { get; }
}
