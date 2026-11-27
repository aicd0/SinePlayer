// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Test;

namespace SinePlayer.Core.Tests;

/// <summary>
/// The name rule that decides which external subtitle belongs to a media file. It compares only what comes before
/// the first '.', so a language suffix such as ".sc" never keeps a subtitle from being picked up.
/// </summary>
public class SubtitleFileNameHelperTests
{
    [Test]
    public void ASubtitleWithALanguageSuffixBelongsToTheMediaOfTheSameBaseName()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SubtitleFileNameHelper.HasSameSubtitleBaseName(
                    @"C:\Media\[DBD-Raws][Fate Kaleid Liner Prisma Illya 2wei!][01][1080P][BDRip][HEVC-10bit][FLACx2].sc.ass",
                    @"C:\Media\[DBD-Raws][Fate Kaleid Liner Prisma Illya 2wei!][01][1080P][BDRip][HEVC-10bit][FLACx2].mkv"), Is.True);
            Assert.That(SubtitleFileNameHelper.HasSameSubtitleBaseName(@"C:\Media\Movie.2020.1080p.zh-Hans.ass", @"C:\Media\Movie.2020.1080p.mkv"), Is.True, "any number of dots may follow the base name");
            Assert.That(SubtitleFileNameHelper.HasSameSubtitleBaseName(@"C:\Media\movie.ass", @"C:\Media\MOVIE.mkv"), Is.True, "the comparison ignores case");
            Assert.That(SubtitleFileNameHelper.HasSameSubtitleBaseName(@"C:\Media\Subtitle.mkv", @"C:\Media\Subtitle"), Is.True, "the media need not carry an extension");
            Assert.That(SubtitleFileNameHelper.HasSameSubtitleBaseName(@"C:\Media\Subtitle.extended.mkv", @"C:\Media\Subtitle"), Is.True, "nor may the subtitle add one");
        }
    }

    [Test]
    public void ASubtitleOfAnotherEpisodeIsNotImported()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SubtitleFileNameHelper.HasSameSubtitleBaseName(@"C:\Media\Show01.ass", @"C:\Media\Show02.mkv"), Is.False);
            Assert.That(SubtitleFileNameHelper.HasSameSubtitleBaseName(@"C:\Media\Movie.ass", @"C:\Media\Movie 2.mkv"), Is.False);
        }
    }

    [Test]
    public void AnEpisodeNumberAfterTheFirstDotTakesNoPartInTheComparison()
    {
        Assert.That(SubtitleFileNameHelper.HasSameSubtitleBaseName(@"C:\Media\Show.01.ass", @"C:\Media\Show.02.mkv"), Is.True, "the first dot ends the name, so a dotted episode number reads as the same release");
    }

    [Test]
    public void AnEmptyBaseNameMatchesNothing()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SubtitleFileNameHelper.HasSameSubtitleBaseName(@"C:\Media\.ass", @"C:\Media\.mkv"), Is.False, "a name that starts with a dot has no base name, so it cannot be claimed by every such file");
            Assert.That(SubtitleFileNameHelper.HasSameSubtitleBaseName(@"C:\Media\.ass", @"C:\Media\Movie.mkv"), Is.False);
            Assert.That(SubtitleFileNameHelper.HasSameSubtitleBaseName("", ""), Is.False);
        }
    }
}
