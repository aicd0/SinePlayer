// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Reflection;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// A chain is the pipeline's own business. What a host sees of one is <see cref="IPlaybackChain"/>: the stream it is
/// for and what it is playing it with. The chain types themselves, and everything they are built from — slots,
/// queues, nodes, tails, versions, the reader's feed — stay inside the assembly, so a type that becomes visible by
/// accident fails here rather than quietly becoming API.
/// </summary>
[TestFixture]
public class PipelineSurfaceTests
{
    private static readonly string sNamespace = typeof(PlaybackPipeline).Namespace!;

    private static readonly Type[] sPublished =
    [
        typeof(AudioFilterChain),
        typeof(AudioFilterChainSnapshot),
        typeof(IAudioChain),
        typeof(IPlaybackChain),
        typeof(IPlaybackChain<>),
        typeof(ISubtitleChain),
        typeof(IVideoChain),
        typeof(MediaEngineState),
        typeof(PlaybackPipeline),
        typeof(ReconcileResult),
    ];

    [Test]
    public void PublishesTheChainInterfaceAndNothingElse()
    {
        string[] visible = [.. typeof(PlaybackPipeline).Assembly
            .GetTypes()
            .Where(type => type is { IsVisible: true } && type.Namespace == sNamespace)
            .Select(type => type.Name)];

        Assert.That(visible.Order(), Is.EqualTo(sPublished.Select(type => type.Name).Order()));
    }

    [Test]
    public void KeepsEveryChainTypeInternal()
    {
        Type[] chains = [.. typeof(PlaybackPipeline).Assembly
            .GetTypes()
            .Where(type => type is { IsInterface: false } && typeof(IPlaybackChain).IsAssignableFrom(type))];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chains, Is.Not.Empty, $"No type implements {nameof(IPlaybackChain)}, so nothing was guarded.");
            foreach (Type chain in chains)
            {
                Assert.That(chain.IsVisible, Is.False, $"{chain.Name} is a chain and is visible outside the assembly.");
            }
        }
    }

    [Test]
    public void EachKindPublishesItsOwnChainInterface()
    {
        Type[] interfaces = [.. typeof(PlaybackPipeline).Assembly
            .GetTypes()
            .Where(type => type is { IsInterface: true } && typeof(IPlaybackChain).IsAssignableFrom(type))];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                interfaces.Select(type => type.Name).Order(),
                Is.EqualTo(new[] { typeof(IAudioChain), typeof(IPlaybackChain), typeof(IPlaybackChain<>), typeof(ISubtitleChain), typeof(IVideoChain) }
                    .Select(type => type.Name)
                    .Order()),
                "a kind of chain is reached through its own interface and through nothing else");

            Assert.That(StreamOf(typeof(IVideoChain)), Is.EqualTo(typeof(VideoStreamInfo)), "a video chain says it plays video streams");
            Assert.That(StreamOf(typeof(IAudioChain)), Is.EqualTo(typeof(AudioStreamInfo)), "an audio chain says it plays audio streams");
            Assert.That(StreamOf(typeof(ISubtitleChain)), Is.EqualTo(typeof(SubtitleStreamInfo)), "a subtitle chain says it plays subtitle streams");
        }
    }

    /// <summary>
    /// The stream type a chain interface declares, which is what spares every caller a conversion: a video chain's
    /// stream is a video stream, always.
    /// </summary>
    private static Type? StreamOf(Type chain)
    {
        Type? typed = Array.Find(chain.GetInterfaces(), type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IPlaybackChain<>));
        return typed?.GetGenericArguments()[0];
    }

    /// <summary>
    /// The pipeline holds chains rather than three fields named after them: a fresh pipeline has one of each kind, and
    /// what the pipeline-wide members describe is the first chain of that kind.
    /// </summary>
    [Test]
    public void AFreshPipelineHoldsOneChainOfEachKind()
    {
        using PlaybackPipeline pipeline = new();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pipeline.VideoChains.Select(chain => chain.Kind), Is.EqualTo(new[] { MediaStreamKind.Video }));
            Assert.That(pipeline.AudioChains.Select(chain => chain.Kind), Is.EqualTo(new[] { MediaStreamKind.Audio }));
            Assert.That(pipeline.SubtitleChains.Select(chain => chain.Kind), Is.EqualTo(new[] { MediaStreamKind.Subtitle }));
        }
    }

    /// <summary>
    /// A chain owns its resolver, so the pipeline's per-kind resolver is that chain's — not a second registration
    /// surface over the same thing.
    /// </summary>
    [Test]
    public void AChainsResolverIsTheOneTheHostRegistersInto()
    {
        using PlaybackPipeline pipeline = new();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pipeline.VideoChains[0].Components, Is.SameAs(pipeline.VideoComponents));
            Assert.That(pipeline.AudioChains[0].Components, Is.SameAs(pipeline.AudioComponents));
            Assert.That(pipeline.SubtitleChains[0].Components, Is.SameAs(pipeline.SubtitleComponents));
        }
    }

    /// <summary>
    /// The pipeline describes playback, not one chain of each kind: what is per kind belongs to the chain, and the only
    /// per-kind names left on the pipeline are the ways to reach a chain — its list, and the resolver it owns.
    /// </summary>
    [Test]
    public void NamesNoKindButToReachItsChains()
    {
        string[] perKind = [.. typeof(PlaybackPipeline)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(member => member.Name)
            .Where(name => name.StartsWith("Video", StringComparison.Ordinal)
                || name.StartsWith("Audio", StringComparison.Ordinal)
                || name.StartsWith("Subtitle", StringComparison.Ordinal))
            .Distinct()];

        Assert.That(
            perKind.Order(),
            Is.EqualTo(new[]
            {
                nameof(PlaybackPipeline.VideoChains),
                nameof(PlaybackPipeline.AudioChains),
                nameof(PlaybackPipeline.SubtitleChains),
                nameof(PlaybackPipeline.VideoComponents),
                nameof(PlaybackPipeline.AudioComponents),
                nameof(PlaybackPipeline.SubtitleComponents),
            }.Order()),
            "a per-kind member on the pipeline is a second place to describe a chain");
    }
}