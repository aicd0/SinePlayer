// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Core.Playback.Abstractions.Timing;

/// <summary>
/// Decides what to do with a frame that does not line up with the clock. Keeping this decision in a
/// single swappable place is what makes audio-master and video-master playback behave consistently,
/// including across renderer switches.
/// </summary>
public interface ISynchronizer
{
    FrameSyncAction Evaluate(TimeSpan frameTimestamp, TimeSpan clockPosition);
}
