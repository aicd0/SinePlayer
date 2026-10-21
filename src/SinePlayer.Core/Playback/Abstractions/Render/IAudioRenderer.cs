// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Timing;

namespace SinePlayer.Core.Playback.Abstractions.Render;

public interface IAudioRenderer : IDisposable
{
    string Name { get; }

    long UnderrunCount { get; }

    IPlaybackClock Clock { get; }

    long QueuedSampleCount { get; }

    PlaybackOperationResult Configure(AudioStreamInfo stream);

    IAudioSubmit Submit(RawAudioBuffer buffer);

    PlaybackOperationResult Flush();
}
