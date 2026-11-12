// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Engine;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The playhead answers one thing for the pipeline: which media the device is playing while it is at a given count. It works that
/// out from the blocks it was handed, so these tests are about which block a count lands in, about the counts that name no time at
/// all, and about nothing being carried from one hand-over to the next but the blocks themselves.
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

    /// <summary>What the file is doing while the device says it is here.</summary>
    private TimeSpan PlayingAt(TimeSpan devicePosition)
    {
        Assert.That(_playhead.TryResolve(devicePosition, out TimeSpan mediaTime), Is.True, "the device is inside the audio it was handed");
        return mediaTime;
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
        Assert.That(whole.TryResolve(TimeSpan.FromMilliseconds(10), out TimeSpan once), Is.True);

        _playhead.OnHandOver(Frames(10), Rate, TimeSpan.Zero, Length(Frames(10)), TimeSpan.Zero);
        _playhead.OnHandOver(Frames(10), Rate, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10) + Length(Frames(10)), TimeSpan.FromMilliseconds(10));
        Assert.That(_playhead.TryResolve(TimeSpan.FromMilliseconds(10), out TimeSpan stepped), Is.True);

        Assert.That(stepped, Is.EqualTo(once), "where the write was split makes no difference to what is heard");
    }

    [Test]
    public void HoldsItsAnswerWhileTheDeviceCannotSayWhereItIs()
    {
        // Before the device has counted anything there is nothing to resolve against.
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10) + Length(Frames(20)), null);
        Assert.That(_playhead.TryResolve(TimeSpan.Zero, out _), Is.False);

        // A paused device does not render, so a block it says nothing about is a block with no count: where the device began is
        // remembered, and the blocks handed over meanwhile are still there to be resolved against.
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromMilliseconds(10020), TimeSpan.FromMilliseconds(10020) + Length(Frames(20)), TimeSpan.Zero);
        Assert.That(PlayingAt(TimeSpan.Zero), Is.EqualTo(TimeSpan.FromSeconds(10)));

        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromMilliseconds(10040), TimeSpan.FromMilliseconds(10040) + Length(Frames(20)), null);

        Assert.That(PlayingAt(TimeSpan.FromMilliseconds(20)), Is.EqualTo(TimeSpan.FromMilliseconds(10020)),
            "the block after it belongs to the count after the first one, which is where the device has got to");
    }

    [Test]
    public void DoesNotLetACountThatRanAheadOfTheAudioTakeTheBlocksAway()
    {
        // A device claiming to have rendered two seconds of audio that was never written is faulty, not fast. Nothing is resolved
        // for that count, and what was recorded survives, so the next count that makes sense is answered properly.
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10) + Length(Frames(20)), TimeSpan.Zero);
        Assert.That(PlayingAt(TimeSpan.FromMilliseconds(5)), Is.EqualTo(TimeSpan.FromMilliseconds(10005)));

        Assert.That(_playhead.TryResolve(TimeSpan.FromSeconds(2), out _), Is.False, "a count that ran ahead of the audio names nothing");

        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromMilliseconds(10020), TimeSpan.FromMilliseconds(10020) + Length(Frames(20)), TimeSpan.FromMilliseconds(20));
        Assert.That(PlayingAt(TimeSpan.FromMilliseconds(20)), Is.EqualTo(TimeSpan.FromMilliseconds(10020)),
            "the block one block of frames in is still known, so the count that makes sense is answered against it");
    }

    [Test]
    public void RefusesACountThatIsBehindWhereTheDeviceStarted()
    {
        // A clock that has been taken back to an earlier moment has not played anything backwards; it names nothing until it is
        // ahead of where it started again.
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10) + Length(Frames(20)), TimeSpan.FromSeconds(5));
        Assert.That(PlayingAt(TimeSpan.FromSeconds(5)), Is.EqualTo(TimeSpan.FromSeconds(10)));

        Assert.That(_playhead.TryResolve(TimeSpan.FromMilliseconds(4990), out _), Is.False);
    }

    [Test]
    public void IgnoresABlockWithNoFrames()
    {
        _playhead.OnHandOver(0, Rate, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), TimeSpan.Zero);

        Assert.That(_playhead.TryResolve(TimeSpan.Zero, out _), Is.False, "a block with nothing in it establishes nothing");
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

        Assert.That(_playhead.TryResolve(TimeSpan.Zero, out _), Is.False, "what was recorded described audio that is gone");

        // From here the first block handed over is the first the device plays, whatever its count says the time is.
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30) + Length(Frames(20)), TimeSpan.Zero);
        Assert.That(PlayingAt(TimeSpan.Zero), Is.EqualTo(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public void CountsTheDeviceFromWhereItFirstSpoke()
    {
        // The device counts from wherever it was started; the file counts from its own timestamps. The first block handed over
        // with a count is what puts the two together, and everything after it is measured from there.
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10) + Length(Frames(20)), TimeSpan.FromSeconds(7));

        Assert.That(PlayingAt(TimeSpan.FromSeconds(7)), Is.EqualTo(TimeSpan.FromSeconds(10)),
            "the device's own seven seconds is the file's ten, which is the block it was handed");
        Assert.That(PlayingAt(TimeSpan.FromMilliseconds(7010)), Is.EqualTo(TimeSpan.FromMilliseconds(10010)));
    }

    [Test]
    public void TakesTheReadingTheBlocksGive()
    {
        // There is nothing to average: the count is the device's own, and what it says is what it has played. What is refused is a
        // count that names no block, which is a different question.
        _playhead.OnHandOver(Frames(20), Rate, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10) + Length(Frames(20)), TimeSpan.Zero);

        Assert.That(PlayingAt(TimeSpan.FromMilliseconds(5)), Is.EqualTo(TimeSpan.FromMilliseconds(10005)));
        Assert.That(PlayingAt(TimeSpan.FromMilliseconds(15)), Is.EqualTo(TimeSpan.FromMilliseconds(10015)));
    }
}
