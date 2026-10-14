// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Internal;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The queue between reading and decoding has no maximum, and that is what it is for: the reader that this
/// queue would stop is the same cursor that reads the sound, so a full video queue would be a stopped sound.
/// What it does with time — keeping everything it is given, never handing over anything from a position that
/// is gone, and telling the decoder when the file has run out — is what is checked here.
/// </summary>
[TestFixture]
public class PacketQueueTests
{
    [Test]
    public void HandsPacketsOverInOrderWithTheEpochTheyWereReadFor()
    {
        EpochGate epochs = new();
        using PacketQueue queue = new(epochs);
        long epoch = epochs.Current;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queue.TryEnqueue(CreatePacket(0), epoch), Is.True);
            Assert.That(queue.TryEnqueue(CreatePacket(1), epoch), Is.True);
            Assert.That(queue.Count, Is.EqualTo(2));
        }

        Assert.That(queue.TryDequeue(out MediaPacket? first, out long firstEpoch, 0), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.Not.Null);
            Assert.That(first!.StreamIndex, Is.EqualTo(0));
            Assert.That(firstEpoch, Is.EqualTo(epoch), "a packet has to remember where it came from");
        }

        // The one left behind goes back to the epoch it was queued with, not to whatever is current.
        epochs.Advance();
        Assert.That(queue.TryDequeue(out MediaPacket? second, out long secondEpoch, 0), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(second!.StreamIndex, Is.EqualTo(1));
            Assert.That(secondEpoch, Is.EqualTo(epoch));
        }

        first.Dispose();
        second.Dispose();
    }

    [Test]
    public void TakesAsMuchAsItIsGivenRatherThanStoppingTheReader()
    {
        EpochGate epochs = new();
        using PacketQueue queue = new(epochs);
        long epoch = epochs.Current;

        // A decoder that has stopped — because the picture is slow, or because the device is — must not stop
        // the reader: the packets behind the video packet are the sound, and it is read by the same cursor.
        using (Assert.EnterMultipleScope())
        {
            for (int index = 0; index < 1000; index++)
            {
                Assert.That(queue.TryEnqueue(CreatePacket(index % 2), epoch), Is.True, $"packet {index} has to be taken");
            }

            Assert.That(queue.Count, Is.EqualTo(1000), "nothing is thrown away to make room");
        }

        Assert.That(queue.TryDequeue(out MediaPacket? first, out long _, 0), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first!.StreamIndex, Is.Zero, "and they are handed over in the order they were read");
            Assert.That(queue.Count, Is.EqualTo(999));
        }

        first.Dispose();
    }

    [Test]
    public void TakesNothingFromAPositionPlaybackHasLeft()
    {
        EpochGate epochs = new();
        using PacketQueue queue = new(epochs);
        long epoch = epochs.Current;

        epochs.Advance();
        using MediaPacket packet = CreatePacket(0);
        Assert.That(queue.TryEnqueue(packet, epoch), Is.False, "reading on for a position that is gone is wasted work");
        Assert.That(queue.Count, Is.Zero);
    }

    [Test]
    public void TakesNothingMoreOnceTheStreamHasEnded()
    {
        EpochGate epochs = new();
        using PacketQueue queue = new(epochs);
        long epoch = epochs.Current;

        Assert.That(queue.IsCompleted, Is.False);
        queue.Complete();
        Assert.That(queue.IsCompleted, Is.True);

        // The reader says the file has run out while the decoder is still working through the queue: what
        // is queued stays queued, and nothing new is taken.
        using MediaPacket packet = CreatePacket(0);
        Assert.That(queue.TryEnqueue(packet, epoch), Is.False);

        // A consumer waiting on an empty completed queue is released rather than left waiting.
        bool dequeued = queue.TryDequeue(out MediaPacket? _, out long _, 5000);
        Assert.That(dequeued, Is.False);
    }

    private static MediaPacket CreatePacket(int streamIndex)
    {
        return new MediaPacket
        {
            StreamIndex = streamIndex,
            Timestamp = TimeSpan.Zero,
            Duration = TimeSpan.Zero,
            IsKeyFrame = true,
            Buffer = new FakeMediaBuffer(),
        };
    }

    private sealed class FakeMediaBuffer : IMediaBuffer
    {
        public long Length => 0;

        public void Dispose()
        {
        }
    }
}
