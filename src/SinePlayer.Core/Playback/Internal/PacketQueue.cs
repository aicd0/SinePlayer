// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;

using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Internal;

/// <summary>
/// A queue of encoded packets: the reader fills it and a decoder empties it. This is what keeps decoding
/// off the reader's thread. While a video decoder is busy with one frame, the reader can carry on reading —
/// and the audio packets interleaved behind that frame in the file are not held up behind work that has
/// nothing to do with them.
/// <para>
/// It has no maximum, and that is the point. A file interleaves its streams, so one cursor reads both: a
/// reader stopped by a full video queue stops producing audio, and the sound then dies whenever the picture
/// is slow. Leaving the queue open removes that coupling — the reader is paced by its own minimums instead
/// of by the decoder — and what bounds it in practice is playback itself, because the reader only reads to
/// keep the queue at its minimum and the minimum advances no faster than the file is played.
/// </para>
/// </summary>
internal sealed class PacketQueue : IDisposable
{
    private readonly object _lock = new();
    private readonly Queue<(long Epoch, MediaPacket Packet)> _packets = [];
    private readonly EpochGate _epochs;

    private bool _closed;
    private bool _completed;

    /// <param name="epochs">Says when the packets being handed over stopped belonging to the position.</param>
    internal PacketQueue(EpochGate epochs)
    {
        _epochs = epochs;
    }

    /// <summary>How many packets are waiting. Used by the reader to decide whether it has read enough.</summary>
    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _packets.Count;
            }
        }
    }

    /// <summary>True once the stream has ended, which is how whoever is decoding learns there is no more.</summary>
    internal bool IsCompleted
    {
        get
        {
            lock (_lock)
            {
                return _completed;
            }
        }
    }

    /// <summary>
    /// Adds a packet. Never waits, because there is no room to wait for. Returns false once the queue is
    /// closed, once the stream has ended, or once the packet belongs to a position playback has left, in
    /// which case the caller still owns it.
    /// </summary>
    internal bool TryEnqueue(MediaPacket packet, long epoch)
    {
        lock (_lock)
        {
            if (_closed || _completed || _epochs.Current != epoch)
            {
                return false;
            }

            // The epoch goes in with the packet, because a seek can land between a dequeue and the decode
            // that follows it, and a decoder that was just flushed must not be handed anything from before
            // the flush — it would have nothing to start from.
            _packets.Enqueue((epoch, packet));
            Monitor.PulseAll(_lock);
            return true;
        }
    }

    /// <summary>Removes the oldest packet, waiting up to the given time for one to arrive.</summary>
    internal bool TryDequeue(out MediaPacket? packet, out long epoch, int timeoutMilliseconds)
    {
        packet = null;
        epoch = 0;

        lock (_lock)
        {
            if (_packets.Count == 0 && !_closed && !_completed)
            {
                Monitor.Wait(_lock, timeoutMilliseconds);
            }

            if (_packets.Count == 0)
            {
                return false;
            }

            (long queued, MediaPacket taken) = _packets.Dequeue();
            packet = taken;
            epoch = queued;
            Monitor.PulseAll(_lock);
            return true;
        }
    }

    /// <summary>Nothing more will be added: the file has run out.</summary>
    internal void Complete()
    {
        lock (_lock)
        {
            _completed = true;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Drops everything queued, for example after a seek. A seek is also what makes the stream
    /// open for business again after the end of the file.</summary>
    internal void Clear()
    {
        lock (_lock)
        {
            DisposePackets();
            _completed = false;
            Monitor.PulseAll(_lock);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _closed = true;
            DisposePackets();
            Monitor.PulseAll(_lock);
        }
    }

    private void DisposePackets()
    {
        while (_packets.Count > 0)
        {
            _packets.Dequeue().Packet.Dispose();
        }
    }
}
