// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Threading;

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Internal;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The frame queue is the one queue that stops its producer, and the only one that may: a decoder waiting
/// here waits for the picture to take a frame, and the picture is the only consumer that follows the clock.
/// What it never does is throw away what the decoder has already produced — a decoder that cannot keep up
/// leaves the picture waiting for frames, which is a wait that shows, rather than frames that disappear.
/// </summary>
[TestFixture]
public class FrameQueueTests
{
    [Test]
    public void HandsFramesOverInOrder()
    {
        EpochGate epochs = new();
        using FrameQueue queue = new(2, epochs);
        long epoch = epochs.Current;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queue.TryEnqueue(CreateFrame(), epoch), Is.True);
            Assert.That(queue.TryEnqueue(CreateFrame(), epoch), Is.True);
            Assert.That(queue.Count, Is.EqualTo(2));
        }

        Assert.That(queue.TryDequeue(out RawVideoFrame? frame, out long queued, 0), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(frame, Is.Not.Null);
            Assert.That(queued, Is.EqualTo(epoch), "the frame has to remember where it came from");
            Assert.That(queue.Count, Is.EqualTo(1));
        }

        frame!.Dispose();
    }

    [Test]
    public void WaitsForThePictureRatherThanDiscardingAFrame()
    {
        EpochGate epochs = new();
        using FrameQueue queue = new(1, epochs);
        long epoch = epochs.Current;
        Assert.That(queue.TryEnqueue(CreateFrame(), epoch), Is.True);

        bool? handedOver = null;
        FakeMediaBuffer handed = new();
        Thread producer = new(() => handedOver = queue.TryEnqueue(CreateFrame(handed), epoch))
        {
            IsBackground = true,
        };

        producer.Start();
        Assert.That(producer.Join(TimeSpan.FromMilliseconds(100)), Is.False, "there is no room, so the decoder waits");

        Assert.That(queue.TryDequeue(out RawVideoFrame? frame, out long _, 0), Is.True);
        frame!.Dispose();

        Assert.That(producer.Join(TimeSpan.FromSeconds(2)), Is.True, "the picture taking a frame is what makes room");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(handedOver, Is.True);
            Assert.That(handed.IsDisposed, Is.False, "a frame nobody took is kept, not discarded");
        }
    }

    [Test]
    public void ReleasesAProducerWaitingForRoomWhenThePositionMovesOn()
    {
        EpochGate epochs = new();
        using FrameQueue queue = new(1, epochs);
        long epoch = epochs.Current;
        Assert.That(queue.TryEnqueue(CreateFrame(), epoch), Is.True);

        bool? handedOver = null;
        Thread producer = new(() => handedOver = queue.TryEnqueue(CreateFrame(), epoch))
        {
            IsBackground = true,
        };

        producer.Start();
        Assert.That(producer.Join(TimeSpan.FromMilliseconds(100)), Is.False, "there is no room yet");

        // What a seek does: waiting for the picture to drain a position playback has left is waiting for
        // frames nobody will show.
        epochs.Advance();
        Assert.That(producer.Join(TimeSpan.FromSeconds(2)), Is.True, "a seek must not wait for the picture");
        Assert.That(handedOver, Is.False, "the frame belongs to a position playback has left");
    }

    [Test]
    public void LooksAtTheOldestFrameWithoutTakingIt()
    {
        EpochGate epochs = new();
        using FrameQueue queue = new(2, epochs);
        long epoch = epochs.Current;

        Assert.That(queue.TryPeek(out RawVideoFrame? _, out long _), Is.False, "there is nothing to look at");

        FakeMediaBuffer oldest = new();
        Assert.That(queue.TryEnqueue(CreateFrame(oldest), epoch), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queue.TryPeek(out RawVideoFrame? peeked, out long peekedEpoch), Is.True);
            Assert.That(peeked!.Buffer, Is.SameAs(oldest), "the oldest frame is the one the picture would show first");
            Assert.That(peekedEpoch, Is.EqualTo(epoch));
            Assert.That(queue.Count, Is.EqualTo(1), "looking costs nothing");
        }

        Assert.That(queue.TryDequeue(out RawVideoFrame? frame, out long _, 0), Is.True);
        frame!.Dispose();
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
