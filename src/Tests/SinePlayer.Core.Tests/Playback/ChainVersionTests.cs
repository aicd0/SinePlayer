// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Engine;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The rule every node applies to the packages it is handed: a package may be consumed only by the generation it
/// was produced in, or by a component the package has not reached yet, and the source is one of the nodes.
/// <para>
/// Every number here belongs to one chain. That is why there is no test saying a picture comparison ignores the
/// sound: a <see cref="ChainVersion"/> has no sound in it, so the question cannot be asked. The tests that used to
/// ask it, and the ones that covered the pipeline-wide allocator (<c>PipelineVersion.Highest</c>/<c>Next</c>, now a
/// per-chain counter), went with the type that made them necessary.
/// </para>
/// </summary>
[TestFixture]
public class ChainVersionTests
{
    /// <summary>A version in which only the source has a number: the stamp a packet carries when it is accepted.</summary>
    private static ChainVersion Source(long source) => new(source, 0, 0);

    private static ChainVersion Version(long source, long decoder, long renderer) => new(source, decoder, renderer);

    [Test]
    public void MaxRecordsEveryComponentThePackageHasMet()
    {
        // Max(package, self): the source's stamp and the decoder's own generation, and nothing about a component
        // that neither side knows.
        var stamped = ChainVersion.Max(Source(1), Version(1, 2, 2));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stamped.Source, Is.EqualTo(1));
            Assert.That(stamped.Decoder, Is.EqualTo(2));
            Assert.That(stamped.Renderer, Is.EqualTo(2));
        }
    }

    [Test]
    public void TheDecodeNodeComparesItsOwnDecoderOnly()
    {
        using (Assert.EnterMultipleScope())
        {
            // A packet has met the source and nothing else; the decoder's own number is zero and an older number
            // for it is not a reason to give the packet up (the rule drops only for a component *before* the node).
            Assert.That(ChainVersion.CompareDecoder(Source(1), Version(1, 5, 5)), Is.EqualTo(VersionVerdict.Accept));

            // The renderer comes after the decoder, so its number is not the decode node's business.
            Assert.That(ChainVersion.CompareDecoder(Version(1, 5, 9), Version(1, 5, 2)), Is.EqualTo(VersionVerdict.Accept));

            // Its own decoder: a newer one is expected, so the packet waits; the replacement is already published.
            Assert.That(ChainVersion.CompareDecoder(Version(1, 6, 0), Version(1, 5, 5)), Is.EqualTo(VersionVerdict.Block));
            Assert.That(ChainVersion.CompareDecoder(Version(1, 4, 0), Version(1, 5, 5)), Is.EqualTo(VersionVerdict.Accept));

            // The source is before the decoder: a newer one is not compatibility's business (freshness is the
            // epoch's), an older one is a package from a source this decoder has left.
            Assert.That(ChainVersion.CompareDecoder(Source(2), Version(1, 5, 5)), Is.EqualTo(VersionVerdict.Accept));
            Assert.That(ChainVersion.CompareDecoder(Source(1), Version(2, 5, 5)), Is.EqualTo(VersionVerdict.Drop));
        }
    }

    [Test]
    public void AcceptsAPackageFromTheSameGeneration()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ChainVersion.CompareRenderer(Version(1, 2, 2), Version(1, 2, 2)), Is.EqualTo(VersionVerdict.Accept));
            Assert.That(ChainVersion.CompareDecoder(Version(1, 2, 2), Version(1, 2, 2)), Is.EqualTo(VersionVerdict.Accept));
        }
    }

    [Test]
    public void AProducersPackageAlwaysNamesItsOwnChain()
    {
        // The rule has no wildcard: a package from a component whose chain is not in it would read as "older" in
        // the first field this node checks. What makes that safe is the producer stamping its whole snapshot, so a
        // package that reaches a renderer always carries the pair's number, never a zero.
        var frame = ChainVersion.Max(Source(1), Version(1, 5, 5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(frame.Decoder, Is.EqualTo(5));
            Assert.That(frame.Renderer, Is.EqualTo(5));
            Assert.That(ChainVersion.CompareRenderer(frame, Version(1, 5, 5)), Is.EqualTo(VersionVerdict.Accept));
        }
    }

    [Test]
    public void AcceptsAPacketThatHasNotReachedTheDecoder()
    {
        // A packet's own field is zero — the decoder has not produced it — and an own number is only ever a reason
        // to hold a package back, never to give it up.
        Assert.That(ChainVersion.CompareDecoder(Source(1), Version(1, 5, 5)), Is.EqualTo(VersionVerdict.Accept));
    }

    [Test]
    public void AcceptsAPackageFromANewerSource()
    {
        // A newer source says nothing about compatibility: whether a package is current is the epoch's question.
        Assert.That(ChainVersion.CompareDecoder(Source(2), Version(1, 5, 5)), Is.EqualTo(VersionVerdict.Accept));
    }

    [Test]
    public void DropsAPackageFromAnOlderSource()
    {
        // The source is before both decoders, so a packet from a source a decoder has left is stale.
        Assert.That(ChainVersion.CompareDecoder(Source(1), Version(2, 5, 5)), Is.EqualTo(VersionVerdict.Drop));
    }

    [Test]
    public void BlocksAPackageThatExpectsANewerNode()
    {
        // The replacement has been published and this node installs it on its next turn, so waiting is waiting
        // for something that is already on its way.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ChainVersion.CompareRenderer(Version(1, 3, 3), Version(1, 2, 2)), Is.EqualTo(VersionVerdict.Block));
            Assert.That(ChainVersion.CompareDecoder(Version(1, 3, 3), Version(1, 2, 2)), Is.EqualTo(VersionVerdict.Block));
        }
    }

    [Test]
    public void BlocksEvenWhenAnUpstreamComponentIsOlder()
    {
        // Rule 1 comes before rule 2: waiting costs nothing, giving a package up does.
        Assert.That(ChainVersion.CompareRenderer(Version(1, 1, 3), Version(1, 2, 2)), Is.EqualTo(VersionVerdict.Block));
    }

    [Test]
    public void DropsAPackageFromAnOlderUpstreamComponent()
    {
        // The decoder is before the renderer, so a frame from a decoder this renderer has left is stale — the rule
        // 6B case, reported through the upstream number.
        Assert.That(ChainVersion.CompareRenderer(Version(1, 1, 1), Version(1, 2, 2)), Is.EqualTo(VersionVerdict.Drop));
    }

    [Test]
    public void AcceptsAPackageThatIsOlderThanTheNodesOwnComponent()
    {
        // A node's own number is not a reason to drop: the package may simply predate this node, and it is the
        // upstream numbers that say whether the package is stale.
        Assert.That(ChainVersion.CompareRenderer(Version(1, 2, 1), Version(1, 2, 2)), Is.EqualTo(VersionVerdict.Accept));
    }

    [Test]
    public void AcceptsAPackageFromANewerUpstreamComponent()
    {
        // A newer upstream is not this check's business: its pair would carry that number for the node itself,
        // which is where the hold-back comes from.
        Assert.That(ChainVersion.CompareRenderer(Version(1, 3, 1), Version(1, 2, 2)), Is.EqualTo(VersionVerdict.Accept));
    }
}
