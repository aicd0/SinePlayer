// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

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

    private static readonly string[] sPublished =
    [
        nameof(AudioFilterChain),
        nameof(AudioFilterChainSnapshot),
        nameof(MediaEngineState),
        nameof(IPlaybackChain),
        nameof(PlaybackPipeline),
        nameof(ReconcileResult),
    ];

    [Test]
    public void PublishesTheChainInterfaceAndNothingElse()
    {
        string[] visible = [.. typeof(PlaybackPipeline).Assembly
            .GetTypes()
            .Where(type => type is { IsVisible: true } && type.Namespace == sNamespace)
            .Select(type => type.Name)];

        Assert.That(visible.Order(), Is.EqualTo(sPublished.Order()));
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
}