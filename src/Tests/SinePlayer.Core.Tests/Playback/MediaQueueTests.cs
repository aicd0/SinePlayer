// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
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
/// untouched, which is what the ends rely on. What the queue *does* carry besides the media is the end of a
/// stream, as an entry of its own: it is handed over in order, it takes no room, it wakes a consumer waiting
/// for one, and it is why an empty queue and a finished one are different observations (see the two tests
/// named for it). There is no clock in the consumer's wait either: an entry or a close releases it, and a
/// clear is not a wake — a consumer has nothing to do about a seek, and being handed "nothing happened" is a
/// result it would have to guess the meaning of.
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
            Assert.That(queue.Enqueue(CreatePacket(0), 7), Is.True);
            Assert.That(queue.Enqueue(CreatePacket(1), 7), Is.True);
            Assert.That(queue.Count, Is.EqualTo(2));
        }

        Assert.That(queue.Dequeue(out MediaPacket? first, out long firstTag, out bool firstIsLast), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstIsLast, Is.False, "media is not the end of a stream");
            Assert.That(first?.StreamIndex, Is.EqualTo(0), "items come back in the order they went in");
            Assert.That(firstTag, Is.EqualTo(7), "an item has to remember where it came from");
        }

        // The tag says what the item belongs to, not what the pipeline is doing now: the queue has no idea
        // whether seven is still current, and it is not its job to have one.
        Assert.That(queue.Dequeue(out MediaPacket? second, out long secondTag, out bool _), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(second?.StreamIndex, Is.EqualTo(1));
            Assert.That(secondTag, Is.EqualTo(7));
        }

        first?.Dispose();
        second?.Dispose();
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
                Assert.That(queue.Enqueue(CreatePacket(index % 2), 0), Is.True, $"item {index} has to be taken");
            }

            Assert.That(queue.Count, Is.EqualTo(1000), "nothing is thrown away to make room");
            Assert.That(queue.Weight, Is.EqualTo(1000), "an item weighs one when the queue is not told otherwise");
        }
    }

    [Test]
    public void WaitsForRoomRatherThanDiscardingWhatTheProducerMade()
    {
        using MediaQueue<RawVideoFrame> queue = new(limit: 1);
        Assert.That(queue.Enqueue(CreateFrame(), 0), Is.True);

        bool? handedOver = null;
        FakeMediaBuffer handed = new();
        Thread producer = new(() => handedOver = queue.Enqueue(CreateFrame(handed), 0))
        {
            IsBackground = true,
        };

        producer.Start();
        Assert.That(producer.Join(TimeSpan.FromMilliseconds(100)), Is.False, "there is no room, so the producer waits");

        Assert.That(queue.Dequeue(out RawVideoFrame? frame, out long _, out bool _), Is.True);
        frame?.Dispose();

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
        Assert.That(queue.Enqueue(CreateFrame(), 0), Is.True);

        bool? handedOver = null;
        Thread producer = new(() => handedOver = queue.Enqueue(CreateFrame(), 0))
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

        Assert.That(queue.Enqueue(CreateBuffer(samples: 100), 0), Is.True);
        Assert.That(queue.Enqueue(CreateBuffer(samples: 250), 0), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queue.Count, Is.EqualTo(2), "the producer counts items");
            Assert.That(queue.Weight, Is.EqualTo(350), "and paces itself by what they are worth");
        }

        Assert.That(queue.Dequeue(out RawAudioBuffer? buffer, out long _, out bool _), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queue.Weight, Is.EqualTo(250), "taking an item takes its weight with it");
        }

        buffer?.Dispose();
    }

    [Test]
    public void LooksAtTheOldestItemWithoutTakingIt()
    {
        using MediaQueue<RawVideoFrame> queue = new(limit: 2);

        Assert.That(queue.TryPeek(out RawVideoFrame? _, out long _), Is.False, "there is nothing to look at");

        FakeMediaBuffer oldest = new();
        Assert.That(queue.Enqueue(CreateFrame(oldest), 3), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queue.TryPeek(out RawVideoFrame? peeked, out long peekedTag), Is.True);
            Assert.That(peeked?.Buffer, Is.SameAs(oldest), "the oldest item is the one the consumer would take first");
            Assert.That(peekedTag, Is.EqualTo(3));
            Assert.That(queue.Count, Is.EqualTo(1), "looking costs nothing");
        }
    }

    [Test]
    public void DisposesWhatItStillHoldsWhenItIsClosed()
    {
        FakeMediaBuffer held = new();
        using MediaQueue<RawVideoFrame> queue = new(limit: 2);
        Assert.That(queue.Enqueue(CreateFrame(held), 0), Is.True);

        queue.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queue.IsClosed, Is.True);
            Assert.That(held.IsDisposed, Is.True, "what the queue took over is the queue's to release");
            Assert.That(queue.Count, Is.Zero);
            Assert.That(queue.Enqueue(CreateFrame(), 0), Is.False, "a closed queue takes nothing more");
        }

        // A closed queue answers at once rather than waiting for something that will never come, which is
        // what keeps a stopping pipeline's joins from waiting on a consumer.
        Assert.That(queue.Dequeue(out RawVideoFrame? _, out long _, out bool _), Is.False, "a closed queue hands nothing over");
    }

    /// <summary>
    /// A consumer waiting on an empty queue when the queue is closed — a stop — is released rather than left
    /// waiting for media that is not coming. It is the consumer's half of the release a producer gets when
    /// the queue closes, and the stop's joins depend on it: a thread parked in a queue with no clock behind
    /// it needs a word, and the close is that word.
    /// </summary>
    [Test]
    public void ReleasesAConsumerWaitingForAnEntryWhenTheQueueIsClosed()
    {
        using MediaQueue<RawVideoFrame> queue = new();
        bool dequeued = true;

        Thread consumer = new(() => dequeued = queue.Dequeue(out RawVideoFrame? _, out long _, out bool _))
        {
            IsBackground = true,
        };

        consumer.Start();
        Assert.That(consumer.Join(TimeSpan.FromMilliseconds(100)), Is.False, "there is nothing to wait for yet");

        queue.Dispose();

        Assert.That(consumer.Join(TimeSpan.FromSeconds(2)), Is.True, "closing the queue has to release the consumer");
        Assert.That(dequeued, Is.False, "and what it is told is that there is nothing more");
    }

    /// <summary>
    /// A queue that is only cleared — a seek — does not wake its consumer to tell it that nothing happened:
    /// there is no entry to hand over, and a seek is not the consumer's business. What releases it is an
    /// entry or a close, and nothing else.
    /// </summary>
    [Test]
    public void DoesNotWakeAConsumerToTellItNothingHappened()
    {
        using MediaQueue<RawVideoFrame> queue = new();
        Assert.That(queue.Enqueue(CreateFrame(), 1), Is.True);

        int taken = 0;
        bool arrivedAtTheEnd = false;
        Thread consumer = new(() =>
        {
            // The frame first, and then the wait this test is about.
            if (queue.Dequeue(out RawVideoFrame? frame, out long _, out bool _))
            {
                taken++;
                frame?.Dispose();
            }

            if (queue.Dequeue(out RawVideoFrame? _, out long _, out bool isLast))
            {
                arrivedAtTheEnd = isLast;
            }
        })
        {
            IsBackground = true,
        };

        consumer.Start();
        Assert.That(consumer.Join(TimeSpan.FromSeconds(2)), Is.False, "the frame is handed over and the consumer waits for the next entry");

        queue.Clear();
        Assert.That(consumer.Join(TimeSpan.FromMilliseconds(100)), Is.False, "a clear is not a wake: there was nothing to hand over");

        queue.EndStream(2);
        Assert.That(consumer.Join(TimeSpan.FromSeconds(2)), Is.True, "the end of the stream releases it");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(taken, Is.EqualTo(1));
            Assert.That(arrivedAtTheEnd, Is.True);
        }
    }

    /// <summary>
    /// The end of a stream, which is an entry like any other and is why a consumer does not have to tell an
    /// empty queue that is finished from one that is merely behind: it is handed the end after everything
    /// enqueued before it, and a producer can say it while the queue is full — which is when a consumer
    /// waiting for room to be made is most likely to be the one waiting for it.
    /// </summary>
    [Test]
    public void HandsTheEndOfTheStreamOverAfterEverythingQueuedBeforeIt()
    {
        using MediaQueue<RawVideoFrame> queue = new(limit: 1);
        Assert.That(queue.Enqueue(CreateFrame(), 5), Is.True);

        queue.EndStream(5);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queue.Count, Is.EqualTo(1), "the end of a stream is not media and is not counted");
            Assert.That(queue.Weight, Is.EqualTo(1), "and it does not weigh anything either");
        }

        Assert.That(queue.Dequeue(out RawVideoFrame? frame, out long frameTag, out bool frameIsLast), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(frameIsLast, Is.False, "the media enqueued before it comes first");
            Assert.That(frameTag, Is.EqualTo(5));
        }

        frame?.Dispose();

        Assert.That(queue.Dequeue(out RawVideoFrame? end, out long endTag, out bool endIsLast), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(endIsLast, Is.True, "which is how a consumer learns the stream ended");
            Assert.That(end, Is.Null, "the end of a stream is not media");
            Assert.That(endTag, Is.EqualTo(5), "and which position's stream it was");
            Assert.That(queue.Count, Is.Zero);
            Assert.That(queue.Weight, Is.Zero);
        }

        Assert.That(queue.TryPeek(out RawVideoFrame? _, out long _), Is.False, "the end of a stream is handed over once");
    }

    /// <summary>
    /// A consumer waiting on an empty queue when the stream ends is released by the end itself rather than
    /// left waiting for a timeout that was meant for media which is not coming: that a queue is empty and
    /// that a stream is over are different things, and the entry is what tells them apart.
    /// </summary>
    [Test]
    public void ReleasesAConsumerWaitingForAnEntryWhenTheStreamEnds()
    {
        using MediaQueue<RawVideoFrame> queue = new();
        var watch = Stopwatch.StartNew();
        bool dequeued = false;
        bool arrivedAtTheEnd = false;

        Thread consumer = new(() =>
        {
            dequeued = queue.Dequeue(out RawVideoFrame? _, out long _, out bool isLast);
            arrivedAtTheEnd = isLast;
        })
        {
            IsBackground = true,
        };

        consumer.Start();
        Assert.That(consumer.Join(TimeSpan.FromMilliseconds(100)), Is.False, "there is nothing to wait for yet");

        queue.EndStream(9);

        Assert.That(consumer.Join(TimeSpan.FromSeconds(2)), Is.True, "ending the stream has to release the consumer");
        watch.Stop();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dequeued, Is.True, "the end of a stream is an entry, and a consumer waiting for one gets it");
            Assert.That(arrivedAtTheEnd, Is.True, "which is what tells it nothing more is coming");
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(2000), "and it is told rather than waited out");
            Assert.That(queue.TryPeek(out RawVideoFrame? _, out long _), Is.False, "the entry was taken, not left behind");
        }
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
