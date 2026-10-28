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
    public void DoesNotFreeACueTheInstantItStopsBeingVisible()
    {
        // The consumer is handed the cues themselves, so freeing one the moment it stops being visible would pull it out
        // from under a pass that is still drawing it.
        WatchImage image = new();
        SubtitleCueWindow window = new();
        window.Add([Cue(1, 2, image)], TimeSpan.Zero);

        window.Add([], TimeSpan.FromSeconds(2.5));

        Assert.That(image.Disposed, Is.False, "a cue just past its end is still the window's");
    }

    [Test]
    public void FreesWhatIsWellBehind()
    {
        WatchImage image = new();
        SubtitleCueWindow window = new();
        window.Add([Cue(1, 2, image)], TimeSpan.Zero);

        // Past the margin the window keeps for a consumer that is drawing right now.
        window.Add([], TimeSpan.FromSeconds(5));

        Assert.That(image.Disposed, Is.True, "nothing needs a cue seconds behind the playhead");
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

    private static string[] Texts(IReadOnlyList<SubtitleCue> cues)
    {
        return [.. cues.Select(cue => cue.Text!)];
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
