// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Reflection;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
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
        typeof(IPictureFrameSource),
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
    /// A surface that draws nothing. These tests are about which chains exist, so the surface only has to exist: what
    /// it is asked to show is never looked at.
    /// </summary>
    private static OperationResult Await(Task<OperationResult> command)
    {
        return command.GetAwaiter().GetResult();
    }

    private static T Await<T>(Task<T> command)
    {
        return command.GetAwaiter().GetResult();
    }

    private sealed class NoTarget : IVideoRenderTarget
    {
        public int SurfaceWidth => 320;

        public int SurfaceHeight => 240;

        public double RasterizationScale => 1.0;

        public event Action? SurfaceSizeChanged
        {
            add { }
            remove { }
        }

        public void AttachPresentationSurface(nint swapChain) { }
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
    /// A chain arrives with its surface, so the constructor cannot make one: a fresh pipeline has no chain of any kind
    /// and holds no surface, and the host adds what it wants to play. The kinds it does add are the kinds it gets.
    /// </summary>
    [Test]
    public void AFreshPipelineHoldsNoChain()
    {
        using PlaybackPipeline pipeline = new();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pipeline.VideoChains, Is.Empty, "nothing is created behind the host's back");
            Assert.That(pipeline.AudioChains, Is.Empty);
            Assert.That(pipeline.SubtitleChains, Is.Empty);
        }
    }

    /// <summary>What a host is given back is the chain that was added, and it is reachable from the list.</summary>
    [Test]
    public void AddingAChainHandsBackTheChainThatWasAdded()
    {
        using PlaybackPipeline pipeline = new();

        IVideoChain video = Await(pipeline.AddVideoChain(new NoTarget()));
        IAudioChain audio = Await(pipeline.AddAudioChain());
        ISubtitleChain subtitle = Await(pipeline.AddSubtitleChain(new NoTarget()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pipeline.VideoChains, Is.EqualTo(new[] { video }));
            Assert.That(pipeline.AudioChains, Is.EqualTo(new[] { audio }));
            Assert.That(pipeline.SubtitleChains, Is.EqualTo(new[] { subtitle }));
            Assert.That(video.Kind, Is.EqualTo(MediaStreamKind.Video));
            Assert.That(audio.Kind, Is.EqualTo(MediaStreamKind.Audio));
            Assert.That(subtitle.Kind, Is.EqualTo(MediaStreamKind.Subtitle));
        }
    }

    /// <summary>
    /// A chain that came out is not in the lists any more, and one that never went in is refused rather than quietly
    /// ignored.
    /// </summary>
    [Test]
    public void RemovedChainsLeaveTheListsAndForeignOnesAreRefused()
    {
        using PlaybackPipeline pipeline = new();
        using PlaybackPipeline other = new();

        IVideoChain video = Await(pipeline.AddVideoChain(new NoTarget()));
        Assert.That(Await(pipeline.RemoveChain(video)).IsSuccessful, Is.True);
        Assert.That(pipeline.VideoChains, Is.Empty);

        IVideoChain foreign = Await(other.AddVideoChain(new NoTarget()));
        Assert.That(Await(pipeline.RemoveChain(foreign)).IsSuccessful, Is.False, "another pipeline's chain is not this one's to remove");
        Assert.That(other.VideoChains, Is.EqualTo(new[] { foreign }), "and it stays where it is");
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
            }.Order()),
            "a per-kind member on the pipeline is a second place to describe a chain");
    }

    /// <summary>
    /// What reconciling made of a chain is asked of that chain, so a result is three dictionaries and nothing else. A
    /// singular view would be a second place that assumes one chain of a kind — the assumption v2 exists to remove —
    /// and it cannot be removed later without breaking whoever came to depend on it.
    /// </summary>
    [Test]
    public void AReconcileResultAnswersOnlyPerChain()
    {
        string[] members = [.. typeof(ReconcileResult)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(member => member is not ConstructorInfo)
            .Select(member => member.Name)
            .Where(name => !name.StartsWith("get_", StringComparison.Ordinal) && !name.StartsWith("set_", StringComparison.Ordinal))
            .Distinct()];

        Assert.That(
            members.Order(),
            Is.EqualTo(new[]
            {
                nameof(ReconcileResult.VideoResults),
                nameof(ReconcileResult.AudioResults),
                nameof(ReconcileResult.SubtitleResults),
            }.Order()),
            "a result is what happened to each chain, and anything else is a way to ask about a kind");
    }
}