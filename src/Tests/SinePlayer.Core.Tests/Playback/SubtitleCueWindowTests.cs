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

    [Test]
    public void ShowsTheCuesThatCoverThePosition()
    {
        SubtitleCueWindow window = Window();
        window.Add(Epoch, [Cue(1, 3), Cue(2, 4)], TimeSpan.Zero);

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
        window.Add(Epoch, [Cue(1, 20)], TimeSpan.Zero);

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
        window.Add(Epoch, [Cue(5, 9)], TimeSpan.Zero);
        window.Add(Epoch, [Cue(5, 9)], TimeSpan.FromSeconds(5));

        Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(6))), Is.EqualTo(new[] { "5" }), "the same event twice is one cue");
    }

    [Test]
    public void TellsSimultaneousEventsApartByTheirText()
    {
        // Two speakers at once are written as two events over one span: the same times, different words.
        SubtitleCueWindow window = Window();
        window.Add(Epoch, [Cue(5, 9, "left"), Cue(5, 9, "right")], TimeSpan.Zero);

        Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(6))), Is.EqualTo(new[] { "left", "right" }));
    }

    [Test]
    public void DropsWhatIsBehindButNotWhatIsStillVisible()
    {
        SubtitleCueWindow window = Window();
        window.Add(Epoch, [Cue(1, 2), Cue(3, 11)], TimeSpan.Zero);

        // Ten seconds on: the first cue is long gone, the second is still up, and a third arrives.
        window.Add(Epoch, [Cue(10, 12)], TimeSpan.FromSeconds(10));

        Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(10.5))), Is.EqualTo(new[] { "3", "10" }), "the ended cue is gone, the running one stayed");
    }

    [Test]
    public void KeepsItsCuesInTimeOrderWhenAJumpGoesBack()
    {
        // A jump back leaves the cues that are still ahead of the landing where they were, and what the re-read delivers
        // begins before them: appending would leave the window holding a timeline whose later end comes first.
        SubtitleCueWindow window = Window();
        window.Add(Epoch, [Cue(10, 20)], TimeSpan.Zero);

        window.SeekTo(Epoch, TimeSpan.FromSeconds(5));
        window.Add(Epoch, [Cue(5, 12)], TimeSpan.FromSeconds(5));

        Assert.That(
            Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(11))),
            Is.EqualTo(new[] { "5", "10" }),
            "the cue that begins first comes first, however the playhead got there");
    }

    [Test]
    public void LetsGoOfAStaleCueThatIsNotTheFirst()
    {
        // Cues are ordered by where they begin, not by where they end: a line that began early can outlast one that began
        // after it, so a sweep that stopped at the first cue still up would keep the ended one for ever.
        WatchImage image = new();
        SubtitleCueWindow window = Window();
        window.Add(Epoch, [Cue(1, 20), Cue(2, 3, image)], TimeSpan.Zero);

        using (window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(5)))
        {
        }

        Assert.That(image.Disposed, Is.True, "the ended cue is let go of even though a longer one began before it");
    }

    [Test]
    public void TrimsWhatTheRendererHasAskedPastEvenWithoutANewEvent()
    {
        // Asking is what drains, so a quiet stretch of the file does not have to wait for the track to say something else
        // before what has been passed is let go of.
        WatchImage image = new();
        SubtitleCueWindow window = Window();
        window.Add(Epoch, [Cue(1, 2, image)], TimeSpan.Zero);

        using (window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(5)))
        {
        }

        Assert.That(image.Disposed, Is.True, "the question past its end was enough");
    }

    [Test]
    public void IgnoresAQuestionAboutTheReadingAJumpAbandoned()
    {
        // What this pins: the renderer asks about the position its clock still reports, and the clock is only repositioned
        // after the jump has already started a new reading. So the first question after a backward jump is about where the
        // playhead *was* — and answering it used to move the floor past everything the re-read was about to deliver, which
        // lost the cue at the top of the file for as long as it took to reach the old position again.
        SubtitleCueWindow window = Window();
        window.Add(Epoch, [Cue(1, 3)], TimeSpan.Zero);

        using (window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(60)))
        {
        }

        const long moved = Epoch + 1;
        window.SeekTo(moved, TimeSpan.Zero);

        Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(60))), Is.Empty, "a question from the reading that was abandoned is not answered");

        window.Add(moved, [Cue(1, 3)], TimeSpan.Zero);

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

        window.Add(6, [Cue(4, 6, image)], TimeSpan.FromSeconds(4));

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
        window.Add(Epoch, [Cue(1, 5)], TimeSpan.Zero);

        using (window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(90)))
        {
        }

        window.SeekTo(Epoch, TimeSpan.FromSeconds(2));
        window.Add(Epoch, [Cue(2, 4)], TimeSpan.FromSeconds(2));

        Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(3))), Is.EqualTo(new[] { "2" }), "the cue the jump landed on was kept");
    }

    [Test]
    public void KeepsACueUntilTheRendererHasAskedPastIt()
    {
        // The reader runs ahead of the renderer, so a cue that has ended is not necessarily one nobody is about to ask for:
        // the window keeps it until a question arrives that is past its end.
        WatchImage image = new();
        SubtitleCueWindow window = Window();
        window.Add(Epoch, [Cue(1, 2, image)], TimeSpan.Zero);

        using (window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(1.5)))
        {
        }

        window.Add(Epoch, [], TimeSpan.FromSeconds(2.5));

        Assert.That(image.Disposed, Is.False, "nothing has asked about a position past the cue's end yet");

        using (window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(3)))
        {
        }

        Assert.That(image.Disposed, Is.True, "asking past it is what lets the window drop it");
    }

    [Test]
    public void FreesWhatNobodyCanAskForWithoutARenderer()
    {
        // Nothing draws the cues, so nothing will ever ask: the caller's position is the floor, and a cue the reader has
        // passed is gone. Without that, a window nobody drains would keep every cue it was ever given.
        WatchImage image = new();
        SubtitleCueWindow window = Window();
        window.Add(Epoch, [Cue(1, 2, image)], TimeSpan.Zero, discardStale: true);

        window.Add(Epoch, [], TimeSpan.FromSeconds(5), discardStale: true);

        Assert.That(image.Disposed, Is.True, "no renderer means no future question to wait for");
    }

    [Test]
    public void WaitsUntilTheNextBoundaryThatCanChangeTheScreen()
    {
        SubtitleCueWindow window = Window();
        window.Add(Epoch, [Cue(1, 3), Cue(2, 4)], TimeSpan.Zero);

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
        window.Add(Epoch, [first], TimeSpan.Zero);

        Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(5))), Is.EqualTo(new[] { "2" }), "a cue with no stated end stays up");

        window.Add(Epoch, [OpenCue(6)], TimeSpan.FromSeconds(6));

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
        window.Add(Epoch, [OpenCue(2)], TimeSpan.Zero);

        window.Add(Epoch, [Unset(4)], TimeSpan.FromSeconds(4));

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
        window.Add(Epoch, [OpenCue(2)], TimeSpan.Zero);
        window.Add(Epoch, [OpenCue(6)], TimeSpan.FromSeconds(4));

        window.Add(Epoch, [OpenCue(2)], TimeSpan.FromSeconds(5.5));

        Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(3))), Is.EqualTo(new[] { "2" }), "one copy, not two");
    }

    [Test]
    public void LeavesAStatedEndAlone()
    {
        // The two may only be replaced by what comes next when the track said nothing. A line written over another one is
        // a text cue's own business.
        SubtitleCueWindow window = Window();
        SubtitleCue first = Cue(1, 9);
        window.Add(Epoch, [first], TimeSpan.Zero);

        window.Add(Epoch, [Cue(3, 5)], TimeSpan.FromSeconds(3));

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
        window.Add(Epoch, [OpenCue(2)], TimeSpan.Zero);

        Assert.That(window.TimeUntilNextChange(TimeSpan.FromSeconds(3)), Is.GreaterThan(TimeSpan.FromDays(1)), "nothing is due to happen on its own");
    }

    [Test]
    public void DropsAnOpenCueThePlayheadJumpedPast()
    {
        // The event that would have ended this cue lies between where it started and where the playhead now is, so it is
        // never read: an open cue left alone would show over whatever the jump landed on.
        SubtitleCueWindow window = Window();
        window.Add(Epoch, [OpenCue(2)], TimeSpan.Zero);

        window.SeekTo(Epoch, TimeSpan.FromSeconds(90));

        Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(90.5))), Is.Empty, "a line the jump went past is not still up");
    }

    [Test]
    public void KeepsAnOpenCueThePlayheadHasNotReached()
    {
        SubtitleCueWindow window = Window();
        window.Add(Epoch, [OpenCue(200)], TimeSpan.Zero);

        window.SeekTo(Epoch, TimeSpan.FromSeconds(50));

        Assert.That(Texts(window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(201))), Is.EqualTo(new[] { "200" }), "a cue ahead of the jump is a fact about the file");
    }

    [Test]
    public void KeepsACueAliveWhileASnapshotHoldsIt()
    {
        // What the deleted one-second margin used to be an estimate of: the window stops offering a cue once a question has
        // passed it, but a renderer may still be drawing it. The hold is what says so, so the picture survives.
        WatchImage image = new();
        SubtitleCueWindow window = Window();
        window.Add(Epoch, [Cue(1, 2, image)], TimeSpan.Zero);

        SubtitleCueSnapshot snapshot = window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(1.5));

        using (window.GetVisibleCues(Epoch, TimeSpan.FromSeconds(5)))
        {
        }

        Assert.That(image.Disposed, Is.False, "the snapshot is still reading it");

        snapshot.Dispose();

        Assert.That(image.Disposed, Is.True, "and the last holder has let go");
    }

    [Test]
    public void StaysAliveUntilTheLastSnapshotIsDropped()
    {
        WatchImage image = new();
        SubtitleCueWindow window = Window();
        window.Add(Epoch, [Cue(1, 20, image)], TimeSpan.Zero);

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
        window.Add(Epoch, [Cue(1, 3, image)], TimeSpan.Zero);

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
    private sealed class WatchImage : ISubtitleImageBuffer
    {
        public long Length => 1000;

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
