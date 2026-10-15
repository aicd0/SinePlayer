// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Threading;

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Internal;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The queue between two stages of playback, and the three policies it is configured with — which is what
/// the tests here are about, because the mechanism is one class.
/// <para>
/// No limit (packets, audio): a producer never waits, because the producer here is the reader and the reader
/// is the sound — a full video queue that stopped it would make the picture's slowness heard. A limit
/// (frames): the producer waits for room, and that is the one back-pressure the design keeps. A way to weigh
/// items (audio): for a producer paced in samples rather than in items.
/// </para>
/// <para>
/// What the queue does <em>not</em> do is compare the tag it carries against anything: the tag is the
/// pipeline's, and both ends decide what it means. That is why there is no test here for "a seek releases
/// the producer" — a seek is not something this class can see — and why the tag survives a position change
/// untouched, which is what the ends rely on.
/// </para>
/// </summary>
[TestFixture]
public class MediaQueueTests
{
    [Test]
    public void HandsItemsOverInOrderWithTheTagTheyWereQueuedFor()
    {
        using MediaQueue<MediaPacket> queue = new();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queue.TryEnqueue(CreatePacket(0), 7), Is.True);
            Assert.That(queue.TryEnqueue(CreatePacket(1), 7), Is.True);
            Assert.That(queue.Count, Is.EqualTo(2));
        }

        Assert.That(queue.TryDequeue(out MediaPacket? first, out long firstTag, 0), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.Not.Null);
            Assert.That(first!.StreamIndex, Is.EqualTo(0), "items come back in the order they went in");
            Assert.That(firstTag, Is.EqualTo(7), "an item has to remember where it came from");
        }

        // The tag says what the item belongs to, not what the pipeline is doing now: the queue has no idea
        // whether seven is still current, and it is not its job to have one.
        Assert.That(queue.TryDequeue(out MediaPacket? second, out long secondTag, 0), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(second!.StreamIndex, Is.EqualTo(1));
            Assert.That(secondTag, Is.EqualTo(7));
        }

        first.Dispose();
        second.Dispose();
    }

    [Test]
    public void TakesAsMuchAsItIsGivenRatherThanStoppingTheProducer()
    {
        using MediaQueue<MediaPacket> queue = new();

        // A consumer that has stopped — because the picture is slow, or because the device is — must not stop
        // the producer: what is behind the video in the file is the sound, and the same cursor reads it.
        using (Assert.EnterMultipleScope())
        {
            for (int index = 0; index < 1000; index++)
            {
                Assert.That(queue.TryEnqueue(CreatePacket(index % 2), 0), Is.True, $"item {index} has to be taken");
            }

            Assert.That(queue.Count, Is.EqualTo(1000), "nothing is thrown away to make room");
            Assert.That(queue.Weight, Is.EqualTo(1000), "an item weighs one when the queue is not told otherwise");
        }
    }

    [Test]
    public void WaitsForRoomRatherThanDiscardingWhatTheProducerMade()
    {
        using MediaQueue<RawVideoFrame> queue = new(limit: 1);
        Assert.That(queue.TryEnqueue(CreateFrame(), 0), Is.True);

        bool? handedOver = null;
        FakeMediaBuffer handed = new();
        Thread producer = new(() => handedOver = queue.TryEnqueue(CreateFrame(handed), 0))
        {
            IsBackground = true,
        };

        producer.Start();
        Assert.That(producer.Join(TimeSpan.FromMilliseconds(100)), Is.False, "there is no room, so the producer waits");

        Assert.That(queue.TryDequeue(out RawVideoFrame? frame, out long _, 0), Is.True);
        frame!.Dispose();

        Assert.That(producer.Join(TimeSpan.FromSeconds(2)), Is.True, "the consumer taking an item is what makes room");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(handedOver, Is.True);
            Assert.That(handed.IsDisposed, Is.False, "what the producer made is kept, not discarded");
        }
    }

    [Test]
    public void ReleasesAProducerWaitingForRoomWhenTheQueueIsClosed()
    {
        using MediaQueue<RawVideoFrame> queue = new(limit: 1);
        Assert.That(queue.TryEnqueue(CreateFrame(), 0), Is.True);

        bool? handedOver = null;
        Thread producer = new(() => handedOver = queue.TryEnqueue(CreateFrame(), 0))
        {
            IsBackground = true,
        };

        producer.Start();
        Assert.That(producer.Join(TimeSpan.FromMilliseconds(100)), Is.False, "there is no room yet");

        // What a stop does: closing the queue is what releases a producer that would otherwise be waiting for
        // a consumer that is never coming back. A wait with no other exit is a wait that has to have this one.
        queue.Dispose();
        Assert.That(producer.Join(TimeSpan.FromSeconds(2)), Is.True, "closing the queue has to release the producer");
        Assert.That(handedOver, Is.False, "and the item the producer still held is the producer's");
    }

    [Test]
    public void WeighsWhatItHoldsWhenItIsGivenAWayToWeighIt()
    {
        using MediaQueue<RawAudioBuffer> queue = new(weigh: buffer => Math.Max(1, buffer.SampleCount));

        Assert.That(queue.TryEnqueue(CreateBuffer(samples: 100), 0), Is.True);
        Assert.That(queue.TryEnqueue(CreateBuffer(samples: 250), 0), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queue.Count, Is.EqualTo(2), "the producer counts items");
            Assert.That(queue.Weight, Is.EqualTo(350), "and paces itself by what they are worth");
        }

        Assert.That(queue.TryDequeue(out RawAudioBuffer? buffer, out long _, 0), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queue.Weight, Is.EqualTo(250), "taking an item takes its weight with it");
        }

        buffer!.Dispose();
    }

    [Test]
    public void LooksAtTheOldestItemWithoutTakingIt()
    {
        using MediaQueue<RawVideoFrame> queue = new(limit: 2);

        Assert.That(queue.TryPeek(out RawVideoFrame? _, out long _), Is.False, "there is nothing to look at");

        FakeMediaBuffer oldest = new();
        Assert.That(queue.TryEnqueue(CreateFrame(oldest), 3), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queue.TryPeek(out RawVideoFrame? peeked, out long peekedTag), Is.True);
            Assert.That(peeked!.Buffer, Is.SameAs(oldest), "the oldest item is the one the consumer would take first");
            Assert.That(peekedTag, Is.EqualTo(3));
            Assert.That(queue.Count, Is.EqualTo(1), "looking costs nothing");
        }
    }

    [Test]
    public void DisposesWhatItStillHoldsWhenItIsClosed()
    {
        FakeMediaBuffer held = new();
        using MediaQueue<RawVideoFrame> queue = new(limit: 2);
        Assert.That(queue.TryEnqueue(CreateFrame(held), 0), Is.True);

        queue.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queue.IsClosed, Is.True);
            Assert.That(held.IsDisposed, Is.True, "what the queue took over is the queue's to release");
            Assert.That(queue.Count, Is.Zero);
            Assert.That(queue.TryEnqueue(CreateFrame(), 0), Is.False, "a closed queue takes nothing more");
        }

        // A consumer waiting on an empty queue that is then closed is released rather than left waiting.
        Assert.That(queue.TryDequeue(out RawVideoFrame? _, out long _, 5000), Is.False);
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

    private static RawVideoFrame CreateFrame(IMediaBuffer? buffer = null)
    {
        return new RawVideoFrame
        {
            Timestamp = TimeSpan.Zero,
            Duration = TimeSpan.Zero,
            Width = 2,
            Height = 2,
            Format = PixelFormat.Yuv420P,
            Buffer = buffer ?? new FakeMediaBuffer(),
        };
    }

    private static RawAudioBuffer CreateBuffer(int samples)
    {
        return new RawAudioBuffer
        {
            Timestamp = TimeSpan.Zero,
            SampleRate = 44100,
            ChannelCount = 1,
            ChannelLayout = ChannelLayout.Mono,
            SampleFormat = AudioSampleFormat.Float32,
            SampleCount = samples,
            Buffer = new FakeMediaBuffer(),
        };
    }

    private sealed class FakeMediaBuffer : IMediaBuffer
    {
        private bool _disposed;

        public long Length => 0;

        public bool IsDisposed => _disposed;

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
