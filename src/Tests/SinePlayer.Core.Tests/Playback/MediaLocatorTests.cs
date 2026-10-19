// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Tests.Playback;

[TestFixture]
public class MediaLocatorTests
{
    [Test]
    public void ParseWindowsPathUsesFileScheme()
    {
        var locator = MediaLocator.Parse(@"C:\media\movie.mkv");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(locator.Scheme, Is.EqualTo(MediaLocator.SchemeFile));
            Assert.That(locator.Value, Is.EqualTo(@"C:\media\movie.mkv"));
            Assert.That(locator.IsFile, Is.True);
        }
    }

    [Test]
    public void ParseForwardSlashDrivePathUsesFileScheme()
    {
        var locator = MediaLocator.Parse("D:/media/movie.mkv");

        Assert.That(locator.Scheme, Is.EqualTo(MediaLocator.SchemeFile));
    }

    [Test]
    public void ParseUncPathUsesFileScheme()
    {
        var locator = MediaLocator.Parse(@"\\server\share\movie.mkv");

        Assert.That(locator.Scheme, Is.EqualTo(MediaLocator.SchemeFile));
    }

    [Test]
    public void ParseRelativePathUsesFileScheme()
    {
        var locator = MediaLocator.Parse("movie.mkv");

        Assert.That(locator.Scheme, Is.EqualTo(MediaLocator.SchemeFile));
    }

    [Test]
    public void ParseHttpUrlUsesHttpScheme()
    {
        var locator = MediaLocator.Parse("http://example.com/movie.mkv");

        Assert.That(locator.Scheme, Is.EqualTo("http"));
    }

    [Test]
    public void ParseHttpsUrlUsesHttpsScheme()
    {
        var locator = MediaLocator.Parse("https://example.com/movie.mkv?token=1");

        Assert.That(locator.Scheme, Is.EqualTo("https"));
    }

    [Test]
    public void ParseUppercaseSchemeIsLowered()
    {
        var locator = MediaLocator.Parse("HTTP://example.com/movie.mkv");

        Assert.That(locator.Scheme, Is.EqualTo("http"));
    }

    [Test]
    public void ParseRejectsEmptyValue()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ArgumentException>(() => MediaLocator.Parse(string.Empty));
            Assert.Throws<ArgumentException>(() => MediaLocator.Parse("   "));
        }
    }
}
