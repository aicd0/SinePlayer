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
        Prepare(chain);

        chain.Clear();

        using RawAudioBuffer output = Apply(chain);

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

        Prepare(chain);

        using RawAudioBuffer output = Apply(chain);

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
        Prepare(chain);

        RecordingFilter late = new("late");
        chain.Add(late);

        using RawAudioBuffer output = Apply(chain);

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

        using (RawAudioBuffer before = Apply(chain))
        {
            Assert.That(early.AppliedBlocks, Is.Zero, "there is nothing to filter before a stream is known");
        }

        Prepare(chain);
        using (Apply(chain))
        {
        }
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
        Prepare(chain);

        using (Apply(chain))
        {
        }
        Assert.That(order, Is.EqualTo(new[] { "first", "second", "third" }));
    }

    [Test]
    public void HandsBackTheBlockItWasGivenWhenNothingTransformedIt()
    {
        AudioFilterChain chain = new();
        chain.Add(new RecordingFilter("only"));
        Prepare(chain);

        RawAudioBuffer block = CreateBlock();
        RawAudioBuffer output = Apply(chain, block);

        Assert.That(output, Is.SameAs(block), "an in-place chain is the same block");

        output.Dispose();
    }

    /// <summary>
    /// A filter that cannot transform a block fails the block: what it would have produced is the shape everything
    /// after it, and the device, was prepared for, so nothing is handed on and nothing is stepped over.
    /// </summary>
    [Test]
    public void FailsTheBlockWhenAFilterCannotTransformIt()
    {
        List<string> order = [];
        AudioFilterChain chain = new();
        RecordingFilter failing = new("failing", order) { FailApply = true };
        RecordingFilter working = new("working", order);
        chain.Add(failing);
        chain.Add(working);
        Prepare(chain);

        using RawAudioBuffer block = CreateBlock();
        OperationResult<RawAudioBuffer> applied = chain.Snapshot().TryApply(block);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(applied.IsSuccessful, Is.False);
            Assert.That(applied.Message, Does.Contain("failing"));
            Assert.That(order, Is.EqualTo(new[] { "failing" }), "what came after it is not handed the wrong shape");
        }

        using RawAudioBuffer next = CreateBlock();
        Assert.That(chain.Snapshot().TryApply(next).IsSuccessful, Is.False, "a filter that failed once is not skipped afterwards");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failing.AppliedBlocks, Is.EqualTo(2), "it is tried again: one block failing says nothing about the next");
            Assert.That(working.AppliedBlocks, Is.Zero);
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
        Prepare(chain);

        RawAudioBuffer output = Apply(chain, CreateBlock(original));

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

    /// <summary>
    /// A filter that changes the format says what it hands on, and the chain passes that on to whoever opens the
    /// device: it is how a renderer comes to be opened for a format the file does not have.
    /// </summary>
    [Test]
    public void ReportsTheFormatItsFiltersLeaveBlocksIn()
    {
        AudioFilterChain chain = new();
        RecordingFilter folding = new("fold") { Reformat = stream => stream.WithLayout(2, ChannelLayout.Stereo) };
        chain.Add(folding);

        AudioStreamInfo surround = CreateStream().WithLayout(6, ChannelLayout.Surround51);
        AudioStreamInfo target = surround.WithLayout(2, ChannelLayout.Stereo);
        chain.Configure(surround, target);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(folding.ConfiguredTarget, Is.SameAs(target), "a filter is told what the next stage can take");
            Assert.That(chain.Snapshot().OutputFormat, Is.Not.Null);
            Assert.That(chain.Snapshot().OutputFormat!.ChannelCount, Is.EqualTo(2), "what the chain hands on is what the renderer is opened for");
        }
    }

    /// <summary>
    /// The filters are a chain, so each one is prepared for what the one before it hands on: the second filter of a
    /// chain whose first filter folds a mix down is given two channels, not the six the stream arrived with.
    /// </summary>
    [Test]
    public void PreparesEachFilterForWhatTheOneBeforeItHandsOn()
    {
        AudioFilterChain chain = new();
        RecordingFilter folding = new("fold") { Reformat = stream => stream.WithLayout(2, ChannelLayout.Stereo) };
        RecordingFilter after = new("after");
        chain.Add(folding);
        chain.Add(after);

        AudioStreamInfo surround = CreateStream().WithLayout(6, ChannelLayout.Surround51);
        AudioStreamInfo target = surround.WithLayout(2, ChannelLayout.Stereo);
        chain.Configure(surround, target);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(folding.ConfiguredStream, Is.SameAs(surround), "the first filter is given the stream");
            Assert.That(after.ConfiguredStream, Is.SameAs(folding.OutputFormat), "the second is given what the first hands on");
            Assert.That(after.ConfiguredStream!.ChannelCount, Is.EqualTo(2));
            Assert.That(after.ConfiguredTarget, Is.SameAs(target), "and the target is the same for all of them");
            Assert.That(chain.Snapshot().OutputFormat!.ChannelCount, Is.EqualTo(2));
        }
    }

    /// <summary>
    /// A filter added while a stream is open joins the end of the chain, so it is prepared for what the chain already
    /// hands on rather than for the format the stream has.
    /// </summary>
    [Test]
    public void PreparesALateFilterForWhatTheChainHandsOn()
    {
        AudioFilterChain chain = new();
        RecordingFilter folding = new("fold") { Reformat = stream => stream.WithLayout(2, ChannelLayout.Stereo) };
        chain.Add(folding);

        AudioStreamInfo surround = CreateStream().WithLayout(6, ChannelLayout.Surround51);
        chain.Configure(surround, surround.WithLayout(2, ChannelLayout.Stereo));

        RecordingFilter late = new("late");
        chain.Add(late);

        Assert.That(late.ConfiguredStream!.ChannelCount, Is.EqualTo(2), "a filter joining a running chain is given what it hands on");
    }

    /// <summary>
    /// A chain of filters that leave the format alone still answers with a format — the stream it was given. Once the
    /// chain has been prepared there is no "null means unchanged" left for a reader to interpret.
    /// </summary>
    [Test]
    public void ReportsTheStreamWhenNothingChangesIt()
    {
        AudioFilterChain chain = new();
        chain.Add(new RecordingFilter("nothing"));
        AudioStreamInfo stream = Prepare(chain);

        Assert.That(chain.Snapshot().OutputFormat, Is.SameAs(stream));
    }

    /// <summary>Before the chain has been told what is playing there is nothing for it to report.</summary>
    [Test]
    public void ReportsNothingBeforeItKnowsWhatIsPlaying()
    {
        AudioFilterChain chain = new();
        chain.Add(new RecordingFilter("nothing"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.Snapshot().OutputFormat, Is.Null);
            Assert.That(chain.Version, Is.Zero);
        }
    }

    /// <summary>
    /// A filter that changes the format can join a running chain, and the version moves so that whoever keeps the
    /// device open knows to look: that is what a reconfigure is triggered by.
    /// </summary>
    [Test]
    public void LetsAFormatChangeJoinARunningChain()
    {
        AudioFilterChain chain = new();
        Prepare(chain);
        long version = chain.Version;

        RecordingFilter folding = new("fold") { Reformat = stream => stream.WithLayout(2, ChannelLayout.Stereo) };
        chain.Add(folding);

        using RawAudioBuffer output = Apply(chain);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(folding.AppliedBlocks, Is.EqualTo(1), "it takes part in the very next block");
            Assert.That(chain.Snapshot().OutputFormat!.ChannelCount, Is.EqualTo(2), "and what the chain hands on has changed");
            Assert.That(chain.Version, Is.EqualTo(version + 1), "the version is how a reader notices");
        }
    }

    /// <summary>
    /// And it can leave one: the filters after it are prepared again, because they were given what it handed on and now
    /// have something else before them.
    /// </summary>
    [Test]
    public void LetsAFormatChangeLeaveARunningChain()
    {
        AudioFilterChain chain = new();
        RecordingFilter folding = new("fold") { Reformat = stream => stream.WithLayout(2, ChannelLayout.Stereo) };
        RecordingFilter after = new("after");
        chain.Add(folding);
        chain.Add(after);

        AudioStreamInfo surround = CreateStream().WithLayout(6, ChannelLayout.Surround51);
        AudioStreamInfo target = surround.WithLayout(2, ChannelLayout.Stereo);
        chain.Configure(surround, target);
        Assert.That(after.ConfiguredStream!.ChannelCount, Is.EqualTo(2), "it is given what the fold hands on");
        long version = chain.Version;

        Assert.That(chain.Remove(folding), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.Snapshot().OutputFormat, Is.SameAs(surround), "nothing changes the format any more, so the chain hands on the stream");
            Assert.That(chain.Version, Is.EqualTo(version + 1));
            Assert.That(after.ConfiguredStream, Is.SameAs(surround), "the filter after it is prepared again, for the stream itself");
        }
    }

    /// <summary>
    /// A clone keeps answering for the chain it was taken from. An edit while a block is in flight publishes a new
    /// snapshot and leaves the old one as it was, so the block is run through the filters — and into the shape — that
    /// the device was actually opened for. The difference shows in the versions, which is how a holder notices there is
    /// a newer snapshot to take.
    /// </summary>
    [Test]
    public void KeepsAnsweringForTheChainItWasTakenFrom()
    {
        AudioFilterChain chain = new();
        AudioStreamInfo stream = Prepare(chain);
        AudioFilterChainSnapshot held = chain.Snapshot();

        RecordingFilter folding = new("fold") { Reformat = current => current.WithLayout(current.ChannelCount + 1, current.ChannelLayout) };
        chain.Add(folding);

        using RawAudioBuffer block = CreateBlock();
        OperationResult<RawAudioBuffer> applied = held.TryApply(block);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(applied.Result, Is.SameAs(block), "the held snapshot has no filter that transforms a block");
            Assert.That(folding.AppliedBlocks, Is.Zero, "the filter added afterwards is not part of it");
            Assert.That(held.OutputFormat, Is.SameAs(stream), "the snapshot still hands on the stream it was taken for");
            Assert.That(chain.Version, Is.GreaterThan(held.Version), "and the chain has moved on, which is how a holder knows");
            Assert.That(chain.Snapshot().OutputFormat!.ChannelCount, Is.EqualTo(stream.ChannelCount + 1), "a fresh clone is the new chain");
        }
    }

    /// <summary>Two snapshots of the same version hand on the same shape, which is what makes the version worth comparing.</summary>
    [Test]
    public void HandsOutTheSameSnapshotUntilTheChainChanges()
    {
        AudioFilterChain chain = new();
        Prepare(chain);

        AudioFilterChainSnapshot first = chain.Snapshot();
        AudioFilterChainSnapshot again = chain.Snapshot();

        chain.Add(new RecordingFilter("fold") { Reformat = stream => stream.WithLayout(2, ChannelLayout.Stereo) });
        AudioFilterChainSnapshot afterEdit = chain.Snapshot();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(again, Is.SameAs(first), "nothing was edited, so the same snapshot answers");
            Assert.That(afterEdit.Version, Is.GreaterThan(first.Version));
            Assert.That(afterEdit.Filters, Is.Not.EqualTo(first.Filters));
        }
    }

    /// <summary>
    /// The version counts publications, so every edit is one — a filter that leaves the format alone still changes what
    /// a block goes through. Preparing the same stream twice is not an edit and publishes nothing.
    /// </summary>
    [Test]
    public void MovesItsVersionOnEveryEdit()
    {
        AudioFilterChain chain = new();
        AudioStreamInfo surround = CreateStream().WithLayout(6, ChannelLayout.Surround51);
        AudioStreamInfo target = surround.WithLayout(2, ChannelLayout.Stereo);

        chain.Configure(surround, target);
        long afterConfigure = chain.Version;

        chain.Configure(surround, target);
        chain.Add(new RecordingFilter("nothing"));
        long afterNothing = chain.Version;
        chain.Add(new RecordingFilter("fold") { Reformat = stream => stream.WithLayout(2, ChannelLayout.Stereo) });
        long afterFold = chain.Version;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterConfigure, Is.EqualTo(1), "being prepared for the first time is the first publication");
            Assert.That(afterNothing, Is.EqualTo(afterConfigure + 1), "a filter that keeps the format is still an edit");
            Assert.That(afterFold, Is.EqualTo(afterConfigure + 2), "and so is one that changes it");
            Assert.That(chain.Snapshot().OutputFormat!.ChannelCount, Is.EqualTo(2), "only the format says what the shape is");
        }
    }

    /// <summary>Runs a block through the chain the way the audio thread does: through a snapshot of it.</summary>
    private static RawAudioBuffer Apply(AudioFilterChain chain, RawAudioBuffer? block = null)
    {
        block ??= CreateBlock();
        OperationResult<RawAudioBuffer> applied = chain.Snapshot().TryApply(block);
        Assert.That(applied.IsSuccessful, Is.True, applied.Message);
        return applied.Result;
    }

    /// <summary>Prepares the chain for a stream, with the target the next stage can take left at the stream itself.</summary>
    private static AudioStreamInfo Prepare(AudioFilterChain chain, AudioStreamInfo? target = null)
    {
        AudioStreamInfo stream = CreateStream();
        chain.Configure(stream, target ?? stream);
        return stream;
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

        /// <summary>What this filter claims to hand on, when a test is about the chain's format rather than its blocks.</summary>
        public Func<AudioStreamInfo, AudioStreamInfo>? Reformat { get; init; }

        public int AppliedBlocks => Volatile.Read(ref _appliedBlocks);

        public bool Disposed { get; private set; }

        public AudioStreamInfo? ConfiguredTarget { get; private set; }

        /// <summary>
        /// What this filter hands on, settled when it is prepared — the way a real filter reports it, rather than
        /// something computed afresh on every read, because the chain takes the instance and passes it on.
        /// </summary>
        public AudioStreamInfo? OutputFormat { get; private set; }

        public OperationResult Configure(AudioStreamInfo stream, AudioStreamInfo target)
        {
            ConfiguredStream = stream;
            ConfiguredTarget = target;
            OutputFormat = null;

            if (RefuseConfigure)
            {
                return OperationResult.Failure($"{Name} refuses this stream.");
            }

            OutputFormat = Reformat?.Invoke(stream);
            return OperationResult.Success();
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
