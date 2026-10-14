// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;

using SinePlayer.Core.Playback.Abstractions.Decode;

namespace SinePlayer.Core.Playback.Internal;

/// <summary>
/// A bounded queue of decoded frames between the decoder and the picture.
/// <para>
/// This is the one queue that waits for room, because it is the one queue whose consumer is the picture and
/// the picture is the only thing that follows the clock. A decoder stopped here is a decoder waiting for a
/// frame to be shown — which used to be a circle, because the reader behind it was stopped by full queues
/// too. The queues behind this one were made open for exactly that reason: the reader is never stopped by
/// the picture, so waiting here costs the sound nothing. What it costs is video, and by design: nothing is
/// discarded, so the decode simply stops until the picture has room again.
/// </para>
/// <para>
/// The wait is released by a seek, which writes frames off rather than keeping them: a producer waiting to
/// hand over work for a position playback has left must not hold up the seek that is emptying this queue.
/// </para>
/// </summary>
internal sealed class FrameQueue : IDisposable
{
    private readonly object _lock = new();
    private readonly Queue<(long Epoch, RawVideoFrame Frame)> _frames = [];
    private readonly int _limit;
    private readonly EpochGate _epochs;

    private bool _closed;

    /// <param name="limit">How many frames may be waiting before the decoder has to pause.</param>
    /// <param name="epochs">Says when the frames being handed over stopped belonging to the position.</param>
    internal FrameQueue(int limit, EpochGate epochs)
    {
        _limit = Math.Max(1, limit);
        _epochs = epochs;
    }

    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _frames.Count;
            }
        }
    }

    /// <summary>
    /// Adds a frame, waiting until there is room. Returns false once the queue is closed, or once the
    /// given epoch belongs to a position playback has left, in which case the caller still owns the frame.
    /// </summary>
    internal bool TryEnqueue(RawVideoFrame frame, long epoch)
    {
        lock (_lock)
        {
            while (!_closed && _frames.Count >= _limit && _epochs.Current == epoch)
            {
                Monitor.Wait(_lock, 10);
            }

            if (_closed || _epochs.Current != epoch)
            {
                return false;
            }

            // The epoch goes in with the frame rather than being read again on the way out: a seek can
            // land between one of those and the other, and what was queued for the old position must not
            // be taken for the new one.
            _frames.Enqueue((epoch, frame));
            Monitor.PulseAll(_lock);
            return true;
        }
    }

    /// <summary>
    /// Looks at the oldest frame without taking it, which is how the presentation thread decides whether a
    /// frame it is holding is the closest one it could show.
    /// </summary>
    internal bool TryPeek(out RawVideoFrame? frame, out long epoch)
    {
        frame = null;
        epoch = 0;

        lock (_lock)
        {
            if (_frames.Count == 0)
            {
                return false;
            }

            (long queued, RawVideoFrame peeked) = _frames.Peek();
            frame = peeked;
            epoch = queued;
            return true;
        }
    }

    /// <summary>Removes the oldest frame, waiting up to the given time for one to arrive.</summary>
    internal bool TryDequeue(out RawVideoFrame? frame, out long epoch, int timeoutMilliseconds)
    {
        frame = null;
        epoch = 0;

        lock (_lock)
        {
            if (_frames.Count == 0 && !_closed)
            {
                Monitor.Wait(_lock, timeoutMilliseconds);
            }

            if (_frames.Count == 0)
            {
                return false;
            }

            (long queued, RawVideoFrame taken) = _frames.Dequeue();
            frame = taken;
            epoch = queued;
            Monitor.PulseAll(_lock);
            return true;
        }
    }

    /// <summary>Drops everything queued, for example after a seek.</summary>
    internal void Clear()
    {
        lock (_lock)
        {
            DisposeFrames();
            Monitor.PulseAll(_lock);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _closed = true;
            DisposeFrames();
            Monitor.PulseAll(_lock);
        }
    }

    private void DisposeFrames()
    {
        while (_frames.Count > 0)
        {
            _frames.Dequeue().Frame.Dispose();
        }
    }
}
