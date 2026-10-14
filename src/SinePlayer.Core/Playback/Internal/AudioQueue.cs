// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;

using SinePlayer.Core.Playback.Abstractions.Decode;

namespace SinePlayer.Core.Playback.Internal;

/// <summary>
/// A queue of decoded audio between the reader and the device.
/// <para>
/// Like the packet queue it has no maximum: the reader that would be stopped by a full queue is the reader
/// that produces everything else too, and a full audio queue is a picture that freezes because the device
/// was slow. The reader paces itself by its own minimums, and the consumer waits only when there is nothing
/// to play — which is reported by halting the clock rather than by blocking anybody.
/// </para>
/// </summary>
internal sealed class AudioQueue : IDisposable
{
    private readonly object _lock = new();
    private readonly Queue<RawAudioBuffer> _buffers = [];
    private readonly EpochGate _epochs;

    private long _queuedSamples;
    private bool _closed;

    /// <param name="epochs">Says when the audio being handed over stopped belonging to the position.</param>
    internal AudioQueue(EpochGate epochs)
    {
        _epochs = epochs;
    }

    internal long QueuedSamples
    {
        get
        {
            lock (_lock)
            {
                return _queuedSamples;
            }
        }
    }

    /// <summary>
    /// Adds a buffer. Never waits. Returns false once the queue is closed, or once the given epoch belongs
    /// to a position playback has left.
    /// </summary>
    internal bool TryEnqueue(RawAudioBuffer buffer, long epoch)
    {
        lock (_lock)
        {
            if (_closed || _epochs.Current != epoch)
            {
                return false;
            }

            _buffers.Enqueue(buffer);
            _queuedSamples += GetWeight(buffer);
            Monitor.PulseAll(_lock);
            return true;
        }
    }

    /// <summary>Removes the oldest buffer, waiting up to the given time for one to arrive.</summary>
    internal bool TryDequeue(out RawAudioBuffer? buffer, int timeoutMilliseconds)
    {
        buffer = null;

        lock (_lock)
        {
            if (_buffers.Count == 0 && !_closed)
            {
                Monitor.Wait(_lock, timeoutMilliseconds);
            }

            if (_buffers.Count == 0)
            {
                return false;
            }

            buffer = _buffers.Dequeue();
            _queuedSamples -= GetWeight(buffer);
            Monitor.PulseAll(_lock);
            return true;
        }
    }

    /// <summary>Drops everything queued, for example after a seek.</summary>
    internal void Clear()
    {
        lock (_lock)
        {
            DisposeBuffers();
            Monitor.PulseAll(_lock);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _closed = true;
            DisposeBuffers();
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>A buffer with no samples still occupies a slot, so the weight is never zero.</summary>
    private static long GetWeight(RawAudioBuffer buffer)
    {
        return Math.Max(1, buffer.SampleCount);
    }

    private void DisposeBuffers()
    {
        while (_buffers.Count > 0)
        {
            _buffers.Dequeue().Dispose();
        }

        _queuedSamples = 0;
    }
}
