// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The ASS dialogue line every text subtitle is normalised to. The parser is the one place its shape is known, so what
/// it does with the fields, the tags and the line breaks is what a renderer can rely on.
/// </summary>
[TestFixture]
public class AssSubtitleTextTests
{
    [Test]
    public void ReadsTheFieldsOfADialogueLine()
    {
        var text = AssSubtitleText.Parse("7,2,Default,,10,20,30,,Hello");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(text.Layer, Is.EqualTo(2));
            Assert.That(text.Style, Is.EqualTo("Default"));
            Assert.That(text.Lines, Is.EqualTo(new[] { "Hello" }));
            Assert.That(text.Alignment, Is.EqualTo(2), "bottom centre is what a line without a position tag gets");
        }
    }

    [Test]
    public void KeepsCommasThatBelongToTheWords()
    {
        var text = AssSubtitleText.Parse("0,0,Default,,0,0,0,,Well, yes, and no");

        Assert.That(text.Lines, Is.EqualTo(new[] { "Well, yes, and no" }), "the ninth field is the whole rest of the line");
    }

    [Test]
    public void ShowsTheWordsAndNotTheTags()
    {
        var text = AssSubtitleText.Parse("0,0,Default,,0,0,0,,{\\i1}Hello{\\i0} there");

        Assert.That(text.Lines, Is.EqualTo(new[] { "Hello there" }));
    }

    [Test]
    public void BreaksALineWhereItSaysItBreaks()
    {
        var text = AssSubtitleText.Parse("0,0,Default,,0,0,0,,First\\NSecond\\NThird");

        Assert.That(text.Lines, Is.EqualTo(new[] { "First", "Second", "Third" }), "\\N is a line break and it is stacked");
    }

    [Test]
    public void TakesTheAnchorTheLineItselfAsksFor()
    {
        // The written position wins over any style table, which is the only source a decoder can see.
        var text = AssSubtitleText.Parse("0,0,Default,,0,0,0,,{\\an8}Above the picture");

        Assert.That(text.Alignment, Is.EqualTo(8));
    }

    [Test]
    public void KeepsTheRawTextForARendererThatCanDoMore()
    {
        var text = AssSubtitleText.Parse("0,0,Default,,0,0,0,,{\\i1}Hello");

        Assert.That(text.Raw, Is.EqualTo("{\\i1}Hello"), "the tags survive, so styling is not lost where it is not understood");
    }

    [Test]
    public void DoesNotThrowOnWhatItCannotUnderstand()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(AssSubtitleText.Parse(null).IsEmpty, Is.True);
            Assert.That(AssSubtitleText.Parse("").IsEmpty, Is.True);
            Assert.That(AssSubtitleText.Parse("no fields at all").Lines, Is.EqualTo(new[] { "no fields at all" }), "the words are recovered from a line of the wrong shape");
            Assert.That(AssSubtitleText.Parse("0,0,Default,,0,0,0,,{\\i1unclosed").Lines, Is.Empty, "an unclosed block swallows the rest rather than drawing tags as words");
        }
    }

    [Test]
    public void TreatsAnEventOfOnlySpacesAsNothingToDraw()
    {
        // A graphics event of a stream that was not recognised as bitmap arrives with no words at all.
        Assert.That(AssSubtitleText.Parse("0,0,Default,,0,0,0,,   ").IsEmpty, Is.True);
    }
}
