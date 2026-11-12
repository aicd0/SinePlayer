// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Engine;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The playhead answers one thing for the pipeline: what to subtract from the device's count. It works that out by reading the
/// count through the blocks it was handed, so these tests are about which block that count lands in, about the counts that cannot
/// be trusted, and about nothing being carried from one hand-over to the next but the measurement itself.
/// </summary>
[TestFixture]
public class AudioPlayheadTests
{
    private const int Rate = 48000;

    private AudioPlayhead _playhead = null!;

    [SetUp]
    public void SetUp()
    {
        _playhead = new AudioPlayhead();
    }

    /// <summary>The frames in a block that lasts this many milliseconds.</summary>
    private static int Frames(int milliseconds)
    {
        return milliseconds * Rate / 1000;
    }

    /// <summary>The media a block of these frames covers when the rate is one, which is what a block normally carries.</summary>
    private static TimeSpan Length(int frames)
    {
        return TimeSpan.FromTicks(frames * TimeSpan.TicksPerSecond / Rate);
    }

    /// <summary>What the clock will read while the device says it is here, which is all the pipeline is given.</summary>
    private TimeSpan PlayingAt(TimeSpan devicePosition)
    {
        Assert.That(_playhead.TryGetOffset(out TimeSpan offset), Is.True, "nothing has been measured to follow");
        return devicePosition - offset;
    }

    [Test]
    public void AnswersTheTimeOfWhatIsBeingPlayed()
    {
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.Zero, Length(Frames(20)), TimeSpan.Zero);

        Assert.That(PlayingAt(TimeSpan.FromMilliseconds(5)), Is.EqualTo(TimeSpan.FromMilliseconds(5)),
            "five milliseconds of the block that was handed over is five milliseconds of the file");
    }

    [Test]
    public void FollowsTheTimestampOfEachBlockRatherThanTheCount()
    {
        // The case a single offset cannot express: the second block says it begins at 100 ms even though the first ended at 20 ms.
        // Whatever the reason — a file whose timestamps and frames disagree — the sound being played belongs to the second block's
        // timeline, and the picture is timed against that same timeline.
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.Zero, Length(Frames(20)), TimeSpan.Zero);
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100) + Length(Frames(20)), TimeSpan.FromMilliseconds(20));

        Assert.That(PlayingAt(TimeSpan.FromMilliseconds(20)), Is.EqualTo(TimeSpan.FromMilliseconds(100)),
            "the device has played one block, and the block that block belongs to says the file is at 100 ms");
    }

    [Test]
    public void TakesThePositionFromWhatTheDeviceHasRendered()
    {
        // The device is the source: its count, read through the blocks, is the position — including inside a block, which is what
        // the pipeline needs between hand-overs.
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.Zero, Length(Frames(20)), TimeSpan.Zero);
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20) + Length(Frames(20)), TimeSpan.FromMilliseconds(20));
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40) + Length(Frames(20)), TimeSpan.FromMilliseconds(35));

        Assert.That(PlayingAt(TimeSpan.FromMilliseconds(35)), Is.EqualTo(TimeSpan.FromMilliseconds(35)),
            "the device has played half of the third block, and the clock says so");
    }

    [Test]
    public void AnswersTheSameForABlockWrittenInSteps()
    {
        // A block the device only had room for part of: what the pipeline reads must not depend on where the block was split.
        AudioPlayhead whole = new();
        whole.OnHandOver(Frames(20), Rate, TimeSpan.Zero, Length(Frames(20)), TimeSpan.Zero);
        Assert.That(whole.TryGetOffset(out TimeSpan once), Is.True);

        _playhead.OnHandOver(Frames(10), Rate, TimeSpan.Zero, Length(Frames(10)), TimeSpan.Zero);
        _playhead.OnHandOver(Frames(10), Rate, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10) + Length(Frames(10)), TimeSpan.FromMilliseconds(10));
        Assert.That(_playhead.TryGetOffset(out TimeSpan stepped), Is.True);

        Assert.That(stepped, Is.EqualTo(once), "where the write was split makes no difference to what is heard");
    }

    [Test]
    public void AveragesAShiftThatFitsInTheQueue()
    {
        // A shift small enough to be the depth of the queue at the moment of the measurement is noise rather than information: a
        // sixteenth of it is taken, so the clock is nudged instead of stepped.
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.Zero, Length(Frames(20)), TimeSpan.Zero);
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(60) + Length(Frames(20)), TimeSpan.FromMilliseconds(20));

        Assert.That(_playhead.TryGetOffset(out TimeSpan first), Is.True);
        Assert.That(first, Is.EqualTo(TimeSpan.FromMilliseconds(-40) / 16), "one sixteenth of the measurement, not the whole of it");

        // Asked again and again with the same shape, it settles on the measurement instead of staying behind it.
        for (int i = 1; i <= 64; i++)
        {
            _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromMilliseconds(60 + (20 * i)), TimeSpan.FromMilliseconds(60 + (20 * i)) + Length(Frames(20)), TimeSpan.FromMilliseconds(20 + (20 * i)));
        }

        Assert.That(_playhead.TryGetOffset(out TimeSpan settled), Is.True);
        Assert.That(settled, Is.EqualTo(TimeSpan.FromMilliseconds(-40)).Within(TimeSpan.FromMilliseconds(1)));
    }

    [Test]
    public void HoldsTheAnswerWhileTheDeviceCannotSayWhereItIs()
    {
        // Before anything is measured, a device that cannot speak leaves nothing to follow.
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10) + Length(Frames(20)), null);
        Assert.That(_playhead.TryGetOffset(out _), Is.False);

        // A paused device does not render, so it has nothing to say. Nothing is invented in its place: the last measurement stands
        // until it can speak again.
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromMilliseconds(10020), TimeSpan.FromMilliseconds(10020) + Length(Frames(20)), TimeSpan.Zero);
        Assert.That(_playhead.TryGetOffset(out TimeSpan measured), Is.True);

        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromMilliseconds(10040), TimeSpan.FromMilliseconds(10040) + Length(Frames(20)), null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_playhead.TryGetOffset(out TimeSpan offset), Is.True);
            Assert.That(offset, Is.EqualTo(measured), "nothing was measured while the device could not say where it is");
            Assert.That(PlayingAt(TimeSpan.Zero), Is.EqualTo(TimeSpan.FromMilliseconds(10000)), "so the clock still reads what it did");
        }
    }

    [Test]
    public void DoesNotLetACountThatRanAheadOfTheAudioEmptyWhatWasRecorded()
    {
        // A device claiming to have rendered two seconds of audio that was never written is faulty, not fast. Nothing is measured
        // for that count, and what was recorded survives, so the next count that makes sense is answered properly.
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10) + Length(Frames(20)), TimeSpan.Zero);
        Assert.That(_playhead.TryGetOffset(out TimeSpan held), Is.True);

        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromMilliseconds(10020), TimeSpan.FromMilliseconds(10020) + Length(Frames(20)), TimeSpan.FromSeconds(2));
        Assert.That(_playhead.TryGetOffset(out TimeSpan refused), Is.True);
        Assert.That(refused, Is.EqualTo(held), "a count that ran ahead of the audio names nothing");

        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromMilliseconds(10040), TimeSpan.FromMilliseconds(10040) + Length(Frames(20)), TimeSpan.FromMilliseconds(20));
        Assert.That(PlayingAt(TimeSpan.FromMilliseconds(20)), Is.EqualTo(TimeSpan.FromMilliseconds(10020)),
            "the block one block of frames in is still known, so the count that makes sense is answered against it");
    }

    [Test]
    public void RefusesACountThatWentBackwards()
    {
        // A clock that has been taken back to an earlier moment has not played anything backwards; it is measuring nothing until
        // it is ahead of where it started again.
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10) + Length(Frames(20)), TimeSpan.FromSeconds(5));
        Assert.That(_playhead.TryGetOffset(out TimeSpan measured), Is.True);

        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromMilliseconds(10020), TimeSpan.FromMilliseconds(10020) + Length(Frames(20)), TimeSpan.FromMilliseconds(4990));

        Assert.That(_playhead.TryGetOffset(out TimeSpan offset), Is.True);
        Assert.That(offset, Is.EqualTo(measured));
    }

    [Test]
    public void IgnoresABlockWithNoFrames()
    {
        _playhead.OnHandOver(0, Rate, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), TimeSpan.Zero);

        Assert.That(_playhead.TryGetOffset(out _), Is.False, "a block with nothing in it establishes nothing");
    }

    [Test]
    public void PlacesTheFramesInsideTheMediaSpanTheBlockCovers()
    {
        // Blocks that were retimed on their way to the device: 160 ms of media carried by 20 ms of frames, which is what audio at
        // eight times the speed looks like. The count is frames, the answer is media time, and each block says how the two go
        // together - the same frames would otherwise read as 10 ms instead of the 80 ms of the file they stand for.
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(10160), TimeSpan.Zero);
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromMilliseconds(10160), TimeSpan.FromMilliseconds(10320), TimeSpan.FromMilliseconds(10));

        Assert.That(PlayingAt(TimeSpan.FromMilliseconds(10)), Is.EqualTo(TimeSpan.FromMilliseconds(10080)),
            "half of the first block's frames is half of the media it covers");
    }

    [Test]
    public void StartsAgainFromTheFirstBlockAfterAReset()
    {
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.Zero, Length(Frames(20)), TimeSpan.Zero);
        _playhead.Reset();

        Assert.That(_playhead.TryGetOffset(out _), Is.False, "what was measured described audio that is gone");

        // From here the first block handed over is the first the device plays, whatever its count says the time is.
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30) + Length(Frames(20)), TimeSpan.Zero);
        Assert.That(PlayingAt(TimeSpan.Zero), Is.EqualTo(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public void ReportsAnOffsetThatTurnsTheDeviceCountIntoMediaTime()
    {
        // The device counts from wherever it was started; the file counts from its own timestamps. The offset is the difference,
        // and it is what lets the timeline follow the device while following the file.
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10) + Length(Frames(20)), TimeSpan.Zero);

        Assert.That(_playhead.TryGetOffset(out TimeSpan offset), Is.True);

        Assert.That(TimeSpan.Zero - offset, Is.EqualTo(TimeSpan.FromSeconds(10)),
            "the device's first moment is the file's 10 s, which is the block it was handed");
    }
}
