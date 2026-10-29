// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The read-ahead is a length of time, so the queue has to weigh what it holds: sixty-four packets are two and a
/// half seconds of a 24 fps film and half a second of a 120 fps one, and only one of those is a buffer that can ride
/// out a read which takes seconds.
/// </summary>
[TestFixture]
public class PacketQueueTests
{
    [Test]
    public void AddsUpTheMediaItHolds()
    {
        PacketQueue queue = new();

        queue.Enqueue(Entry(Packet(TimeSpan.FromSeconds(1)), TimeSpan.FromSeconds(1)));
        queue.Enqueue(Entry(Packet(TimeSpan.FromSeconds(1)), TimeSpan.FromSeconds(1)));
        queue.Enqueue(Entry(Packet(TimeSpan.FromSeconds(1)), TimeSpan.FromSeconds(1)));

        Assert.That(queue.Count, Is.EqualTo(3));
        Assert.That(queue.Queued, Is.EqualTo(TimeSpan.FromSeconds(3)));
    }

    [Test]
    public void GivesBackTheMediaOfWhatItHandsOut()
    {
        PacketQueue queue = new();
        queue.Enqueue(Entry(Packet(TimeSpan.FromSeconds(2)), TimeSpan.FromSeconds(2)));
        queue.Enqueue(Entry(Packet(TimeSpan.FromSeconds(1)), TimeSpan.FromSeconds(1)));

        Assert.That(queue.TryDequeue(out _), Is.True);

        Assert.That(queue.Count, Is.EqualTo(1));
        Assert.That(queue.Queued, Is.EqualTo(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public void CountsNothingForTheEndOfTheStream()
    {
        PacketQueue queue = new();

        queue.Enqueue(new PacketEntry(null, default, 0, TimeSpan.Zero));

        Assert.That(queue.Count, Is.EqualTo(1), "the end of the stream is still an entry to be read");
        Assert.That(queue.Queued, Is.EqualTo(TimeSpan.Zero), "but it is no media to play");
    }

    [Test]
    public void ForgetsEverythingItHeldWhenCleared()
    {
        PacketQueue queue = new();
        queue.Enqueue(Entry(Packet(TimeSpan.FromSeconds(4)), TimeSpan.FromSeconds(4)));

        queue.Clear();

        Assert.That(queue.IsEmpty, Is.True);
        Assert.That(queue.Count, Is.Zero);
        Assert.That(queue.Queued, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public void HandsPacketsOutInTheOrderTheyWereRead()
    {
        PacketQueue queue = new();
        queue.Enqueue(Entry(Packet(TimeSpan.FromSeconds(1)), TimeSpan.FromSeconds(1), 1));
        queue.Enqueue(Entry(Packet(TimeSpan.FromSeconds(1)), TimeSpan.FromSeconds(1), 2));

        Assert.That(queue.TryPeek(out PacketEntry head), Is.True);
        Assert.That(head.Epoch, Is.EqualTo(1));
        Assert.That(queue.TryDequeue(out PacketEntry first), Is.True);
        Assert.That(first.Epoch, Is.EqualTo(1));
        Assert.That(queue.TryDequeue(out PacketEntry second), Is.True);
        Assert.That(second.Epoch, Is.EqualTo(2));
    }

    private static PacketEntry Entry(MediaPacket packet, TimeSpan span, long epoch = 0)
    {
        return new PacketEntry(packet, default, epoch, span);
    }

    private static MediaPacket Packet(TimeSpan duration)
    {
        return new MediaPacket
        {
            StreamIndex = 0,
            Timestamp = TimeSpan.Zero,
            Duration = duration,
            IsKeyFrame = false,
            Buffer = new FakeBuffer(),
        };
    }

    private sealed class FakeBuffer : IMediaBuffer
    {
        public long Length => 0;

        public void Dispose()
        {
        }
    }
}
