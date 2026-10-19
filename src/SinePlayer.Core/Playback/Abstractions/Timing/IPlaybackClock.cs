// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Timing;

/// <summary>
/// The authoritative playback position. Exactly one clock drives a pipeline; it is normally the
/// audio renderer's device clock, and switching the audio renderer hands the role to the new
/// instance without any other stage changing.
/// </summary>
public interface IPlaybackClock
{
    TimeSpan Position { get; }

    bool IsRunning { get; }

    double Rate { get; }

    void Start();

    void Pause();

    /// <summary>Jumps to a new position after the pipeline has flushed its buffered data.</summary>
    void SetPosition(TimeSpan position);

    void SetRate(double rate);
}
