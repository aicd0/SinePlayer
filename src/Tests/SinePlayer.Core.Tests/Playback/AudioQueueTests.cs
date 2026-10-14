// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Internal;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The queue between the reader and the device has no maximum either, and for the same reason: the reader
/// a full queue would stop is the reader that produces everything else too. What is checked here is that
/// audio handed over is audio kept, and that what belongs to a position playback has left is not taken.
/// </summary>
[TestFixture]
public class AudioQueueTests
{
    [Test]
    public void HandsBuffersOverInOrder()
    {
        EpochGate epochs = new();
        using AudioQueue queue = new(epochs);
        long epoch = epochs.Current;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queue.TryEnqueue(CreateBuffer(), epoch), Is.True);
            Assert.That(queue.QueuedSamples, Is.EqualTo(100));
        }

        Assert.That(queue.TryDequeue(out RawAudioBuffer? buffer, 0), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer, Is.Not.Null);
            Assert.That(queue.QueuedSamples, Is.Zero);
        }

        buffer!.Dispose();
    }

    [Test]
    public void TakesAsMuchAudioAsItIsGivenRatherThanStoppingTheReader()
    {
        EpochGate epochs = new();
        using AudioQueue queue = new(epochs);
        long epoch = epochs.Current;

        // A device that is slow to take audio must not stop the reader: a reader waiting here waits for a
        // device, and everything behind it — the picture included — waits with it.
        using (Assert.EnterMultipleScope())
        {
            for (int index = 0; index < 100; index++)
            {
                Assert.That(queue.TryEnqueue(CreateBuffer(), epoch), Is.True, $"buffer {index} has to be taken");
                Assert.That(queue.QueuedSamples, Is.EqualTo((index + 1) * 100));
            }
        }

        Assert.That(queue.TryDequeue(out RawAudioBuffer? first, 0), Is.True);
        first!.Dispose();
    }

    [Test]
    public void TakesNothingFromAPositionPlaybackHasLeft()
    {
        EpochGate epochs = new();
        using AudioQueue queue = new(epochs);

        epochs.Advance();
        using RawAudioBuffer buffer = CreateBuffer();
        Assert.That(queue.TryEnqueue(buffer, epochs.Current - 1), Is.False, "audio for a position that is gone is not taken");
        Assert.That(queue.QueuedSamples, Is.Zero);
    }

    private static RawAudioBuffer CreateBuffer()
    {
        return new RawAudioBuffer
        {
            Timestamp = TimeSpan.Zero,
            SampleRate = 44100,
            ChannelCount = 1,
            ChannelLayout = ChannelLayout.Mono,
            SampleFormat = AudioSampleFormat.Float32,
            SampleCount = 100,
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
