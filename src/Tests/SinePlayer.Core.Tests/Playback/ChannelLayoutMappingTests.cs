// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// How a stream's speaker arrangement is named. The channel mask says which speakers exist; the count only says how many,
/// and seven channels are not one arrangement — a DTS-ES track is 5.1 plus a back centre, and calling that "unknown" left it
/// with no fold to stereo and no way to be played at all.
/// </summary>
[TestFixture]
public class ChannelLayoutMappingTests
{
    /// <summary><c>AV_CHANNEL_ORDER_NATIVE</c>, whose union holds the mask.</summary>
    private const int NativeOrder = 1;

    [Test]
    public void NamesBothSixPointOneArrangements()
    {
        // FFmpeg's "6.1" puts the back centre fourth and the side pair last; "6.1(back)" has a back pair with the
        // centre sixth, which is the arrangement a DTS-ES track declares. They fold differently, so they are two
        // names rather than one.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ToChannelLayout(0x70F, 7), Is.EqualTo(ChannelLayout.Surround61));
            Assert.That(ToChannelLayout(0x13F, 7), Is.EqualTo(ChannelLayout.Surround61Back));
        }
    }

    [Test]
    public void NamesTheArrangementsThatAreNotTheCommonOnes()
    {
        // Seven and eight channels are not one arrangement each: a front-of-centre pair can take the place of a
        // surround pair, and 5.1.2 spends its eighth channel above the front speakers.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ToChannelLayout(0x6CB, 7), Is.EqualTo(ChannelLayout.Surround61Front));
            Assert.That(ToChannelLayout(0x637, 7), Is.EqualTo(ChannelLayout.Surround70));
            Assert.That(ToChannelLayout(0x6C7, 7), Is.EqualTo(ChannelLayout.Surround70Front));
            Assert.That(ToChannelLayout(0xFF, 8), Is.EqualTo(ChannelLayout.Surround71Wide));
            Assert.That(ToChannelLayout(0x6CF, 8), Is.EqualTo(ChannelLayout.Surround71WideSide));
            Assert.That(ToChannelLayout(0x560F, 8), Is.EqualTo(ChannelLayout.Surround512));
        }
    }

    [Test]
    public void NamesTheLayoutsThatHaveAMaskOfTheirOwn()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ToChannelLayout(0x3, 2), Is.EqualTo(ChannelLayout.Stereo));
            Assert.That(ToChannelLayout(0x37, 5), Is.EqualTo(ChannelLayout.Surround50));
            Assert.That(ToChannelLayout(0x3F, 6), Is.EqualTo(ChannelLayout.Surround51));
            Assert.That(ToChannelLayout(0x60F, 6), Is.EqualTo(ChannelLayout.Surround51Side), "5.1 with side speakers is not 5.1 with back speakers");
            Assert.That(ToChannelLayout(0x63F, 8), Is.EqualTo(ChannelLayout.Surround71));
        }
    }

    [Test]
    public void NamesSixPointOneFromTheCountWhenTheOrderCarriesNoMask()
    {
        // A custom channel order lists its speakers one by one, so there is no mask to match: the count is all that is
        // left, and seven channels are taken to be the arrangement seven channels usually are.
        Assert.That(ToChannelLayout(0, 7, order: 2), Is.EqualTo(ChannelLayout.Surround61));
    }

    [Test]
    public void LeavesAMaskItCannotExpressToTheCount()
    {
        // An octagonal arrangement is eight positions this enumeration has no name for, so the mask says nothing
        // usable — and the count is what every stream was named by before the mask was read.
        Assert.That(ToChannelLayout(0x737, 8), Is.EqualTo(ChannelLayout.Surround71));
        Assert.That(ToChannelLayout(0x737, 10), Is.EqualTo(ChannelLayout.Unknown));
    }

    private static ChannelLayout ToChannelLayout(ulong mask, int channelCount, int order = NativeOrder)
    {
        return FFmpegInterop.ToChannelLayout(new AVChannelLayout
        {
            Order = order,
            ChannelCount = channelCount,
            Layout = (nint)mask,
        });
    }
}
