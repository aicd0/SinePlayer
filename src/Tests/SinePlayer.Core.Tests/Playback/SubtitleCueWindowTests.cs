// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The window a subtitle track's decoded cues wait in. It belongs to the track rather than to a position: a cue is a
/// timestamped fact about the file, so what is on screen is the same question however the playhead got there.
/// </summary>
[TestFixture]
public class SubtitleCueWindowTests
{
    [Test]
    public void ShowsTheCuesThatCoverThePosition()
    {
        SubtitleCueWindow window = new();
        window.Add([Cue(1, 3), Cue(2, 4)], TimeSpan.Zero);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Texts(window.Visible(TimeSpan.FromSeconds(0.5))), Is.Empty, "before the first cue starts");
            Assert.That(Texts(window.Visible(TimeSpan.FromSeconds(2.5))), Is.EqualTo(new[] { "1", "2" }), "overlapping events are both up");
            Assert.That(Texts(window.Visible(TimeSpan.FromSeconds(3.5))), Is.EqualTo(new[] { "2" }), "the first has ended");
            Assert.That(Texts(window.Visible(TimeSpan.FromSeconds(4))), Is.Empty, "a cue's end is not its moment");
        }
    }

    [Test]
    public void KeepsACueThatIsStillOnScreenWhenThePlayheadMoves()
    {
        // What a forward seek costs without this: the cue's packet lies before the landing, so the re-read never delivers
        // it again and the text is lost until the cue's own end.
        SubtitleCueWindow window = new();
        window.Add([Cue(1, 20)], TimeSpan.Zero);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Texts(window.Visible(TimeSpan.FromSeconds(6))), Is.EqualTo(new[] { "1" }));
            Assert.That(Texts(window.Visible(TimeSpan.FromSeconds(15))), Is.EqualTo(new[] { "1" }));
            Assert.That(Texts(window.Visible(TimeSpan.FromSeconds(20))), Is.Empty);
        }
    }

    [Test]
    public void CountsTheSameEventOnceWhenItIsReadAgain()
    {
        // A seek re-reads from a keyframe, so the events ahead of the new position arrive a second time.
        SubtitleCueWindow window = new();
        window.Add([Cue(5, 9)], TimeSpan.Zero);
        window.Add([Cue(5, 9)], TimeSpan.FromSeconds(5));

        Assert.That(Texts(window.Visible(TimeSpan.FromSeconds(6))), Is.EqualTo(new[] { "5" }), "the same event twice is one cue");
    }

    [Test]
    public void TellsSimultaneousEventsApartByTheirText()
    {
        // Two speakers at once are written as two events over one span: the same times, different words.
        SubtitleCueWindow window = new();
        window.Add([Cue(5, 9, "left"), Cue(5, 9, "right")], TimeSpan.Zero);

        Assert.That(Texts(window.Visible(TimeSpan.FromSeconds(6))), Is.EqualTo(new[] { "left", "right" }));
    }

    [Test]
    public void DropsWhatIsBehindButNotWhatIsStillVisible()
    {
        SubtitleCueWindow window = new();
        window.Add([Cue(1, 2), Cue(3, 11)], TimeSpan.Zero);

        // Ten seconds on: the first cue is long gone, the second is still up, and a third arrives.
        window.Add([Cue(10, 12)], TimeSpan.FromSeconds(10));

        Assert.That(Texts(window.Visible(TimeSpan.FromSeconds(10.5))), Is.EqualTo(new[] { "3", "10" }), "the ended cue is gone, the running one stayed");
    }

    [Test]
    public void TrimsWhatTheRendererHasAskedPastEvenWithoutANewEvent()
    {
        // Asking is what drains, so a quiet stretch of the file does not have to wait for the track to say something else
        // before what has been passed is let go of.
        WatchImage image = new();
        SubtitleCueWindow window = new();
        window.Add([Cue(1, 2, image)], TimeSpan.Zero);

        using (window.Visible(TimeSpan.FromSeconds(5)))
        {
        }

        Assert.That(image.Disposed, Is.True, "the question past its end was enough");
    }

    [Test]
    public void MovesTheFloorBackwardsWithAJump()
    {
        // A backward jump asks about positions the window has already passed. If the floor stayed where it was, everything
        // the jump landed on would be discarded as already seen.
        SubtitleCueWindow window = new();
        window.Add([Cue(1, 5)], TimeSpan.Zero);

        using (window.Visible(TimeSpan.FromSeconds(90)))
        {
        }

        window.SkipTo(TimeSpan.FromSeconds(2));
        window.Add([Cue(2, 4)], TimeSpan.FromSeconds(2));

        Assert.That(Texts(window.Visible(TimeSpan.FromSeconds(3))), Is.EqualTo(new[] { "2" }), "the cue the jump landed on was kept");
    }

    [Test]
    public void KeepsACueUntilTheRendererHasAskedPastIt()
    {
        // The reader runs ahead of the renderer, so a cue that has ended is not necessarily one nobody is about to ask for:
        // the window keeps it until a question arrives that is past its end.
        WatchImage image = new();
        SubtitleCueWindow window = new();
        window.Add([Cue(1, 2, image)], TimeSpan.Zero);

        using (window.Visible(TimeSpan.FromSeconds(1.5)))
        {
        }

        window.Add([], TimeSpan.FromSeconds(2.5));

        Assert.That(image.Disposed, Is.False, "nothing has asked about a position past the cue's end yet");

        using (window.Visible(TimeSpan.FromSeconds(3)))
        {
        }

        Assert.That(image.Disposed, Is.True, "asking past it is what lets the window drop it");
    }

    [Test]
    public void FreesWhatNobodyCanAskForWithoutARenderer()
    {
        // Nothing draws the cues, so nothing will ever ask: the read position is the floor, and a cue the reader has
        // passed is gone. Without that, a window nobody drains would keep every cue it was ever given.
        WatchImage image = new();
        SubtitleCueWindow window = new();
        window.Add([Cue(1, 2, image)], TimeSpan.Zero, hasConsumer: false);

        window.Add([], TimeSpan.FromSeconds(5), hasConsumer: false);

        Assert.That(image.Disposed, Is.True, "no renderer means no future question to wait for");
    }

    [Test]
    public void WaitsUntilTheNextBoundaryThatCanChangeTheScreen()
    {
        SubtitleCueWindow window = new();
        window.Add([Cue(1, 3), Cue(2, 4)], TimeSpan.Zero);

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
        SubtitleCueWindow window = new();
        SubtitleCue first = OpenCue(2);
        window.Add([first], TimeSpan.Zero);

        Assert.That(Texts(window.Visible(TimeSpan.FromSeconds(5))), Is.EqualTo(new[] { "2" }), "a cue with no stated end stays up");

        window.Add([OpenCue(6)], TimeSpan.FromSeconds(6));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.End, Is.EqualTo(TimeSpan.FromSeconds(6)), "the replacing event is where it ends");
            Assert.That(Texts(window.Visible(TimeSpan.FromSeconds(5))), Is.EqualTo(new[] { "2" }), "and it was up until then");
            Assert.That(Texts(window.Visible(TimeSpan.FromSeconds(6))), Is.EqualTo(new[] { "6" }), "only the new one is up after it");
        }
    }

    [Test]
    public void EndsAnOpenCueWhenTheTrackClearsTheScreen()
    {
        // A display set with no objects in it takes the picture away rather than putting something up, so it arrives as a
        // cue with nothing in it: never drawn, it exists to end what came before.
        SubtitleCueWindow window = new();
        window.Add([OpenCue(2)], TimeSpan.Zero);

        window.Add([Unset(4)], TimeSpan.FromSeconds(4));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Texts(window.Visible(TimeSpan.FromSeconds(3.5))), Is.EqualTo(new[] { "2" }), "up until the clear");
            Assert.That(Texts(window.Visible(TimeSpan.FromSeconds(4))), Is.Empty, "gone from the clear onwards");
            Assert.That(Texts(window.Visible(TimeSpan.FromSeconds(4.5))), Is.Empty, "and the clear draws nothing itself");
        }
    }

    [Test]
    public void CountsAnEventOnceWhenItIsReadAgainAfterItsEndWasSettled()
    {
        // A seek re-reads an event that a later one has since closed, so the copy that comes back says "no end stated"
        // where the one already held says when it ended. Matching on both would keep two copies of one line.
        SubtitleCueWindow window = new();
        window.Add([OpenCue(2)], TimeSpan.Zero);
        window.Add([OpenCue(6)], TimeSpan.FromSeconds(4));

        window.Add([OpenCue(2)], TimeSpan.FromSeconds(5.5));

        Assert.That(Texts(window.Visible(TimeSpan.FromSeconds(3))), Is.EqualTo(new[] { "2" }), "one copy, not two");
    }

    [Test]
    public void LeavesAStatedEndAlone()
    {
        // The two may only be replaced by what comes next when the track said nothing. A line written over another one is
        // a text cue's own business.
        SubtitleCueWindow window = new();
        SubtitleCue first = Cue(1, 9);
        window.Add([first], TimeSpan.Zero);

        window.Add([Cue(3, 5)], TimeSpan.FromSeconds(3));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.End, Is.EqualTo(TimeSpan.FromSeconds(9)), "the end it stated is the end it keeps");
            Assert.That(Texts(window.Visible(TimeSpan.FromSeconds(6))), Is.EqualTo(new[] { "1" }), "and it is still up after the other began");
        }
    }

    [Test]
    public void WaitsForAnOpenCueToBeReplacedRatherThanForItsEnd()
    {
        SubtitleCueWindow window = new();
        window.Add([OpenCue(2)], TimeSpan.Zero);

        Assert.That(window.TimeUntilNextChange(TimeSpan.FromSeconds(3)), Is.GreaterThan(TimeSpan.FromDays(1)), "nothing is due to happen on its own");
    }

    [Test]
    public void DropsAnOpenCueThePlayheadJumpedPast()
    {
        // The event that would have ended this cue lies between where it started and where the playhead now is, so it is
        // never read: an open cue left alone would show over whatever the jump landed on.
        SubtitleCueWindow window = new();
        window.Add([OpenCue(2)], TimeSpan.Zero);

        window.SkipTo(TimeSpan.FromSeconds(90));

        Assert.That(Texts(window.Visible(TimeSpan.FromSeconds(90.5))), Is.Empty, "a line the jump went past is not still up");
    }

    [Test]
    public void KeepsAnOpenCueThePlayheadHasNotReached()
    {
        SubtitleCueWindow window = new();
        window.Add([OpenCue(200)], TimeSpan.Zero);

        window.SkipTo(TimeSpan.FromSeconds(50));

        Assert.That(Texts(window.Visible(TimeSpan.FromSeconds(201))), Is.EqualTo(new[] { "200" }), "a cue ahead of the jump is a fact about the file");
    }

    [Test]
    public void KeepsACueAliveWhileASnapshotHoldsIt()
    {
        // What the deleted one-second margin used to be an estimate of: the window stops offering a cue once a question has
        // passed it, but a renderer may still be drawing it. The hold is what says so, so the picture survives.
        WatchImage image = new();
        SubtitleCueWindow window = new();
        window.Add([Cue(1, 2, image)], TimeSpan.Zero);

        SubtitleCueSnapshot snapshot = window.Visible(TimeSpan.FromSeconds(1.5));

        using (window.Visible(TimeSpan.FromSeconds(5)))
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
        SubtitleCueWindow window = new();
        window.Add([Cue(1, 20, image)], TimeSpan.Zero);

        SubtitleCueSnapshot first = window.Visible(TimeSpan.FromSeconds(2));
        SubtitleCueSnapshot second = window.Visible(TimeSpan.FromSeconds(3));

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
        SubtitleCueWindow window = new();

        SubtitleCueSnapshot snapshot = window.Visible(TimeSpan.Zero);
        snapshot.Dispose();
        snapshot.Dispose();

        Assert.That(snapshot.Cues, Is.Empty);
    }

    [Test]
    public void ForgetsEverythingWhenItIsReset()
    {
        WatchImage image = new();
        SubtitleCueWindow window = new();
        window.Add([Cue(1, 3, image)], TimeSpan.Zero);

        window.Reset();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Texts(window.Visible(TimeSpan.FromSeconds(2))), Is.Empty);
            Assert.That(image.Disposed, Is.True, "a reset is the track going away, so nothing is left to draw");
        }
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
