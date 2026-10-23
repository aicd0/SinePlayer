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
}
