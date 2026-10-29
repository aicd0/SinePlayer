// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.DebugTools;

namespace SinePlayer.Core.Tests;

[TestFixture]
public class LoggerTests
{
    [Test]
    public void IsTagMatch_SelectsItselfAndDescendants()
    {
        Assert.That(Logger.IsTagMatch("A", "A"), Is.True);
        Assert.That(Logger.IsTagMatch("A", "A/B"), Is.True);
        Assert.That(Logger.IsTagMatch("A", "A/B/C"), Is.True);
        Assert.That(Logger.IsTagMatch("A/B", "A/B"), Is.True);
        Assert.That(Logger.IsTagMatch("A/B", "A/B/C"), Is.True);
    }

    [Test]
    public void IsTagMatch_DoesNotSelectAncestorsOrSiblings()
    {
        Assert.That(Logger.IsTagMatch("A/B", "A"), Is.False);
        Assert.That(Logger.IsTagMatch("A", "AB"), Is.False);
        Assert.That(Logger.IsTagMatch("A", "B"), Is.False);
        Assert.That(Logger.IsTagMatch("A/B", "A/BC"), Is.False);
        Assert.That(Logger.IsTagMatch("A", "a"), Is.False);
    }

    [Test]
    public void KeepsAPlainTagLevelAsItsFolderName()
    {
        Assert.That(LogTree.ToName("PlaybackLogger"), Is.EqualTo("PlaybackLogger"));
        Assert.That(LogTree.ToName("PlaybackPipeline"), Is.EqualTo("PlaybackPipeline"));
    }

    /// <summary>
    /// A level is a folder below the tree, so a level that names this one's parent, this folder, or nothing at all
    /// cannot be one: ".." would put the log a directory up, and "." would put it where the tree itself lives.
    /// </summary>
    [Test]
    public void RefusesATagLevelThatNamesAFolderInsteadOfBeingOne()
    {
        Assert.That(LogTree.ToName(".."), Is.Empty);
        Assert.That(LogTree.ToName("."), Is.Empty);
        Assert.That(LogTree.ToName(""), Is.Empty);
        Assert.That(LogTree.ToName("..."), Is.Empty);
        Assert.That(LogTree.ToName("   "), Is.Empty);
    }

    /// <summary>
    /// The characters a name may not hold become an underscore rather than a path: a separator would make one level
    /// into two, and a colon makes a drive or a second stream rather than part of a name.
    /// </summary>
    [Test]
    public void MakesASeparatorOrAReservedCharacterPartOfTheName()
    {
        Assert.That(LogTree.ToName("A\\B"), Is.EqualTo("A_B"));
        Assert.That(LogTree.ToName("A:B"), Is.EqualTo("A_B"));
        Assert.That(LogTree.ToName("A*B?C"), Is.EqualTo("A_B_C"));
        Assert.That(LogTree.ToName("A<B>C|D"), Is.EqualTo("A_B_C_D"));
    }

    /// <summary>Windows strips a trailing dot or space, so a level that keeps one would share a folder with another.</summary>
    [Test]
    public void DropsTheTrailingDotsAndSpacesWindowsWouldDropAnyway()
    {
        Assert.That(LogTree.ToName("A."), Is.EqualTo("A"));
        Assert.That(LogTree.ToName("A "), Is.EqualTo("A"));
        Assert.That(LogTree.ToName("A. . "), Is.EqualTo("A"));
    }

    /// <summary>A device name opens the device whatever folder it is in, so it cannot be a folder either.</summary>
    [Test]
    public void RefusesADeviceName()
    {
        Assert.That(LogTree.ToName("CON"), Is.Empty);
        Assert.That(LogTree.ToName("nul"), Is.Empty);
        Assert.That(LogTree.ToName("Com1"), Is.Empty);
        Assert.That(LogTree.ToName("LPT9"), Is.Empty);

        // Only the names themselves, not every word that starts like one.
        Assert.That(LogTree.ToName("CONSOLE"), Is.EqualTo("CONSOLE"));
        Assert.That(LogTree.ToName("COM0"), Is.EqualTo("COM0"));
        Assert.That(LogTree.ToName("COMPONENT"), Is.EqualTo("COMPONENT"));
    }

    /// <summary>A level is bounded so that the tree stays inside a path however long a tag someone writes.</summary>
    [Test]
    public void BoundsTheLengthOfALevel()
    {
        Assert.That(LogTree.ToName(new string('A', 500)).Length, Is.LessThanOrEqualTo(48));
        Assert.That(LogTree.ToName(new string('A', 500)), Is.EqualTo(new string('A', 48)));
    }

    /// <summary>
    /// One level is one name: the split into levels happens before this, so a separator that still arrives here is
    /// part of the text rather than a folder boundary.
    /// </summary>
    [Test]
    public void NeverLetsOneLevelBecomeTwo()
    {
        Assert.That(LogTree.ToName("SomeTag/InnerTag"), Is.EqualTo("SomeTag_InnerTag"));
        Assert.That(LogTree.ToName("SomeTag\\InnerTag"), Is.EqualTo("SomeTag_InnerTag"));
    }

    /// <summary>
    /// The path a tag is written under: one folder per level, and a level that names no folder is skipped rather than
    /// becoming a step upwards. This is the mapping both the write side and the read side use, which is why it is the
    /// one to prove: ".." resolves to nothing here, so there is nowhere for a walk out of the tree to start.
    /// </summary>
    [Test]
    public void BuildsTheTreePathFromTheLevelsThatNameOne()
    {
        Assert.That(LogTree.GetRelativePath("PlaybackLogger"), Is.EqualTo("PlaybackLogger"));
        Assert.That(LogTree.GetRelativePath("PlaybackLogger/PlaybackPipeline"), Is.EqualTo(@"PlaybackLogger\PlaybackPipeline"));
        Assert.That(LogTree.GetRelativePath("PlaybackLogger/.."), Is.EqualTo("PlaybackLogger"));
        Assert.That(LogTree.GetRelativePath(".."), Is.EqualTo("Empty"));
        Assert.That(LogTree.GetRelativePath("../../etc"), Is.EqualTo("etc"));
        Assert.That(LogTree.GetRelativePath("PlaybackLogger/../../x"), Is.EqualTo(@"PlaybackLogger\x"));
        Assert.That(LogTree.GetRelativePath("A:B/C*D"), Is.EqualTo(@"A_B\C_D"));
        Assert.That(LogTree.GetRelativePath(string.Empty), Is.EqualTo("Empty"));
    }
}
