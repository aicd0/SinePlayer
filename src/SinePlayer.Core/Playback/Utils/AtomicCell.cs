// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Utils;

internal sealed class AtomicCell<T>(T initial)
{
    private ValueBox _value = new(initial);

    public T Read()
    {
        return Volatile.Read(ref _value).Value;
    }

    public void Write(Func<T, T> update)
    {
        while (true)
        {
            ValueBox oldBox = Volatile.Read(ref _value);
            T oldValue = oldBox.Value;
            T newValue = update(oldValue);
            if (Interlocked.CompareExchange(ref _value, new ValueBox(newValue), oldBox) == oldBox)
            {
                break;
            }
        }
    }

    private class ValueBox(T value)
    {
        public readonly T Value = value;
    }
}
