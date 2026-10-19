// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The queue between two stages of playback. It is a data structure and nothing else: it takes what it is
/// given, hands it back in order, and never waits — every thread sleeps on its own <see cref="PipelineNode"/>
/// and is woken by the thread that changed something it looks at (see <see cref="PipelineNodeTests"/>).
/// <para>
/// What it does <em>not</em> do is compare the tag it carries against anything: the tag is the pipeline's, and
/// both ends decide what it means. That is why there is no test here for "a seek releases the producer" — a
/// seek is not something this class can see — and why the tag survives a position change untouched, which is
/// what the ends rely on. What the queue *does* carry besides the media is the end of a stream, as an entry of
/// its own: it is handed over in order, it takes no room, and it is why an empty queue and a finished one are
/// different observations.
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

        Assert.That(queue.TryDequeue(out MediaItem<MediaPacket>? first), Is.True, "the queue has to hand the first entry over");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first?.IsLast, Is.False, "media is not the end of a stream");
            Assert.That(first?.Item?.StreamIndex, Is.EqualTo(0), "items come back in the order they went in");
            Assert.That(first?.Epoch, Is.EqualTo(7), "an item has to remember where it came from");
        }

        // The tag says what the item belongs to, not what the pipeline is doing now: the queue has no idea
        // whether seven is still current, and it is not its job to have one.
        Assert.That(queue.TryDequeue(out MediaItem<MediaPacket>? second), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(second?.Item?.StreamIndex, Is.EqualTo(1));
            Assert.That(second?.Epoch, Is.EqualTo(7));
        }

        first?.Item?.Dispose();
        second?.Item?.Dispose();
    }

    /// <summary>
    /// Nothing is thrown away to make room and nobody is made to wait for it: how much may be waiting is the
    /// producer's business, and the one queue that paces its producer checks the window before it produces.
    /// </summary>
    [Test]
    public void TakesAsMuchAsItIsGivenRatherThanStoppingTheProducer()
    {
        using MediaQueue<MediaPacket> queue = new();

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

        Assert.That(queue.TryDequeue(out MediaItem<RawAudioBuffer>? entry), Is.True);
        Assert.That(queue.Weight, Is.EqualTo(250), "taking an item takes its weight with it");

        entry?.Item?.Dispose();
    }

    [Test]
    public void LooksAtTheOldestItemWithoutTakingIt()
    {
        using MediaQueue<RawVideoFrame> queue = new();

        Assert.That(queue.TryPeek(out MediaItem<RawVideoFrame>? _), Is.False, "there is nothing to look at");

        FakeMediaBuffer oldest = new();
        Assert.That(queue.TryEnqueue(CreateFrame(oldest), 3), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queue.TryPeek(out MediaItem<RawVideoFrame>? peeked), Is.True);
            Assert.That(peeked?.Item?.Buffer, Is.SameAs(oldest), "the oldest item is the one the consumer would take first");
            Assert.That(peeked?.Epoch, Is.EqualTo(3));
            Assert.That(queue.Count, Is.EqualTo(1), "looking costs nothing");
        }
    }

    /// <summary>
    /// The take that a look earns: a consumer hands back the entry it looked at and gets that entry, or
    /// nothing at all when a clear has taken it away — which is the whole reason the entry is a class, since
    /// the entries that replace it may carry the very same tag.
    /// </summary>
    [Test]
    public void TakesTheEntryThatWasLookedAtRatherThanWhateverIsAtTheHead()
    {
        using MediaQueue<RawVideoFrame> queue = new();
        FakeMediaBuffer first = new();
        FakeMediaBuffer second = new();

        Assert.That(queue.TryEnqueue(CreateFrame(first), 1), Is.True);
        if (!queue.TryPeek(out MediaItem<RawVideoFrame>? seen))
        {
            Assert.Fail("there has to be an entry to look at");
            return;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seen.Item?.Buffer, Is.SameAs(first));
            Assert.That(seen.Epoch, Is.EqualTo(1));
        }

        // A clear — a seek — takes what it holds, and what replaces it carries the very same tag, so nothing
        // about an entry except its identity can say whether it is still the one that was looked at.
        queue.Clear();
        Assert.That(queue.TryEnqueue(CreateFrame(second), 1), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queue.TryDequeue(seen), Is.False, "the entry that was looked at is gone, so nothing is taken");
            Assert.That(queue.Count, Is.EqualTo(1), "the entry that is there now was left alone");
        }

        if (!queue.TryPeek(out MediaItem<RawVideoFrame>? current))
        {
            Assert.Fail("the entry that replaced it has to be there");
            return;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queue.TryDequeue(current), Is.True, "what was looked at is what is taken");
            Assert.That(queue.Count, Is.Zero);
        }
    }

    [Test]
    public void DisposesWhatItStillHoldsWhenItIsClosed()
    {
        FakeMediaBuffer held = new();
        using MediaQueue<RawVideoFrame> queue = new();
        Assert.That(queue.TryEnqueue(CreateFrame(held), 0), Is.True);

        queue.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queue.IsClosed, Is.True);
            Assert.That(held.IsDisposed, Is.True, "what the queue took over is the queue's to release");
            Assert.That(queue.Count, Is.Zero);
            Assert.That(queue.TryEnqueue(CreateFrame(), 0), Is.False, "a closed queue takes nothing more");
            Assert.That(queue.TryDequeue(out MediaItem<RawVideoFrame>? _), Is.False, "and hands nothing over");
            Assert.That(queue.TryPeek(out MediaItem<RawVideoFrame>? _), Is.False, "and has nothing to look at");
        }
    }

    /// <summary>
    /// The end of a stream, which is an entry like any other and is why a consumer does not have to tell an
    /// empty queue that is finished from one that is merely behind: it is handed the end after everything
    /// enqueued before it, and a producer can say it while media is waiting.
    /// </summary>
    [Test]
    public void HandsTheEndOfTheStreamOverAfterEverythingQueuedBeforeIt()
    {
        using MediaQueue<RawVideoFrame> queue = new();
        Assert.That(queue.TryEnqueue(CreateFrame(), 5), Is.True);

        queue.EndStream(5);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queue.Count, Is.EqualTo(1), "the end of a stream is not media and is not counted");
            Assert.That(queue.Weight, Is.EqualTo(1), "and it does not weigh anything either");
        }

        Assert.That(queue.TryDequeue(out MediaItem<RawVideoFrame>? first), Is.True, "the queue has to hand the media over first");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first?.IsLast, Is.False, "the media enqueued before it comes first");
            Assert.That(first?.Epoch, Is.EqualTo(5));
        }

        first?.Item?.Dispose();

        Assert.That(queue.TryDequeue(out MediaItem<RawVideoFrame>? end), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(end?.IsLast, Is.True, "which is how a consumer learns the stream ended");
            Assert.That(end?.Item, Is.Null, "the end of a stream is not media");
            Assert.That(end?.Epoch, Is.EqualTo(5), "and which position's stream it was");
            Assert.That(queue.Count, Is.Zero);
            Assert.That(queue.Weight, Is.Zero);
        }

        Assert.That(queue.TryPeek(out MediaItem<RawVideoFrame>? _), Is.False, "the end of a stream is handed over once");
    }

    /// <summary>
    /// A closed queue says so rather than waiting for something that will never come: every word answers at
    /// once, which is what keeps a stopping pipeline's joins from waiting on a consumer. Nothing in the queue
    /// waits for anything — the threads do the waiting, each on its own node.
    /// </summary>
    [Test]
    public void AnswersAtOnceOnceItIsClosed()
    {
        MediaQueue<RawVideoFrame> queue = new();
        queue.Dispose();

        bool taken = queue.TryDequeue(out MediaItem<RawVideoFrame>? entry);
        bool looked = queue.TryPeek(out MediaItem<RawVideoFrame>? peeked);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(taken, Is.False);
            Assert.That(entry, Is.Null);
            Assert.That(looked, Is.False);
            Assert.That(peeked, Is.Null);
            Assert.That(queue.IsClosed, Is.True);
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
