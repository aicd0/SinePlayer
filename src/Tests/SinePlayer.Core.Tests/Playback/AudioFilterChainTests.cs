// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Filter;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The chain is the host's list and the pipeline's working set: what is added is what is configured for the next
/// stream, in order, and a filter that cannot take part is left out rather than allowed to take the sound with it.
/// </summary>
[TestFixture]
public class AudioFilterChainTests
{
    [Test]
    public void AddsFiltersInTheOrderTheyWereGiven()
    {
        AudioFilterChain chain = new();
        RecordingFilter first = new("first");
        RecordingFilter second = new("second");

        chain.Add(first);
        chain.Add(second);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.Count, Is.EqualTo(2));
            Assert.That(chain.Filters, Is.EqualTo(new IAudioFilter[] { first, second }));
        }
    }

    [Test]
    public void RefusesTheSameFilterTwice()
    {
        AudioFilterChain chain = new();
        RecordingFilter filter = new("only");
        chain.Add(filter);

        Assert.Throws<InvalidOperationException>(() => chain.Add(filter));
    }

    /// <summary>
    /// A filter is the host's, so taking one out cannot end it: the host may still be setting its level, and may
    /// put it back.
    /// </summary>
    [Test]
    public void HandsAFilterBackWhenItIsRemoved()
    {
        AudioFilterChain chain = new();
        RecordingFilter filter = new("only");
        chain.Add(filter);

        Assert.That(chain.Remove(filter), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.Count, Is.Zero);
            Assert.That(chain.Remove(filter), Is.False, "a filter that is not in the chain cannot be removed twice");
            Assert.That(filter.Disposed, Is.False, "the chain never disposes what the host gave it");
        }
    }

    [Test]
    public void EmptiesTheChainWhenItIsCleared()
    {
        AudioFilterChain chain = new();
        RecordingFilter first = new("first");
        RecordingFilter second = new("second");
        chain.Add(first);
        chain.Add(second);
        chain.Configure(CreateStream());

        chain.Clear();

        using RawAudioBuffer output = chain.Apply(CreateBlock());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.Count, Is.Zero);
            Assert.That(first.AppliedBlocks + second.AppliedBlocks, Is.Zero, "a cleared chain runs nothing");
            Assert.That(first.Disposed || second.Disposed, Is.False, "clearing does not dispose the host's filters");
        }
    }

    /// <summary>
    /// A filter that cannot work with the stream is a line in the log and nothing more: the rest of the chain runs,
    /// the open succeeds, and the caller has nothing to handle.
    /// </summary>
    [Test]
    public void LeavesOutAFilterThatRefusesTheStream()
    {
        AudioFilterChain chain = new();
        RecordingFilter accepting = new("accepting");
        RecordingFilter refusing = new("refusing") { RefuseConfigure = true };
        chain.Add(accepting);
        chain.Add(refusing);

        chain.Configure(CreateStream());

        using RawAudioBuffer output = chain.Apply(CreateBlock());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(output.Buffer, Is.Not.Null);
            Assert.That(accepting.AppliedBlocks, Is.EqualTo(1), "the rest of the chain still runs");
            Assert.That(refusing.AppliedBlocks, Is.Zero, "what could not be configured is never handed a block");
            Assert.That(refusing.ConfiguredStream, Is.Not.Null, "every filter is asked about the stream, even the one that refuses");
            Assert.That(refusing.Disposed, Is.False);
        }
    }

    /// <summary>
    /// The chain remembers the stream, which is what makes an edit immediate: a filter added while something is
    /// open is prepared there and then and takes part in the very next block — no reconcile, no rebuild.
    /// </summary>
    [Test]
    public void PreparesAFilterAddedAfterTheStreamIsKnown()
    {
        AudioFilterChain chain = new();
        chain.Configure(CreateStream());

        RecordingFilter late = new("late");
        chain.Add(late);

        using RawAudioBuffer output = chain.Apply(CreateBlock());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(late.ConfiguredStream, Is.Not.Null, "a filter added while open is configured as it is added");
            Assert.That(late.AppliedBlocks, Is.EqualTo(1), "and it is part of the next block");
            Assert.That(chain.Filters, Is.EqualTo(new IAudioFilter[] { late }));
        }
    }

    /// <summary>A filter added before anything is open waits for the stream, which is what <c>Configure</c> gives it.</summary>
    [Test]
    public void PreparesAnEarlyFilterWhenTheStreamArrives()
    {
        AudioFilterChain chain = new();
        RecordingFilter early = new("early");
        chain.Add(early);

        using (RawAudioBuffer before = chain.Apply(CreateBlock()))
        {
            Assert.That(early.AppliedBlocks, Is.Zero, "there is nothing to filter before a stream is known");
        }

        chain.Configure(CreateStream());
        chain.Apply(CreateBlock()).Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(early.ConfiguredStream, Is.Not.Null);
            Assert.That(early.AppliedBlocks, Is.EqualTo(1));
        }
    }

    [Test]
    public void RunsTheFiltersInTheOrderTheyWereAdded()
    {
        List<string> order = [];
        AudioFilterChain chain = new();
        chain.Add(new RecordingFilter("first", order));
        chain.Add(new RecordingFilter("second", order));
        chain.Add(new RecordingFilter("third", order));
        chain.Configure(CreateStream());

        chain.Apply(CreateBlock()).Dispose();

        Assert.That(order, Is.EqualTo(new[] { "first", "second", "third" }));
    }

    [Test]
    public void HandsBackTheBlockItWasGivenWhenNothingTransformedIt()
    {
        AudioFilterChain chain = new();
        chain.Add(new RecordingFilter("only"));
        chain.Configure(CreateStream());

        RawAudioBuffer block = CreateBlock();
        RawAudioBuffer output = chain.Apply(block);

        Assert.That(output, Is.SameAs(block), "an in-place chain is the same block");

        output.Dispose();
    }

    /// <summary>
    /// A filter that cannot transform a block loses its place and the block carries on: the effect is lost, the
    /// sound is not — and it is not asked again for the rest of the stream.
    /// </summary>
    [Test]
    public void DropsAFilterThatCannotTransformAndCarriesTheBlockOn()
    {
        List<string> order = [];
        AudioFilterChain chain = new();
        RecordingFilter failing = new("failing", order) { FailApply = true };
        RecordingFilter working = new("working", order);
        chain.Add(failing);
        chain.Add(working);
        chain.Configure(CreateStream());

        RawAudioBuffer block = CreateBlock();
        using (RawAudioBuffer output = chain.Apply(block))
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(output, Is.SameAs(block), "the block that the failing filter was handed still plays");
                Assert.That(order, Is.EqualTo(new[] { "failing", "working" }), "a failure does not stop the block");
            }
        }

        chain.Apply(CreateBlock()).Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failing.AppliedBlocks, Is.EqualTo(1), "the dropped filter is not asked again");
            Assert.That(working.AppliedBlocks, Is.EqualTo(2));
        }
    }

    /// <summary>
    /// A filter is allowed to hand back a different block. The one it was given is released here, because nothing
    /// else will ever see it — and the caller is left with exactly one block to dispose.
    /// </summary>
    [Test]
    public void ReleasesABlockAFilterReplaced()
    {
        TestBuffer original = new();
        TestBuffer replacement = new();
        AudioFilterChain chain = new();
        chain.Add(new RecordingFilter("copier") { Replace = _ => CreateBlock(replacement) });
        chain.Configure(CreateStream());

        RawAudioBuffer output = chain.Apply(CreateBlock(original));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(original.Disposed, Is.True, "the block the filter was given is released by the chain");
            Assert.That(output.Buffer, Is.SameAs(replacement), "what the filter produced is what the device gets");
        }

        output.Dispose();
    }

    private static AudioStreamInfo CreateStream()
    {
        return new AudioStreamInfo
        {
            Index = 0,
            CodecId = "pcm_s16le",
            SampleRate = 44100,
            ChannelCount = 1,
            ChannelLayout = ChannelLayout.Mono,
            SampleFormat = AudioSampleFormat.Int16,
        };
    }

    private static RawAudioBuffer CreateBlock(IMediaBuffer? buffer = null)
    {
        return new RawAudioBuffer
        {
            Timestamp = TimeSpan.Zero,
            SampleRate = 44100,
            ChannelCount = 1,
            ChannelLayout = ChannelLayout.Mono,
            SampleFormat = AudioSampleFormat.Int16,
            SampleCount = 4,
            Buffer = buffer ?? new TestBuffer(),
        };
    }

    private sealed class RecordingFilter : IAudioFilter
    {
        private readonly List<string>? _order;
        private int _appliedBlocks;

        internal RecordingFilter(string name, List<string>? order = null)
        {
            Name = name;
            _order = order;
        }

        public string Name { get; }

        public AudioStreamInfo? ConfiguredStream { get; private set; }

        public bool RefuseConfigure { get; init; }

        public bool FailApply { get; init; }

        public Func<RawAudioBuffer, RawAudioBuffer>? Replace { get; init; }

        public int AppliedBlocks => Volatile.Read(ref _appliedBlocks);

        public bool Disposed { get; private set; }

        public OperationResult Configure(AudioStreamInfo stream)
        {
            ConfiguredStream = stream;

            return RefuseConfigure
                ? OperationResult.Failure($"{Name} refuses this stream.")
                : OperationResult.Success();
        }

        public OperationResult Apply(RawAudioBuffer input, out RawAudioBuffer output)
        {
            Interlocked.Increment(ref _appliedBlocks);
            _order?.Add(Name);
            output = input;

            if (FailApply)
            {
                return OperationResult.Failure($"{Name} cannot transform this block.");
            }

            if (Replace is not null)
            {
                output = Replace(input);
            }

            return OperationResult.Success();
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class TestBuffer : IMediaBuffer
    {
        public long Length => 0;

        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
