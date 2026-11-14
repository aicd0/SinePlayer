// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Subtitle;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// What an ASS line says about changing by itself, which is the difference between the pipeline sleeping until the next
/// cue and redrawing through this one. The tags are read once per cue, so every case here is a cue that would otherwise
/// be drawn once and freeze — or, just as wrong, a static line that would be redrawn sixty times a second.
/// </summary>
[TestFixture]
public class AssSubtitleChangesTests
{
    private static readonly TimeSpan sStart = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan sEnd = TimeSpan.FromSeconds(13);

    [Test]
    public void ATimedTagBecomesAWindowInsideTheCue()
    {
        SubtitleChange[]? changes = AssSubtitleChanges.Parse(@"0,Default,,0,0,0,,{\t(0,1000,\fs40)}Hello", sStart, sEnd);

        Assert.That(changes, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Has.Length.EqualTo(1), "one \t is one window");
            Assert.That(changes![0].Start, Is.EqualTo(sStart), "the window is absolute, counted from the cue's start");
            Assert.That(changes[0].End, Is.EqualTo(sStart + TimeSpan.FromSeconds(1)), "and it ends when the tag says it does");
            Assert.That(changes[0].Continuous, Is.True, "a \t changes throughout its window, not only at its start");
        }
    }

    [Test]
    public void AMoveWithItsOwnTimesUsesThem()
    {
        SubtitleChange[]? changes = AssSubtitleChanges.Parse(@"0,Default,,0,0,0,,{\move(0,0,100,100,500,1500)}Hello", sStart, sEnd);

        Assert.That(changes, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes![0].Start, Is.EqualTo(sStart + TimeSpan.FromMilliseconds(500)));
            Assert.That(changes[0].End, Is.EqualTo(sStart + TimeSpan.FromMilliseconds(1500)));
        }
    }

    [Test]
    public void AMoveWithoutTimesCoversTheWholeCue()
    {
        SubtitleChange[]? changes = AssSubtitleChanges.Parse(@"0,Default,,0,0,0,,{\move(0,0,100,100)}Hello", sStart, sEnd);

        Assert.That(changes, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes![0].Start, Is.EqualTo(sStart), "a move with no times moves for the line's whole life");
            Assert.That(changes[0].End, Is.EqualTo(sEnd));
        }
    }

    [Test]
    public void KaraokeTurnsOverAtItsSyllables()
    {
        SubtitleChange[]? changes = AssSubtitleChanges.Parse(@"0,Default,,0,0,0,,{\k20}He{\k30}llo", sStart, sEnd);

        Assert.That(changes, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Has.Length.EqualTo(2), "one turnover per syllable after the first");
            Assert.That(changes![0].Start, Is.EqualTo(sStart + TimeSpan.FromMilliseconds(200)), "the durations accumulate");
            Assert.That(changes[1].Start, Is.EqualTo(sStart + TimeSpan.FromMilliseconds(500)));
            Assert.That(changes[0].Continuous, Is.False, "a syllable turns over at a moment, it does not animate");
            Assert.That(changes[0].End, Is.EqualTo(changes[0].Start), "so its window takes no time");
        }

        // Which is exactly what a drawing has to notice: a window of no length is never "live" at a position, so the
        // question is whether the instant went by, and a drawing taken at it is up to date rather than one behind.
        SubtitleCue cue = new() { Start = sStart, End = sEnd, Text = "Hello", Changes = changes };
        TimeSpan turnover = sStart + TimeSpan.FromMilliseconds(200);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cue.HasChanges(turnover - TimeSpan.FromMilliseconds(16), turnover), Is.True, "the instant passing asks for a drawing");
            Assert.That(cue.HasChanges(turnover, turnover), Is.False, "the drawing taken at it does not ask for another");
            Assert.That(cue.HasChanges(turnover, turnover + TimeSpan.FromMilliseconds(16)), Is.False, "nor does the moment after");
            Assert.That(cue.HasChanges(turnover - TimeSpan.FromMilliseconds(16), turnover + TimeSpan.FromMilliseconds(1)), Is.True, "a late drawing still hears about the one it missed");
            Assert.That(cue.HasChanges(sStart, sStart + TimeSpan.FromMilliseconds(100)), Is.False, "before the first turnover nothing has changed");
        }
    }

    [Test]
    public void AStaticLineSaysNothingChanges()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(AssSubtitleChanges.Parse(@"0,Default,,0,0,0,,Hello", sStart, sEnd), Is.Null, "plain text is drawn once");
            Assert.That(AssSubtitleChanges.Parse(@"0,Default,,0,0,0,,{\an8\b1}Hello", sStart, sEnd), Is.Null, "and so is a line that only says where and how to draw it");
        }
    }

    [Test]
    public void ACueReportsWhetherAndWhenItsPictureChanges()
    {
        SubtitleCue cue = new()
        {
            Start = sStart,
            End = sEnd,
            Text = "Hello",
            Changes = AssSubtitleChanges.Parse(@"0,Default,,0,0,0,,{\t(1000,2000,\fs40)}Hello", sStart, sEnd),
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cue.HasChanges(sStart, sStart + TimeSpan.FromMilliseconds(500)), Is.False, "before the window the picture is still");
            Assert.That(cue.TimeUntilChange(sStart), Is.EqualTo(TimeSpan.FromSeconds(1)), "and the next change is the window opening");
            Assert.That(cue.HasChanges(sStart + TimeSpan.FromMilliseconds(1400), sStart + TimeSpan.FromMilliseconds(1500)), Is.True, "inside it the picture is moving");
            Assert.That(cue.TimeUntilChange(sStart + TimeSpan.FromMilliseconds(1500)), Is.EqualTo(TimeSpan.Zero), "so there is nothing to wait for");
            Assert.That(cue.HasChanges(sStart + TimeSpan.FromMilliseconds(1500), sStart + TimeSpan.FromMilliseconds(2500)), Is.True, "a drawing taken inside it is out of date by the next one");
            Assert.That(cue.HasChanges(sEnd, sEnd + TimeSpan.FromMilliseconds(1000)), Is.False, "after it the picture is still again");
            Assert.That(cue.TimeUntilChange(sEnd), Is.Null, "and nothing else will change it");
        }
    }
}
