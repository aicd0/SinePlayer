// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.Null;
using SinePlayer.Core.Playback.Registry;
using SinePlayer.Core.Playback.Selection;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The resolver is what the pipeline reads and what the host writes. It does not decide eligibility — a factory
/// says what it can offer, and what it offers is what gets offered to the caller — so what is left to pin is that
/// the order is the host's, that a clone is a copy, and that what is offered is a sequence to be walked.
/// </summary>
[TestFixture]
public class ComponentResolverTests
{
    [Test]
    public void KeepsTheRegistrationOrder()
    {
        ComponentRegistry registry = new();
        NullVideoRendererFactory first = new();
        NullVideoRendererFactory second = new("null:second");
        NullVideoRendererFactory third = new("null:third");
        registry.Register(first);
        registry.Register(second);
        registry.Register(third);

        ComponentResolver resolver = new(registry);

        Assert.That(Renderers(resolver), Is.EqualTo(new[] { first, second, third }));
    }

    /// <summary>
    /// A resolver over a live registry follows it, which is what makes a reconcile see an edit rather than the
    /// state the file was opened with.
    /// </summary>
    [Test]
    public void ReadingTheLiveRegistryFollowsIt()
    {
        ComponentRegistry registry = new();
        ComponentResolver resolver = new(registry);

        NullAudioRendererFactory device = new();
        registry.Register(device);

        Assert.That(resolver.GetComponents<IAudioRendererFactory>(), Is.EqualTo(new[] { device }));
    }

    [Test]
    public void ACloneReadsWhatItCopied()
    {
        ComponentRegistry registry = new();
        NullAudioRendererFactory first = new();
        registry.Register(first);

        ComponentResolver live = new(registry);
        ComponentResolver clone = live.Clone();

        registry.Register(new NullAudioRendererFactory("null:second"));
        registry.Remove(first);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clone.GetComponents<IAudioRendererFactory>(), Is.EqualTo(new[] { first }), "a clone is a copy");
            Assert.That(live.GetComponents<IAudioRendererFactory>().Count, Is.EqualTo(1), "the live registry moved on");
            Assert.That(live.GetComponents<IAudioRendererFactory>()[0], Is.Not.SameAs(first));
        }
    }

    /// <summary>
    /// A clone is a whole resolver, so it can be registered into — onto its own registry, which is what "sharing
    /// nothing" means.
    /// </summary>
    [Test]
    public void ACloneCanBeChangedWithoutReachingTheOriginal()
    {
        ComponentResolver original = new();
        NullVideoRendererFactory renderer = new();
        original.Register(renderer);

        ComponentResolver clone = original.Clone();
        clone.Clear<IVideoRendererFactory>();
        clone.Register(new NullVideoRendererFactory("null:clone"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Renderers(original), Is.EqualTo(new[] { renderer }), "the original kept what it had");
            Assert.That(Renderers(clone).Count, Is.EqualTo(1));
            Assert.That(Renderers(clone)[0], Is.Not.SameAs(renderer));
        }
    }

    [Test]
    public void ACloneCarriesThePreferences()
    {
        ComponentResolver original = new() { Preferences = new PlaybackPreferences { PreferHardwareDecoding = false } };

        ComponentResolver clone = original.Clone();
        clone.Preferences = clone.Preferences with { PreferHardwareDecoding = true };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clone.Preferences.PreferHardwareDecoding, Is.True);
            Assert.That(original.Preferences.PreferHardwareDecoding, Is.False, "the setting belongs to each resolver");
        }
    }

    [Test]
    public void AResolverThatOwnsItsRegistryStartsEmpty()
    {
        ComponentResolver resolver = new();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Renderers(resolver), Is.Empty);
            Assert.That(resolver.Preferences, Is.SameAs(PlaybackPreferences.Default));
        }
    }

    /// <summary>
    /// A factory that offers nothing is a backend saying it cannot serve this machine, so no creator for it is in
    /// the sequence and whoever walks it never learns it was registered.
    /// </summary>
    [Test]
    public void AFactoryThatOffersNothingAddsNoCreator()
    {
        ComponentResolver resolver = new();
        resolver.Register(new DecliningVideoRendererFactory("declines"));
        resolver.Register(new NullVideoRendererFactory());

        List<IComponentCreator<IVideoRenderer>> creators = [.. resolver.ResolveVideoRenderers()];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(creators, Has.Count.EqualTo(1), "only the registration that offered something is in the sequence");
            Assert.That(creators[0].Create(out IVideoRenderer? renderer).IsSuccessful, Is.True);
            Assert.That(renderer, Is.TypeOf<NullVideoRenderer>());
            renderer?.Dispose();
        }
    }

    /// <summary>
    /// Everything that offers something is offered, in the order the host registered it: which one is worth taking
    /// is the caller's decision, not the resolver's.
    /// </summary>
    [Test]
    public void EveryOfferIsInTheSequenceInRegistrationOrder()
    {
        ComponentResolver resolver = new();
        resolver.Register(new NullVideoRendererFactory());
        resolver.Register(new NullVideoRendererFactory("null:second"));

        Assert.That(resolver.ResolveVideoRenderers().Select(creator => creator.Signature), Is.EqualTo(new[] { "null:video-renderer", "null:second" }));
    }

    /// <summary>
    /// Hardware decoding is a requirement, so the sequence is the field asked twice: what the registrations would
    /// make with it, and then — because a file that plays in software beats one that does not play — the same
    /// registrations asked without it.
    /// </summary>
    [Test]
    public void AVideoDecoderSequenceAsksForHardwareFirstAndThenWithout()
    {
        ComponentResolver resolver = new() { Preferences = new PlaybackPreferences { PreferHardwareDecoding = true } };
        resolver.Register(new RecordingVideoDecoderFactory());

        using SyntheticMediaSource source = new(new SyntheticMediaScript());
        List<IComponentCreator<IVideoDecoder>> creators = [.. resolver.ResolveVideoDecoders(source, source.Info.VideoStreams[0], gpuDevice: null, bufferedFrames: 4)];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(creators, Has.Count.EqualTo(2), "the same registration asked both ways");
            Assert.That(creators[0].Signature, Does.StartWith("hardware:"), "the requirement is tried first");
            Assert.That(creators[1].Signature, Does.StartWith("software:"), "and the fallback is the same factory without it");
        }
    }

    private static IReadOnlyList<IVideoRendererFactory> Renderers(ComponentResolver resolver)
    {
        return resolver.GetComponents<IVideoRendererFactory>();
    }

    /// <summary>A factory that cannot serve whatever it is asked about, and says so by offering nothing.</summary>
    private sealed class DecliningVideoRendererFactory(string name) : IVideoRendererFactory
    {
        public override string ToString()
        {
            return name;
        }

        public PlaybackOperationResult TryCreate(out IComponentCreator<IVideoRenderer>? creator)
        {
            creator = null;
            return PlaybackOperationResult.Failure($"{name} cannot serve this machine.");
        }
    }

    /// <summary>A factory that offers a creator for each way it is asked, so a sequence shows what was asked of it.</summary>
    private sealed class RecordingVideoDecoderFactory : IVideoDecoderFactory
    {
        public PlaybackOperationResult TryCreate(IMediaSource source, VideoStreamInfo stream, DecoderOptions options, out IComponentCreator<IVideoDecoder>? creator)
        {
            creator = new FixedCreator<IVideoDecoder>(options.HardwareDecoding ? "hardware:decoder" : "software:decoder", () => null);
            return PlaybackOperationResult.Success;
        }
    }
}
