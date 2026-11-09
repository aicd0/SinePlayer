// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class ChainTail
{
    private long _epoch = -1;
    private long _ticks = -1;

    public long Ticks => Interlocked.Read(ref _ticks);

    public void Report(long epoch, TimeSpan timestamp)
    {
        if (Volatile.Read(ref _epoch) != epoch)
        {
            Volatile.Write(ref _epoch, epoch);
            Interlocked.Exchange(ref _ticks, -1);
        }

        long ticks = timestamp.Ticks;
        if (ticks > Interlocked.Read(ref _ticks))
        {
            Interlocked.Exchange(ref _ticks, ticks);
        }
    }

    public void Reset()
    {
        Volatile.Write(ref _epoch, -1);
        Interlocked.Exchange(ref _ticks, -1);
    }

    public TimeSpan ReadAhead(TimeSpan position)
    {
        long ticks = Interlocked.Read(ref _ticks);
        if (ticks < 0)
        {
            return TimeSpan.MinValue;
        }

        long ahead = ticks - position.Ticks;
        return ahead > 0 ? TimeSpan.FromTicks(ahead) : TimeSpan.Zero;
    }

    public bool IsSpent(long epoch, TimeSpan position, long furthestTicks)
    {
        long ticks = Volatile.Read(ref _epoch) == epoch ? Interlocked.Read(ref _ticks) : -1;
        if (ticks >= position.Ticks)
        {
            return false;
        }

        return furthestTicks >= position.Ticks;
    }
}
