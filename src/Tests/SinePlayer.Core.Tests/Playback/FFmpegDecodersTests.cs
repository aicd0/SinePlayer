// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Which decoder a stream is opened with. FFmpeg names a codec and a decoder separately, and for DTS the two words differ —
/// the codec is <c>dts</c>, the decoder is <c>dca</c> — so a lookup that only ever asks by the container's name finds nothing
/// at all. That is what left a DTS-HD MA track unplayable while the decoder for it sat in the same DLL.
/// </summary>
[TestFixture]
public class FFmpegDecodersTests
{
    /// <summary><c>AV_CODEC_ID_DTS</c>, which is what a <c>dts</c> stream declares.</summary>
    private const int DtsCodecId = 86020;

    [Test]
    public void FindsTheDtsDecoderByItsCodecEvenThoughItsNameDiffers()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(FFmpegNative.avcodec_find_decoder_by_name("dts"), Is.EqualTo(nint.Zero), "FFmpeg has no decoder called 'dts'");

            List<FFmpegDecoderCandidate> candidates = [.. FFmpegDecoders.ByCodecIdThenNames(DtsCodecId, "dts", "dca")];

            Assert.That(candidates, Has.Count.EqualTo(1), "the id's own decoder — 'dca' is the same one under the name that works");
            Assert.That(candidates[0].Codec, Is.Not.EqualTo(nint.Zero), "the codec handle is what opens a context");
            Assert.That(candidates[0].Name, Is.EqualTo("dts"), "and the name stays the stream's, for a report to read");
        }
    }

    [Test]
    public void KeepsTheCallersNameForTheDecoderItResolved()
    {
        // The two orders differ in which name a decoder is labelled by, which is all a report can show when both point at
        // the same handle: the video path ranks its own candidates, so what it asked for is what it is called.
        List<FFmpegDecoderCandidate> idFirst = [.. FFmpegDecoders.ByCodecIdThenNames(DtsCodecId, "dca")];
        List<FFmpegDecoderCandidate> nameFirst = [.. FFmpegDecoders.ByNamesThenCodecId(DtsCodecId, "dca")];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(idFirst, Has.Count.EqualTo(1), "'dca' is the decoder the id already found");
            Assert.That(idFirst[0].Name, Is.EqualTo("dts"), "so the stream's own name is what it is called");

            Assert.That(nameFirst, Has.Count.EqualTo(1), "and asking for it by name does not add a second copy");
            Assert.That(nameFirst[0].Name, Is.EqualTo("dca"), "the name the caller asked for is the one that sticks");
        }
    }

    [Test]
    public void OffersEachDecoderOnce()
    {
        // A name that resolves to the codec's own decoder is the same handle: opening it twice is a fallback that cannot
        // change the answer.
        List<FFmpegDecoderCandidate> candidates = [.. FFmpegDecoders.ByCodecIdThenNames(DtsCodecId, "dca", "dts", "dca")];

        Assert.That(candidates.Select(candidate => candidate.Codec), Is.Unique, "one handle per decoder, not one per name");
    }

    [Test]
    public void OffersNothingForACodecNoDecoderHandles()
    {
        List<FFmpegDecoderCandidate> candidates = [.. FFmpegDecoders.ByCodecIdThenNames(999999, "no-such-decoder")];

        Assert.That(candidates, Is.Empty, "a codec this build cannot decode has no candidate to try");
    }
}
