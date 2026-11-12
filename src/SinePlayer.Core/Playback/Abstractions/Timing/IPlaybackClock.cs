// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Timing;

public interface IPlaybackClock
{
    TimeSpan Position { get; }

    bool IsRunning { get; }

    void Start();

    void Pause();

    void SetPosition(TimeSpan position);
}
