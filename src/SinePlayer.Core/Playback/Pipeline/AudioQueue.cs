// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Concurrent;

using SinePlayer.Core.Playback.Abstractions.Decode;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class AudioQueue
{
    private readonly ConcurrentQueue<AudioEntry> _entries = new();

    private long _samples;

    internal long Samples => Interlocked.Read(ref _samples);

    internal bool IsEmpty => _entries.IsEmpty;

    internal void Enqueue(AudioEntry entry)
    {
        if (entry.Buffer is not null)
        {
            Interlocked.Add(ref _samples, Weigh(entry.Buffer));
        }

        _entries.Enqueue(entry);
    }

    internal bool TryDequeue(out AudioEntry entry)
    {
        if (!_entries.TryDequeue(out entry))
        {
            return false;
        }

        if (entry.Buffer is not null)
        {
            Interlocked.Add(ref _samples, -Weigh(entry.Buffer));
        }

        return true;
    }

    internal void Clear()
    {
        while (_entries.TryDequeue(out AudioEntry entry))
        {
            entry.Buffer?.Dispose();
        }

        Interlocked.Exchange(ref _samples, 0);
    }

    private static long Weigh(RawAudioBuffer buffer)
    {
        return Math.Max(1, buffer.SampleCount);
    }
}
