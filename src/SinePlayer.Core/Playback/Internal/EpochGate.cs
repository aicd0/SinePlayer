// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Threading;

namespace SinePlayer.Core.Playback.Internal;

/// <summary>
/// Counts the times playback moved to a different position. Work carries the count it was started
/// under, so anything produced for the old position can be recognised and given up on instead of being
/// played as if it belonged to the new one.
/// <para>
/// A counter rather than a lock on purpose: a seek has to be able to say "everything already in flight
/// is worthless" without waiting for whatever that work is doing, including work that is waiting for
/// room in a queue the seek is about to empty.
/// </para>
/// </summary>
internal sealed class EpochGate
{
    private long _epoch;

    /// <summary>The count new work should carry.</summary>
    internal long Current => Volatile.Read(ref _epoch);

    /// <summary>Marks everything already in flight as belonging to where playback used to be.</summary>
    internal long Advance()
    {
        return Interlocked.Increment(ref _epoch);
    }
}
