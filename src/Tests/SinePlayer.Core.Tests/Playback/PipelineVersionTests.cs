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
    public void TheDecodeNodeComparesTheSourceAndItsOwnDecoderOnly()
    {
        using (Assert.EnterMultipleScope())
        {
            // A packet has met the source and nothing else.
            Assert.That(PipelineVersion.CompareVideoDecoder(Source(1), Video(1, 5, 5)), Is.EqualTo(VersionVerdict.Accept));
            Assert.That(PipelineVersion.CompareAudioDecoder(Source(1), Audio(1, 5, 5)), Is.EqualTo(VersionVerdict.Accept));

            // The renderer comes after the decoder, so its number is not the decode node's business.
            Assert.That(PipelineVersion.CompareVideoDecoder(Video(1, 5, 9), Video(1, 5, 2)), Is.EqualTo(VersionVerdict.Accept));

            // Its own decoder: newer blocks, older is not a reason to drop (rule 6B).
            Assert.That(PipelineVersion.CompareVideoDecoder(Video(1, 6, 0), Video(1, 5, 5)), Is.EqualTo(VersionVerdict.Block));
            Assert.That(PipelineVersion.CompareVideoDecoder(Video(1, 4, 0), Video(1, 5, 5)), Is.EqualTo(VersionVerdict.Accept));

            // The source, in both directions.
            Assert.That(PipelineVersion.CompareVideoDecoder(Source(2), Video(1, 5, 5)), Is.EqualTo(VersionVerdict.Block));
            Assert.That(PipelineVersion.CompareVideoDecoder(Source(1), Video(2, 5, 5)), Is.EqualTo(VersionVerdict.Drop));
        }
    }

    [Test]
    public void AcceptsAPackageFromTheSameGeneration()
    {
        Assert.That(PipelineVersion.CompareVideoRenderer(Video(1, 2, 2), Video(1, 2, 2)), Is.EqualTo(VersionVerdict.Accept));
    }

    [Test]
    public void AcceptsAComponentThePackageHasNotReached()
    {
        // A packet the source has just produced has met the source and nothing else.
        Assert.That(PipelineVersion.CompareVideoRenderer(Source(1), Video(1, 5, 5)), Is.EqualTo(VersionVerdict.Accept));
        Assert.That(PipelineVersion.CompareVideoRenderer(Video(1, 2, 0), Video(1, 2, 2)), Is.EqualTo(VersionVerdict.Accept), "a frame leaving the decoder has not passed the renderer yet");
    }

    [Test]
    public void BlocksAPackageFromANewerSource()
    {
        // The source is a node of its own: a decoder built for source 1 must not be handed source 2's packets.
        Assert.That(PipelineVersion.CompareVideoRenderer(Source(2), Video(1, 2, 2)), Is.EqualTo(VersionVerdict.Block));
        Assert.That(PipelineVersion.CompareAudioRenderer(Source(2), Audio(1, 2, 2)), Is.EqualTo(VersionVerdict.Block));
    }

    [Test]
    public void DropsAPackageFromAnOlderSource()
    {
        Assert.That(PipelineVersion.CompareVideoRenderer(Source(1), Video(2, 2, 2)), Is.EqualTo(VersionVerdict.Drop));
        Assert.That(PipelineVersion.CompareAudioRenderer(Source(1), Audio(2, 2, 2)), Is.EqualTo(VersionVerdict.Drop));
    }

    [Test]
    public void BlocksAPackageFromANewerComponent()
    {
        // The replacement has been published and this node installs it on its next turn, so waiting is waiting
        // for something that is already on its way.
        Assert.That(PipelineVersion.CompareVideoRenderer(Video(1, 3, 3), Video(1, 2, 2)), Is.EqualTo(VersionVerdict.Block));
        Assert.That(PipelineVersion.CompareVideoRenderer(Video(1, 3, 3), Video(1, 2, 3)), Is.EqualTo(VersionVerdict.Block), "only the component that is behind has to change");
    }

    [Test]
    public void DropsAPackageWhoseUpstreamComponentHasBeenUpgraded()
    {
        // The decoder is before the renderer, so a frame from a decoder this renderer has left is stale.
        Assert.That(PipelineVersion.CompareVideoRenderer(Video(1, 1, 1), Video(1, 2, 2)), Is.EqualTo(VersionVerdict.Drop));
        Assert.That(PipelineVersion.CompareAudioRenderer(Audio(1, 1, 1), Audio(1, 2, 2)), Is.EqualTo(VersionVerdict.Drop));
    }

    [Test]
    public void AcceptsAPackageThatIsOlderThanTheNodesOwnComponent()
    {
        // Rule 6B drops only for a version *before* the node: a package has never seen the node's own component,
        // so its number for it says nothing about whether the package is stale.
        Assert.That(PipelineVersion.CompareVideoRenderer(Video(1, 2, 1), Video(1, 2, 2)), Is.EqualTo(VersionVerdict.Accept));
        Assert.That(PipelineVersion.CompareAudioRenderer(Audio(1, 2, 1), Audio(1, 2, 2)), Is.EqualTo(VersionVerdict.Accept));
    }

    [Test]
    public void BlocksWhenAComponentIsNewerWhateverTheOthersSay()
    {
        // Waiting is not a loss and the replacement is already published, so a newer number wins over an older
        // one rather than the package being given up.
        Assert.That(PipelineVersion.CompareVideoRenderer(Video(1, 3, 1), Video(1, 2, 2)), Is.EqualTo(VersionVerdict.Block));
    }

    [Test]
    public void IgnoresTheSoundWhenComparingThePicture()
    {
        Assert.That(PipelineVersion.CompareVideoRenderer(new PipelineVersion(1, 9, 9, 2, 2), Video(1, 2, 2)), Is.EqualTo(VersionVerdict.Accept));
    }

    [Test]
    public void IgnoresThePictureWhenComparingTheSound()
    {
        Assert.That(PipelineVersion.CompareAudioRenderer(new PipelineVersion(1, 2, 2, 9, 9), Audio(1, 2, 2)), Is.EqualTo(VersionVerdict.Accept));
    }
}