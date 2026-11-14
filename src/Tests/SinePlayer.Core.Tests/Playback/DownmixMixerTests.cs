// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Backends.Downmix;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The mixer's table is the only place an arrangement's groups are written down, so what it says about each one is
/// checked directly: the labels a mixing UI shows, the order it shows them in, and the level a group carries until it
/// is changed. The fold reads the same table, so a wrong entry here would also be a wrong entry in the mix.
/// </summary>
[TestFixture]
public class DownmixMixerTests
{
    [Test]
    public void DescribesFivePointOneAsFourGroups()
    {
        IReadOnlyList<DownmixChannelGroup> groups = DownmixMixer.Describe(ChannelLayout.Surround51);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups.Select(group => group.Key), Is.EqualTo(new[] { "front", "center", "lfe", "back" }));
            Assert.That(groups.Select(group => group.Label), Is.EqualTo(new[] { "FL/FR", "FC", "LFE", "BL/BR" }));
            Assert.That(groups.Select(group => group.DefaultLevel), Is.EqualTo(new[] { 100, 71, 0, 71 }));
        }
    }

    [Test]
    public void ListsTheSidePairBeforeTheBackPairInSevenPointOne()
    {
        // The order is the one the mixing UI is meant to read in, not the order the mask happens to put the channels in.
        Assert.That(Labels(ChannelLayout.Surround71), Is.EqualTo(new[] { "FL/FR", "FC", "LFE", "SL/SR", "BL/BR" }));
    }

    [Test]
    public void DescribesTheArrangementsThatAreNotTheCommonOnes()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Labels(ChannelLayout.Surround51Side), Is.EqualTo(new[] { "FL/FR", "FC", "LFE", "SL/SR" }));
            Assert.That(Labels(ChannelLayout.Surround61), Is.EqualTo(new[] { "FL/FR", "FC", "LFE", "BC", "SL/SR" }));
            Assert.That(Labels(ChannelLayout.Surround61Back), Is.EqualTo(new[] { "FL/FR", "FC", "LFE", "BL/BR", "BC" }));
            Assert.That(Labels(ChannelLayout.Surround61Front), Is.EqualTo(new[] { "FL/FR", "LFE", "FLC/FRC", "SL/SR" }));
            Assert.That(Labels(ChannelLayout.Surround70), Is.EqualTo(new[] { "FL/FR", "FC", "BL/BR", "SL/SR" }));
            Assert.That(Labels(ChannelLayout.Surround70Front), Is.EqualTo(new[] { "FL/FR", "FC", "FLC/FRC", "SL/SR" }));
            Assert.That(Labels(ChannelLayout.Surround71Wide), Is.EqualTo(new[] { "FL/FR", "FC", "LFE", "BL/BR", "FLC/FRC" }));
            Assert.That(Labels(ChannelLayout.Surround71WideSide), Is.EqualTo(new[] { "FL/FR", "FC", "LFE", "FLC/FRC", "SL/SR" }));
            Assert.That(Labels(ChannelLayout.Surround512), Is.EqualTo(new[] { "FL/FR", "FC", "LFE", "SL/SR", "TFL/TFR" }));
        }
    }

    [Test]
    public void DescribesMonoAndStereoWithTheirOwnLabel()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Labels(ChannelLayout.Mono), Is.EqualTo(new[] { "M" }));
            Assert.That(Labels(ChannelLayout.Stereo), Is.EqualTo(new[] { "L/R" }));
        }
    }

    [Test]
    public void DescribesAnUnknownArrangementAsThePairItWouldControl()
    {
        // The mixing section is never empty: an arrangement whose speakers are unknown still offers the pair whose
        // level a fold to stereo would apply to.
        IReadOnlyList<DownmixChannelGroup> groups = DownmixMixer.Describe(ChannelLayout.Unknown);

        Assert.That(groups.Select(group => group.Label), Is.EqualTo(new[] { "L/R" }));
    }

    private static IEnumerable<string> Labels(ChannelLayout layout)
    {
        return DownmixMixer.Describe(layout).Select(group => group.Label);
    }
}
