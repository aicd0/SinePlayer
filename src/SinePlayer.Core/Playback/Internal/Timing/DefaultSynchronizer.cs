// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;

using SinePlayer.Core.Playback.Abstractions.Timing;

namespace SinePlayer.Core.Playback.Internal.Timing;

/// <summary>
/// Decides what to do with a frame by how far it is from the clock: early frames are offered again
/// later, late ones are given up on rather than shown behind the sound.
/// <para>
/// Both tolerances are deliberately small. A frame held too long shows up as stutter and a frame
/// dropped too eagerly as judder, so the window is a compromise between the two rather than a
/// correctness boundary.
/// </para>
/// </summary>
public sealed class DefaultSynchronizer : ISynchronizer
{
    private static readonly TimeSpan DefaultEarlyTolerance = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan DefaultLateTolerance = TimeSpan.FromMilliseconds(40);

    private readonly TimeSpan _earlyTolerance;
    private readonly TimeSpan _lateTolerance;

    public DefaultSynchronizer()
        : this(DefaultEarlyTolerance, DefaultLateTolerance)
    {
    }

    /// <param name="earlyTolerance">How far ahead of the clock a frame may be before it waits.</param>
    /// <param name="lateTolerance">How far behind the clock a frame may be before it is dropped.</param>
    public DefaultSynchronizer(TimeSpan earlyTolerance, TimeSpan lateTolerance)
    {
        _earlyTolerance = earlyTolerance;
        _lateTolerance = lateTolerance;
    }

    public FrameSyncAction Evaluate(TimeSpan frameTimestamp, TimeSpan clockPosition)
    {
        TimeSpan offset = frameTimestamp - clockPosition;
        if (offset > _earlyTolerance)
        {
            return FrameSyncAction.Wait;
        }

        return offset < _lateTolerance.Negate() ? FrameSyncAction.Drop : FrameSyncAction.Present;
    }
}
