// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Engine;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The window a subtitle track's decoded cues wait in. It belongs to the track rather than to a position: a cue is a
/// timestamped fact about the file, so what is on screen is the same question however the playhead got there.
/// </summary>
[TestFixture]
public class SubtitleCueWindowTests
{
    /// <summary>The run of reading the tests below belong to, unless they are about a jump between runs.</summary>
    private const long Epoch = 0;

    /// <summary>A picture big enough that a handful of them reach the window's budget.</summary>
    private const long BudgetSize = 20L * 1024 * 1024;

    /// <summary>What the window keeps unless it is told otherwise.</summary>
    private const long DefaultBudget = 32L * 1024 * 1024;

    [Test]
    public void ShowsTheCuesThatCoverThePosition()
    {
        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[Cue(1, 3), Cue(2, 4)], TimeSpan.Zero);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(0.5))), Is.Empty, "before the first cue starts");
            Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(2.5))), Is.EqualTo(new[] { "1", "2" }), "overlapping events are both up");
            Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(3.5))), Is.EqualTo(new[] { "2" }), "the first has ended");
            Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(4))), Is.Empty, "a cue's end is not its moment");
        }
    }

    [Test]
    public void KeepsACueThatIsStillOnScreenWhenThePlayheadMoves()
    {
        // What a forward seek costs without this: the cue's packet lies before the landing, so the re-read never delivers
        // it again and the text is lost until the cue's own end.
        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[Cue(1, 20)], TimeSpan.Zero);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(6))), Is.EqualTo(new[] { "1" }));
            Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(15))), Is.EqualTo(new[] { "1" }));
            Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(20))), Is.Empty);
        }
    }

    [Test]
    public void CountsTheSameEventOnceWhenItIsReadAgain()
    {
        // A seek re-reads from a keyframe, so the events ahead of the new position arrive a second time.
        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[Cue(5, 9)], TimeSpan.Zero);
        window.Add(Epoch, (List<SubtitleCue>)[Cue(5, 9)], TimeSpan.FromSeconds(5));

        Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(6))), Is.EqualTo(new[] { "5" }), "the same event twice is one cue");
    }

    [Test]
    public void TellsSimultaneousEventsApartByTheirText()
    {
        // Two speakers at once are written as two events over one span: the same times, different words.
        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[Cue(5, 9, "left"), Cue(5, 9, "right")], TimeSpan.Zero);

        Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(6))), Is.EqualTo(new[] { "left", "right" }));
    }

    [Test]
    public void ShowsWhatIsStillUpAndNotWhatHasEnded()
    {
        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[Cue(1, 2), Cue(3, 11)], TimeSpan.Zero);

        // Ten seconds on: the first cue is long gone, the second is still up, and a third arrives.
        window.Add(Epoch, (List<SubtitleCue>)[Cue(10, 12)], TimeSpan.FromSeconds(10));

        Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(10.5))), Is.EqualTo(new[] { "3", "10" }), "the ended cue is gone, the running one stayed");
    }

    [Test]
    public void KeepsItsCuesInTimeOrderWhenAJumpGoesBack()
    {
        // A jump back leaves the cues that are still ahead of the landing where they were, and what the re-read delivers
        // begins before them: appending would leave the window holding a timeline whose later end comes first.
        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[Cue(10, 20)], TimeSpan.Zero);

        window.SeekTo(Epoch, TimeSpan.FromSeconds(5));
        window.Add(Epoch, (List<SubtitleCue>)[Cue(5, 12)], TimeSpan.FromSeconds(5));

        Assert.That(
            Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(11))),
            Is.EqualTo(new[] { "5", "10" }),
            "the cue that begins first comes first, however the playhead got there");
    }

    [Test]
    public void KeepsACueThePlayheadHasPassedSoAJumpBackFindsIt()
    {
        // The packet behind a line lies before where a jump back lands, so a re-read never delivers it again. Keeping
        // what has been passed is what makes the line the jump landed in come back without reading the file.
        WatchImage image = new();
        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[Cue(1, 2, image)], TimeSpan.Zero);

        Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(5))), Is.Empty, "the line has ended");
        Assert.That(image.Disposed, Is.False, "and it is kept rather than let go of");

        window.SeekTo(Epoch, TimeSpan.FromSeconds(1.5));

        Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(1.5))), Is.EqualTo(new[] { "1" }), "the jump back finds the line that belongs there");
    }

    [Test]
    public void FreesTheLeastRecentlyAskedForCueWhenTheBudgetIsExceeded()
    {
        // The budget is a limit, not a hint: once it is exceeded the cold cues that have waited longest go. Asking for
        // a line is what keeps it recent, so the place a cue was just seen is the last one to lose it — even though it
        // is the oldest thing in the track.
        const long budget = 64L * 1024 * 1024;
        const int count = 70_000;

        SubtitleCueWindow window = Window();
        var images = new WatchImage[count];
        for (int index = 0; index < count; index++)
        {
            var image = new WatchImage(1024);
            images[index] = image;
            var start = TimeSpan.FromSeconds(index);
            window.Add(
                Epoch,
                (List<SubtitleCue>)[new SubtitleCue { Start = start, End = start + TimeSpan.FromMilliseconds(900), Image = new SubtitleImage { Buffer = image, Left = 0, Top = 0, Width = 1, Height = 1 } }],
                start);
        }

        // The oldest event in the file is the one on screen, so it is asked for last of all.
        using (window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(0.5)))
        {
        }

        window.SeekTo(Epoch, TimeSpan.FromSeconds(60_000));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(images[1].Disposed, Is.True, "the cue that has waited longest is the one that goes");
            Assert.That(images[0].Disposed, Is.False, "the cue that was on screen most recently stays");
            Assert.That(window.ApproximateSize, Is.LessThanOrEqualTo(budget), "and the keeping is back within the budget");
        }
    }

    [Test]
    public void IgnoresAQuestionAboutTheReadingAJumpAbandoned()
    {
        // What this pins: the renderer asks about the position its clock still reports, and the clock is only repositioned
        // after the jump has already started a new reading. So the first question after a backward jump is about where the
        // playhead *was* — and answering it used to move the floor past everything the re-read was about to deliver, which
        // lost the cue at the top of the file for as long as it took to reach the old position again.
        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[Cue(1, 3)], TimeSpan.Zero);

        using (window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(60)))
        {
        }

        const long moved = Epoch + 1;
        window.SeekTo(moved, TimeSpan.Zero);

        Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(60))), Is.Empty, "a question from the reading that was abandoned is not answered");

        window.Add(moved, (List<SubtitleCue>)[Cue(1, 3)], TimeSpan.Zero);

        // The renderer does not ask once: it polls, and the clock is only repositioned some time after the jump. Every one
        // of those questions must leave the new reading alone, not merely the first.
        Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(60))), Is.Empty);

        Assert.That(
            Texts(window.GetVisibleCues(moved, TimeSpan.FromSeconds(2))),
            Is.EqualTo(new[] { "1" }),
            "the cue at the top of the file comes back, because the abandoned questions did not move the floor past it");
    }

    [Test]
    public void RefusesCuesFromAReadingThatHasBeenAbandoned()
    {
        // The decoder notices a jump before the reader has finished with the packets it already queued, so a cue can arrive
        // late from the reading that was replaced. It describes a position nobody will ask about again.
        WatchImage image = new();
        SubtitleCueWindow window = Window();
        window.SeekTo(7, TimeSpan.FromSeconds(4));

        window.Add(6, (List<SubtitleCue>)[Cue(4, 6, image)], TimeSpan.FromSeconds(4));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Texts(window.GetVisibleCues(7, TimeSpan.FromSeconds(5))), Is.Empty, "the stray cue is not shown");
            Assert.That(image.Disposed, Is.True, "and it is let go of rather than kept for a question that will never come");
        }
    }

    [Test]
    public void MovesTheFloorBackwardsWithAJump()
    {
        // A backward jump asks about positions the window has already passed. If the floor stayed where it was, everything
        // the jump landed on would be discarded as already seen.
        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[Cue(1, 5)], TimeSpan.Zero);

        using (window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(90)))
        {
        }

        window.SeekTo(Epoch, TimeSpan.FromSeconds(2));
        window.Add(Epoch, (List<SubtitleCue>)[Cue(2, 4)], TimeSpan.FromSeconds(2));

        Assert.That(
            Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(3))),
            Is.EqualTo(new[] { "1", "2" }),
            "the cue the jump landed on was kept, and so was the one already passed that still covers it");
    }

    [Test]
    public void CountsTheSameEventOnceWhenTheCopyAlreadyHeldIsCold()
    {
        // A jump re-reads what it lands on. If the event was already held and has been passed since, the copy that comes
        // back must still be recognised as the same one, or the budget would be spent on two pictures of one line.
        WatchImage held = new();
        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[Cue(1, 2, held)], TimeSpan.Zero);

        using (window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(5)))
        {
        }

        WatchImage repeated = new();
        window.Add(Epoch, (List<SubtitleCue>)[Cue(1, 2, repeated)], TimeSpan.FromSeconds(5));

        window.SeekTo(Epoch, TimeSpan.FromSeconds(1.5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(1.5))), Is.EqualTo(new[] { "1" }), "one copy, although the match was cold");
            Assert.That(repeated.Disposed, Is.True, "the second decode is let go of");
            Assert.That(held.Disposed, Is.False, "and the copy that was kept is the one that answers");
        }
    }

    [Test]
    public void NeverFreesWhatIsStillOnScreenForTheBudget()
    {
        // A budget cannot be met by taking away the answer to the question being asked: cues whose end is still ahead
        // stay, even when they alone exceed the limit.
        WatchImage first = new(BudgetSize);
        WatchImage second = new(BudgetSize);
        WatchImage third = new(BudgetSize);
        WatchImage fourth = new(BudgetSize);

        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[Cue(1, 100, first), Cue(2, 100, second), Cue(3, 100, third), Cue(4, 100, fourth)], TimeSpan.Zero);

        using (window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(1.5)))
        {
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Disposed, Is.False);
            Assert.That(second.Disposed, Is.False);
            Assert.That(third.Disposed, Is.False);
            Assert.That(fourth.Disposed, Is.False, "nothing on screen was given up to meet the budget");
        }
    }

    [Test]
    public void LetsTheReaderSayWhatHasBeenPassedWhenNothingIsDrawing()
    {
        // Nothing will ask this window anything, so the position the reader has reached is the only clock it has.
        // Without it a track nobody draws would never mark anything as passed, and the budget would have nothing to take.
        WatchImage first = new(BudgetSize);
        WatchImage second = new(BudgetSize);
        WatchImage third = new(BudgetSize);
        WatchImage fourth = new(BudgetSize);

        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[Cue(1, 2, first), Cue(3, 4, second), Cue(5, 6, third)], TimeSpan.Zero, readerIsTheFloor: true);
        window.Add(Epoch, (List<SubtitleCue>)[Cue(7, 8, fourth)], TimeSpan.FromSeconds(7), readerIsTheFloor: true);

        Assert.That(first.Disposed, Is.True, "what the reader has passed is what the budget may take");
    }

    [Test]
    public void CountsTextAgainstTheBudgetToo()
    {
        // A picture is not the only thing a track can cost. Text is cheap per event but a track can hold tens of
        // thousands of them, so the words and the animation instants they name are counted as well.
        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[Cue(1, 2, "a line of subtitles")], TimeSpan.Zero);

        long plain = window.ApproximateSize;

        SubtitleCueWindow withChanges = Window();
        withChanges.Add(
            Epoch,
            (List<SubtitleCue>)
            [
                new SubtitleCue
                {
                    Start = TimeSpan.FromSeconds(1),
                    End = TimeSpan.FromSeconds(2),
                    Text = "a line of subtitles",
                    Changes = (List<SubtitleChange>)[new SubtitleChange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), false)],
                },
            ],
            TimeSpan.Zero);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plain, Is.GreaterThan("a line of subtitles".Length * sizeof(char)), "the words are counted");
            Assert.That(withChanges.ApproximateSize, Is.GreaterThan(plain), "and so is each instant the line animates at");
        }
    }

    [Test]
    public void KeepsItsOrderWhenAnEventArrivesFromBeforeWhatIsHeld()
    {
        // A jump into a region the budget had given up re-reads events that belong before the ones still held, so the
        // store has to file them in the middle rather than at the end.
        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[Cue(10, 11), Cue(20, 21)], TimeSpan.FromSeconds(20));
        window.SeekTo(Epoch, TimeSpan.FromSeconds(5));
        window.Add(Epoch, (List<SubtitleCue>)[Cue(5, 6), Cue(7, 8)], TimeSpan.FromSeconds(5));

        Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(7.5))), Is.EqualTo(new[] { "7" }), "the event that was filed in the middle answers");

        window.SeekTo(Epoch, TimeSpan.FromSeconds(10.5));

        Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(10.5))), Is.EqualTo(new[] { "10" }), "and the ones that were already held are still in order behind it");
    }

    [Test]
    public void KeepsWhatItRetainsUnderTheBudget()
    {
        // A track long enough to cost far more than the budget is not allowed to keep it all: what has been passed goes,
        // oldest first, until the keeping fits again.
        string text = new('x', 2000);

        SubtitleCueWindow window = Window();
        for (int index = 0; index < 40_000; index++)
        {
            var start = TimeSpan.FromSeconds(index);
            window.Add(
                Epoch,
                (List<SubtitleCue>)[new SubtitleCue { Start = start, End = start + TimeSpan.FromMilliseconds(900), Text = text }],
                start,
                readerIsTheFloor: true);
        }

        Assert.That(window.ApproximateSize, Is.LessThanOrEqualTo(DefaultBudget), "the keeping stays within the budget");
    }

    [Test]
    public void EmptiesWhatHasBeenPassedWhenAJumpGoesPastIt()
    {
        string text = new('x', 2000);

        SubtitleCueWindow window = Window();
        for (int index = 0; index < 40_000; index++)
        {
            var start = TimeSpan.FromSeconds(index);
            window.Add(Epoch, (List<SubtitleCue>)[new SubtitleCue { Start = start, End = start + TimeSpan.FromMilliseconds(900), Text = text }], start);
        }

        window.SeekTo(Epoch, TimeSpan.FromSeconds(50_000));

        Assert.That(window.ApproximateSize, Is.LessThanOrEqualTo(DefaultBudget), "a jump past the whole backlog is what makes all of it forgettable at once");
    }

    [Test]
    public void KeepsTheDefaultBudgetUntilItIsToldOtherwise()
    {
        SubtitleCueWindow window = Window();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.Capacity, Is.EqualTo(DefaultBudget), "the budget is what the settings start at");

            window.Capacity = 1024L;
            Assert.That(window.Capacity, Is.EqualTo(8L * 1024 * 1024), "a budget too small to hold anything is not accepted");

            window.Capacity = 1L << 40;
            Assert.That(window.Capacity, Is.EqualTo(4096L * 1024 * 1024), "and neither is one beyond what the setting offers");
        }
    }

    [Test]
    public void FreesAtOnceWhenTheBudgetIsTurnedDown()
    {
        const long size = 12L * 1024 * 1024;
        WatchImage first = new(size);
        WatchImage second = new(size);

        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[Cue(1, 2, first)], TimeSpan.Zero);

        Assert.That(first.Disposed, Is.False, "a cue that is still up is never taken to meet the budget");

        window.Add(Epoch, (List<SubtitleCue>)[Cue(7, 8, second)], TimeSpan.FromSeconds(7));

        // Past both, so both may go: what stops them is the budget, not the playhead.
        using (window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(9)))
        {
        }

        Assert.That(first.Disposed, Is.False, "the two fit, so neither is let go of");

        window.Capacity = 8L * 1024 * 1024;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Disposed, Is.True, "lowering the budget frees at once rather than at the next cue");
            Assert.That(second.Disposed, Is.True);
            Assert.That(window.ApproximateSize, Is.LessThanOrEqualTo(8L * 1024 * 1024));
        }
    }

    [Test]
    public void WaitsUntilTheNextBoundaryThatCanChangeTheScreen()
    {
        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[Cue(1, 3), Cue(2, 4)], TimeSpan.Zero);

        using (Assert.EnterMultipleScope())
        {
            // Before anything is up, the answer is when the first cue starts.
            Assert.That(window.TimeUntilNextChange(TimeSpan.FromSeconds(0.5)), Is.EqualTo(TimeSpan.FromSeconds(0.5)));

            // With one up and another due, whichever comes first — here the second cue's start.
            Assert.That(window.TimeUntilNextChange(TimeSpan.FromSeconds(1.5)), Is.EqualTo(TimeSpan.FromSeconds(0.5)));

            // With everything on screen, the earliest end.
            Assert.That(window.TimeUntilNextChange(TimeSpan.FromSeconds(2.5)), Is.EqualTo(TimeSpan.FromSeconds(0.5)));

            // Past the last end there is nothing left to wait for.
            Assert.That(window.TimeUntilNextChange(TimeSpan.FromSeconds(5)), Is.Null);
        }
    }

    [Test]
    public void EndsAnOpenCueWhereTheNextEventBegins()
    {
        WatchImage image = new();
        // A bitmap display set says when it starts and never when it ends: libavcodec reports the end as unstated, and the
        // event that replaces it is the only thing that says how long it was up.
        SubtitleCueWindow window = Window();
        SubtitleCue first = OpenCue(2);
        window.Add(Epoch, (List<SubtitleCue>)[first], TimeSpan.Zero);

        Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(5))), Is.EqualTo(new[] { "2" }), "a cue with no stated end stays up");

        window.Add(Epoch, (List<SubtitleCue>)[OpenCue(6)], TimeSpan.FromSeconds(6));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.End, Is.EqualTo(TimeSpan.FromSeconds(6)), "the replacing event is where it ends");
            Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(5))), Is.EqualTo(new[] { "2" }), "and it was up until then");
            Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(6))), Is.EqualTo(new[] { "6" }), "only the new one is up after it");
        }
    }

    [Test]
    public void EndsAnOpenCueWhenTheTrackClearsTheScreen()
    {
        // A display set with no objects in it takes the picture away rather than putting something up, so it arrives as a
        // cue with nothing in it: never drawn, it exists to end what came before.
        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[OpenCue(2)], TimeSpan.Zero);

        window.Add(Epoch, (List<SubtitleCue>)[Unset(4)], TimeSpan.FromSeconds(4));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(3.5))), Is.EqualTo(new[] { "2" }), "up until the clear");
            Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(4))), Is.Empty, "gone from the clear onwards");
            Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(4.5))), Is.Empty, "and the clear draws nothing itself");
        }
    }

    [Test]
    public void CountsAnEventOnceWhenItIsReadAgainAfterItsEndWasSettled()
    {
        // A seek re-reads an event that a later one has since closed, so the copy that comes back says "no end stated"
        // where the one already held says when it ended. Matching on both would keep two copies of one line.
        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[OpenCue(2)], TimeSpan.Zero);
        window.Add(Epoch, (List<SubtitleCue>)[OpenCue(6)], TimeSpan.FromSeconds(4));

        window.Add(Epoch, (List<SubtitleCue>)[OpenCue(2)], TimeSpan.FromSeconds(5.5));

        Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(3))), Is.EqualTo(new[] { "2" }), "one copy, not two");
    }

    [Test]
    public void LeavesAStatedEndAlone()
    {
        // The two may only be replaced by what comes next when the track said nothing. A line written over another one is
        // a text cue's own business.
        SubtitleCueWindow window = Window();
        SubtitleCue first = Cue(1, 9);
        window.Add(Epoch, (List<SubtitleCue>)[first], TimeSpan.Zero);

        window.Add(Epoch, (List<SubtitleCue>)[Cue(3, 5)], TimeSpan.FromSeconds(3));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.End, Is.EqualTo(TimeSpan.FromSeconds(9)), "the end it stated is the end it keeps");
            Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(6))), Is.EqualTo(new[] { "1" }), "and it is still up after the other began");
        }
    }

    [Test]
    public void WaitsForAnOpenCueToBeReplacedRatherThanForItsEnd()
    {
        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[OpenCue(2)], TimeSpan.Zero);

        Assert.That(window.TimeUntilNextChange(TimeSpan.FromSeconds(3)), Is.GreaterThan(TimeSpan.FromDays(1)), "nothing is due to happen on its own");
    }

    [Test]
    public void DropsAnOpenCueThePlayheadJumpedPast()
    {
        // The event that would have ended this cue lies between where it started and where the playhead now is, so it is
        // never read: an open cue left alone would show over whatever the jump landed on.
        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[OpenCue(2)], TimeSpan.Zero);

        window.SeekTo(Epoch, TimeSpan.FromSeconds(90));

        Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(90.5))), Is.Empty, "a line the jump went past is not still up");
    }

    [Test]
    public void KeepsAnOpenCueThePlayheadHasNotReached()
    {
        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[OpenCue(200)], TimeSpan.Zero);

        window.SeekTo(Epoch, TimeSpan.FromSeconds(50));

        Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(201))), Is.EqualTo(new[] { "200" }), "a cue ahead of the jump is a fact about the file");
    }

    [Test]
    public void FreesWhatTheBudgetEvictedOnlyWhenTheLastSnapshotLetsGo()
    {
        // Evicting is not freeing: a cue the renderer is still drawing keeps its picture, and the last holder is what
        // actually lets it go.
        WatchImage image = new(BudgetSize);
        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[Cue(1, 2, image)], TimeSpan.Zero);

        SubtitleCueSnapshot snapshot = window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(1.5));
        Assert.That(image.Disposed, Is.False);

        // Passing the line makes it cold; the three that follow fill the budget again while staying ahead of the
        // playhead, so the cold one is what the budget takes.
        using (window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(5)))
        {
        }

        WatchImage ahead = new(BudgetSize);
        window.Add(Epoch, (List<SubtitleCue>)[Cue(10, 20, ahead), Cue(21, 30, new WatchImage(BudgetSize)), Cue(31, 40, new WatchImage(BudgetSize))], TimeSpan.FromSeconds(5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(image.Disposed, Is.False, "the snapshot is still drawing it");
            Assert.That(ahead.Disposed, Is.False, "and what is still ahead was not given up for the budget");
        }

        snapshot.Dispose();

        Assert.That(image.Disposed, Is.True, "and the last holder has let go");
    }

    [Test]
    public void StaysAliveUntilTheLastSnapshotIsDropped()
    {
        WatchImage image = new();
        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[Cue(1, 20, image)], TimeSpan.Zero);

        SubtitleCueSnapshot first = window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(2));
        SubtitleCueSnapshot second = window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(3));

        window.Reset();
        first.Dispose();

        Assert.That(image.Disposed, Is.False, "another snapshot is still reading it");

        second.Dispose();

        Assert.That(image.Disposed, Is.True, "the track going away frees nothing a caller is still using");
    }

    [Test]
    public void LetsGoOfAnEmptySnapshotTwice()
    {
        // Nothing to draw is the common case, and it is one shared object: releasing it must stay harmless.
        SubtitleCueWindow window = Window();

        SubtitleCueSnapshot snapshot = window.GetVisibleCues(Epoch, TimeSpan.Zero);
        snapshot.Dispose();
        snapshot.Dispose();

        Assert.That(snapshot.Cues, Is.Empty);
    }

    [Test]
    public void ForgetsEverythingWhenItIsReset()
    {
        WatchImage image = new();
        SubtitleCueWindow window = Window();
        window.Add(Epoch, (List<SubtitleCue>)[Cue(1, 3, image)], TimeSpan.Zero);

        window.Reset();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(2))), Is.Empty);
            Assert.That(image.Disposed, Is.True, "a reset is the track going away, so nothing is left to draw");
        }
    }

    /// <summary>
    /// A window that has been told where its reading starts, which is what the pipeline does before it reads anything: the
    /// window is never the one to decide which reading it is in.
    /// </summary>
    private static SubtitleCueWindow Window()
    {
        SubtitleCueWindow window = new();
        window.SeekTo(Epoch, TimeSpan.Zero);
        return window;
    }

    private static SubtitleCue Cue(double startSeconds, double endSeconds, string? text = null)
    {
        return new SubtitleCue
        {
            Start = TimeSpan.FromSeconds(startSeconds),
            End = TimeSpan.FromSeconds(endSeconds),
            Text = text ?? startSeconds.ToString("0.###"),
        };
    }

    /// <summary>A cue from a track that says when an event begins and leaves its end to the event after it.</summary>
    private static SubtitleCue OpenCue(double startSeconds, string? text = null)
    {
        return new SubtitleCue
        {
            Start = TimeSpan.FromSeconds(startSeconds),
            End = SubtitleCue.OpenEnd,
            Text = text ?? startSeconds.ToString("0.###"),
        };
    }

    /// <summary>An event with nothing in it, which is how a track takes its picture away.</summary>
    private static SubtitleCue Unset(double atSeconds)
    {
        return new SubtitleCue
        {
            Start = TimeSpan.FromSeconds(atSeconds),
            End = TimeSpan.FromSeconds(atSeconds),
        };
    }

    private static SubtitleCue Cue(double startSeconds, double endSeconds, WatchImage image)
    {
        return new SubtitleCue
        {
            Start = TimeSpan.FromSeconds(startSeconds),
            End = TimeSpan.FromSeconds(endSeconds),
            Text = startSeconds.ToString("0.###"),
            Image = new SubtitleImage
            {
                Buffer = image,
                Left = 0.0,
                Top = 0.0,
                Width = 1.0,
                Height = 1.0,
            },
        };
    }

    private static string[] Texts(SubtitleCueSnapshot snapshot)
    {
        using (snapshot)
        {
            return [.. snapshot.Cues.Select(cue => cue.Text!)];
        }
    }

    /// <summary>
    /// A cue owns its image, and disposing it is the only observable effect a cue has — so a stand-in image is what makes
    /// "still held" checkable at all.
    /// </summary>
    private sealed class WatchImage(long length = 1000) : ISubtitleImageBuffer
    {
        public long Length => length;

        public int Width => 1;

        public int Height => 1;

        public int Stride => 4;

        public nint Pixels => 0;

        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
