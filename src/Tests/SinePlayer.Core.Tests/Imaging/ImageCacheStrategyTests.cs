// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Imaging;

namespace SinePlayer.Core.Tests.Imaging;

/// <summary>
/// Says which cache entries can hold a picture of a given resolution: the smallest that is big enough first, and none
/// when both are smaller.
/// </summary>
[TestFixture]
public class ImageCacheStrategyTests
{
    [Test]
    public void OffersEveryEntryBigEnoughToHoldTheResolution()
    {
        Assert.That(ImageCacheStrategy.CalculateCacheEntryKeys(200L * 100), Is.EqualTo(new[] { "100k", "300k" }));
        Assert.That(ImageCacheStrategy.CalculateCacheEntryKeys(400L * 400), Is.EqualTo(new[] { "300k" }));
        Assert.That(ImageCacheStrategy.CalculateCacheEntryKeys(1200L * 1000), Is.Empty);
    }

    [Test]
    public void OffersAnEntryWhoseResolutionIsExactlyTheOneAskedFor()
    {
        Assert.That(ImageCacheStrategy.CalculateCacheEntryKeys(100000), Is.EqualTo(new[] { "100k", "300k" }));
        Assert.That(ImageCacheStrategy.CalculateCacheEntryKeys(100001), Is.EqualTo(new[] { "300k" }));
        Assert.That(ImageCacheStrategy.CalculateCacheEntryKeys(300000), Is.EqualTo(new[] { "300k" }));
        Assert.That(ImageCacheStrategy.CalculateCacheEntryKeys(300001), Is.Empty);
    }
}
