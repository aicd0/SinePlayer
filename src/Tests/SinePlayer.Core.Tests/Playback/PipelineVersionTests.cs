// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The rule every node applies to the packages it is handed: a package may be consumed only by the generation it
/// was produced in, or by a component the package has not reached yet, and the source is one of the nodes.
/// </summary>
[TestFixture]
public class PipelineVersionTests
{
    private static PipelineVersion Source(long source) => default(PipelineVersion).WithSource(source);

    private static PipelineVersion Video(long source, long decoder, long renderer) => new(source, 0, 0, decoder, renderer);

    private static PipelineVersion Audio(long source, long decoder, long renderer) => new(source, decoder, renderer, 0, 0);

    [Test]
    public void PublishesAPairUnderOneNumberAboveTheHighestInUse()
    {
        var published = new PipelineVersion(1, 3, 3, 2, 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(published.Highest, Is.EqualTo(3));
            Assert.That(published.Next, Is.EqualTo(4));
            Assert.That(published.WithVideo(published.Next).VideoDecoder, Is.EqualTo(4));
            Assert.That(published.WithVideo(published.Next).VideoRenderer, Is.EqualTo(4), "a decoder and the renderer that draws its frames are one generation");
            Assert.That(published.WithAudio(published.Next).AudioDecoder, Is.EqualTo(4));
            Assert.That(published.WithAudio(published.Next).AudioRenderer, Is.EqualTo(4));
        }
    }

    [Test]
    public void TheHighestInUseIsTheHighestOfEveryComponent()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(default(PipelineVersion).Highest, Is.Zero);
            Assert.That(default(PipelineVersion).Next, Is.EqualTo(1), "the first publication takes the first number");
            Assert.That(new PipelineVersion(1, 7, 2, 5, 4).Highest, Is.EqualTo(7));
        }
    }

    [Test]
    public void MaxRecordsEveryComponentThePackageHasMet()
    {
        // Max(package, self): the source's stamp and the decoder's own generation, and nothing about a component
        // that neither side knows.
        var stamped = PipelineVersion.Max(Source(1), Video(1, 2, 2));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stamped.Source, Is.EqualTo(1));
            Assert.That(stamped.VideoDecoder, Is.EqualTo(2));
            Assert.That(stamped.VideoRenderer, Is.EqualTo(2));
            Assert.That(stamped.AudioDecoder, Is.Zero, "a picture package says nothing about the sound");
            Assert.That(stamped.AudioRenderer, Is.Zero);
        }
    }

    [Test]
    public void TheDecodeNodeComparesItsOwnDecoderOnly()
    {
        using (Assert.EnterMultipleScope())
        {
            // A packet has met the source and nothing else; the decoder's own number is zero and an older number
            // for it is not a reason to give the packet up (the rule drops only for a component *before* the node).
            Assert.That(PipelineVersion.CompareVideoDecoder(Source(1), Video(1, 5, 5)), Is.EqualTo(VersionVerdict.Accept));
            Assert.That(PipelineVersion.CompareAudioDecoder(Source(1), Audio(1, 5, 5)), Is.EqualTo(VersionVerdict.Accept));

            // The renderer comes after the decoder, so its number is not the decode node's business.
            Assert.That(PipelineVersion.CompareVideoDecoder(Video(1, 5, 9), Video(1, 5, 2)), Is.EqualTo(VersionVerdict.Accept));

            // Its own decoder: a newer one is expected, so the packet waits; the replacement is already published.
            Assert.That(PipelineVersion.CompareVideoDecoder(Video(1, 6, 0), Video(1, 5, 5)), Is.EqualTo(VersionVerdict.Block));
            Assert.That(PipelineVersion.CompareVideoDecoder(Video(1, 4, 0), Video(1, 5, 5)), Is.EqualTo(VersionVerdict.Accept));

            // The source is before the decoder: a newer one is not compatibility's business (freshness is the
            // epoch's), an older one is a package from a source this decoder has left.
            Assert.That(PipelineVersion.CompareVideoDecoder(Source(2), Video(1, 5, 5)), Is.EqualTo(VersionVerdict.Accept));
            Assert.That(PipelineVersion.CompareVideoDecoder(Source(1), Video(2, 5, 5)), Is.EqualTo(VersionVerdict.Drop));
        }
    }

    [Test]
    public void AcceptsAPackageFromTheSameGeneration()
    {
        Assert.That(PipelineVersion.CompareVideoRenderer(Video(1, 2, 2), Video(1, 2, 2)), Is.EqualTo(VersionVerdict.Accept));
        Assert.That(PipelineVersion.CompareAudioRenderer(Audio(1, 2, 2), Audio(1, 2, 2)), Is.EqualTo(VersionVerdict.Accept));
    }

    [Test]
    public void AProducersPackageAlwaysNamesItsOwnChain()
    {
        // The rule has no wildcard: a package from a component whose chain is not in it would read as "older" in
        // the first field this node checks. What makes that safe is the producer stamping its whole snapshot, so a
        // package that reaches a renderer always carries the pair's number, never a zero.
        var frame = PipelineVersion.Max(Source(1), Video(1, 5, 5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(frame.VideoDecoder, Is.EqualTo(5));
            Assert.That(frame.VideoRenderer, Is.EqualTo(5));
            Assert.That(PipelineVersion.CompareVideoRenderer(frame, Video(1, 5, 5)), Is.EqualTo(VersionVerdict.Accept));
        }
    }

    [Test]
    public void AcceptsAPacketThatHasNotReachedTheDecoder()
    {
        // A packet's own field is zero — the decoder has not produced it — and an own number is only ever a reason
        // to hold a package back, never to give it up.
        Assert.That(PipelineVersion.CompareVideoDecoder(Source(1), Video(1, 5, 5)), Is.EqualTo(VersionVerdict.Accept));
        Assert.That(PipelineVersion.CompareAudioDecoder(Source(1), Audio(1, 5, 5)), Is.EqualTo(VersionVerdict.Accept));
    }

    [Test]
    public void AcceptsAPackageFromANewerSource()
    {
        // A newer source says nothing about compatibility: whether a package is current is the epoch's question.
        Assert.That(PipelineVersion.CompareVideoDecoder(Source(2), Video(1, 5, 5)), Is.EqualTo(VersionVerdict.Accept));
        Assert.That(PipelineVersion.CompareAudioDecoder(Source(2), Audio(1, 5, 5)), Is.EqualTo(VersionVerdict.Accept));
    }

    [Test]
    public void DropsAPackageFromAnOlderSource()
    {
        // The source is before both decoders, so a packet from a source a decoder has left is stale.
        Assert.That(PipelineVersion.CompareVideoDecoder(Source(1), Video(2, 5, 5)), Is.EqualTo(VersionVerdict.Drop));
        Assert.That(PipelineVersion.CompareAudioDecoder(Source(1), Audio(2, 5, 5)), Is.EqualTo(VersionVerdict.Drop));
    }

    [Test]
    public void BlocksAPackageThatExpectsANewerNode()
    {
        // The replacement has been published and this node installs it on its next turn, so waiting is waiting
        // for something that is already on its way.
        Assert.That(PipelineVersion.CompareVideoRenderer(Video(1, 3, 3), Video(1, 2, 2)), Is.EqualTo(VersionVerdict.Block));
        Assert.That(PipelineVersion.CompareAudioRenderer(Audio(1, 3, 3), Audio(1, 2, 2)), Is.EqualTo(VersionVerdict.Block));
    }

    [Test]
    public void BlocksEvenWhenAnUpstreamComponentIsOlder()
    {
        // Rule 1 comes before rule 2: waiting costs nothing, giving a package up does.
        Assert.That(PipelineVersion.CompareVideoRenderer(Video(1, 1, 3), Video(1, 2, 2)), Is.EqualTo(VersionVerdict.Block));
    }

    [Test]
    public void DropsAPackageFromAnOlderUpstreamComponent()
    {
        // The decoder is before the renderer, so a frame from a decoder this renderer has left is stale — the rule
        // 6B case, reported through the upstream number.
        Assert.That(PipelineVersion.CompareVideoRenderer(Video(1, 1, 1), Video(1, 2, 2)), Is.EqualTo(VersionVerdict.Drop));
        Assert.That(PipelineVersion.CompareAudioRenderer(Audio(1, 1, 1), Audio(1, 2, 2)), Is.EqualTo(VersionVerdict.Drop));
    }

    [Test]
    public void AcceptsAPackageThatIsOlderThanTheNodesOwnComponent()
    {
        // A node's own number is not a reason to drop: the package may simply predate this node, and it is the
        // upstream numbers that say whether the package is stale.
        Assert.That(PipelineVersion.CompareVideoRenderer(Video(1, 2, 1), Video(1, 2, 2)), Is.EqualTo(VersionVerdict.Accept));
        Assert.That(PipelineVersion.CompareAudioRenderer(Audio(1, 2, 1), Audio(1, 2, 2)), Is.EqualTo(VersionVerdict.Accept));
    }

    [Test]
    public void AcceptsAPackageFromANewerUpstreamComponent()
    {
        // A newer upstream is not this check's business: its pair would carry that number for the node itself,
        // which is where the hold-back comes from.
        Assert.That(PipelineVersion.CompareVideoRenderer(Video(1, 3, 1), Video(1, 2, 2)), Is.EqualTo(VersionVerdict.Accept));
    }

    [Test]
    public void IgnoresTheSoundWhenComparingThePicture()
    {
        Assert.That(PipelineVersion.CompareVideoRenderer(new PipelineVersion(1, 9, 9, 2, 2), Video(1, 2, 2)), Is.EqualTo(VersionVerdict.Accept));
        Assert.That(PipelineVersion.CompareVideoDecoder(new PipelineVersion(1, 9, 9, 2, 2), Video(1, 2, 2)), Is.EqualTo(VersionVerdict.Accept));
    }

    [Test]
    public void IgnoresThePictureWhenComparingTheSound()
    {
        Assert.That(PipelineVersion.CompareAudioRenderer(new PipelineVersion(1, 2, 2, 9, 9), Audio(1, 2, 2)), Is.EqualTo(VersionVerdict.Accept));
        Assert.That(PipelineVersion.CompareAudioDecoder(new PipelineVersion(1, 2, 2, 9, 9), Audio(1, 2, 2)), Is.EqualTo(VersionVerdict.Accept));
    }
}