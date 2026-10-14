// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;

using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Timing;

namespace SinePlayer.Core.Playback.Abstractions.Render;

/// <summary>
/// Plays decoded audio on an output device. Because the device knows how much audio has actually
/// been heard, this stage also provides the pipeline clock whenever audio is present.
/// </summary>
public interface IAudioRenderer : IDisposable
{
    string Name { get; }

    double Volume { get; set; }

    bool IsMuted { get; set; }

    /// <summary>Clock driven by this renderer, used as the timing master while audio plays.</summary>
    IPlaybackClock Clock { get; }

    /// <summary>Audio queued but not yet played, in samples. Drives back-pressure and seek flushing.</summary>
    long QueuedSampleCount { get; }

    PlaybackResult Configure(AudioStreamInfo stream);

    /// <summary>Queues audio for playback. The caller keeps ownership of the buffer.</summary>
    PlaybackResult Submit(RawAudioBuffer buffer);

    /// <summary>Drops everything queued, for example after a seek.</summary>
    PlaybackResult Flush();
}
