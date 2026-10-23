// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Utils;

internal sealed class SeqLock<T>(T initial)
{
    private const int Attempts = 4;

    private readonly Lock _lock = new();
    private T _value = initial;
    private int _sequence;

    public T Read()
    {
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            int sequence = Volatile.Read(ref _sequence);
            if ((sequence & 1) != 0)
            {
                Thread.Yield();
                continue;
            }

            T value = _value;
            if (Volatile.Read(ref _sequence) == sequence)
            {
                return value;
            }
        }

        lock (_lock)
        {
            return _value;
        }
    }

    public void Write(Func<T, T> update)
    {
        lock (_lock)
        {
            Volatile.Write(ref _sequence, _sequence + 1);
            try
            {
                _value = update(_value);
            }
            finally
            {
                Volatile.Write(ref _sequence, _sequence + 1);
            }
        }
    }
}
