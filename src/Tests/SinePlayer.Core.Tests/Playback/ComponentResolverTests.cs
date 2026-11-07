// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
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
/// A resolver is one kind's: what its chain reads and what the host writes. It does not decide eligibility — a
/// factory says what it can offer, and what it offers is what gets offered to the caller — so what is left to pin is
/// that the order is the host's, that a clone is a copy, and that what is offered is a sequence to be walked.
/// </summary>
[TestFixture]
public class ComponentResolverTests
{
    [Test]
    public void KeepsTheRegistrationOrder()
    {
        ComponentRegistry registry = new();
        registry.Register(new NullVideoRendererFactory("null:first"));
        registry.Register(new NullVideoRendererFactory("null:second"));
        registry.Register(new NullVideoRendererFactory("null:third"));

        VideoComponentResolver resolver = new(registry);

        Assert.That(Signatures(resolver.ResolveRenderers()), Is.EqualTo(new[] { "null:first", "null:second", "null:third" }));
    }

    /// <summary>
    /// A resolver over a live registry follows it, which is what makes a reconcile see an edit rather than the
    /// state the file was opened with.
    /// </summary>
    [Test]
    public void ReadingTheLiveRegistryFollowsIt()
    {
        ComponentRegistry registry = new();
        AudioComponentResolver resolver = new(registry);

        registry.Register(new NullAudioRendererFactory("null:live"));

        Assert.That(Signatures(resolver.ResolveRenderers()), Is.EqualTo(new[] { "null:live" }));
    }

    [Test]
    public void ACloneReadsWhatItCopied()
    {
        ComponentRegistry registry = new();
        NullAudioRendererFactory first = new("null:first");
        registry.Register(first);

        AudioComponentResolver live = new(registry);
        AudioComponentResolver clone = live.Clone();

        registry.Register(new NullAudioRendererFactory("null:second"));
        registry.Remove(first);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Signatures(clone.ResolveRenderers()), Is.EqualTo(new[] { "null:first" }), "a clone is a copy");
            Assert.That(Signatures(live.ResolveRenderers()), Is.EqualTo(new[] { "null:second" }), "the live registry moved on");
        }
    }

    /// <summary>
    /// A clone is a whole resolver, so it can be registered into — onto its own registry, which is what "sharing
    /// nothing" means.
    /// </summary>
    [Test]
    public void ACloneCanBeChangedWithoutReachingTheOriginal()
    {
        VideoComponentResolver original = new();
        original.Register(new NullVideoRendererFactory("null:original"));

        VideoComponentResolver clone = original.Clone();
        clone.ClearRenderers();
        clone.Register(new NullVideoRendererFactory("null:clone"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Signatures(original.ResolveRenderers()), Is.EqualTo(new[] { "null:original" }), "the original kept what it had");
            Assert.That(Signatures(clone.ResolveRenderers()), Is.EqualTo(new[] { "null:clone" }));
        }
    }

    [Test]
    public void ACloneCarriesThePreferences()
    {
        VideoComponentResolver original = new() { Preferences = new VideoPreferences { PreferHardwareDecoding = false } };

        VideoComponentResolver clone = original.Clone();
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
        VideoComponentResolver resolver = new();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolver.ResolveRenderers(), Is.Empty);
            Assert.That(resolver.Preferences, Is.SameAs(VideoPreferences.Default));
        }
    }

    /// <summary>
    /// A factory that offers nothing is a backend saying it cannot serve this machine, so no creator for it is in
    /// the sequence and whoever walks it never learns it was registered.
    /// </summary>
    [Test]
    public void AFactoryThatOffersNothingAddsNoCreator()
    {
        VideoComponentResolver resolver = new();
        resolver.Register(new DecliningVideoRendererFactory("declines"));
        resolver.Register(new NullVideoRendererFactory());

        List<IComponentCreator<IVideoRenderer>> creators = [.. resolver.ResolveRenderers()];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(creators, Has.Count.EqualTo(1), "only the registration that offered something is in the sequence");
            Assert.That(creators[0].Create(out IVideoRenderer? renderer).IsSuccessful, Is.True);
            Assert.That(renderer, Is.TypeOf<NullVideoRenderer>());
            renderer?.Dispose();
        }
    }

    /// <summary>
    /// Hardware decoding is a requirement, so the sequence is the field asked twice: what the registrations would
    /// make with it, and then — because a file that plays in software beats one that does not play — the same
    /// registrations asked without it.
    /// </summary>
    [Test]
    public void AVideoDecoderSequenceAsksForHardwareFirstAndThenWithout()
    {
        VideoComponentResolver resolver = new() { Preferences = new VideoPreferences { PreferHardwareDecoding = true } };
        resolver.Register(new RecordingVideoDecoderFactory());

        using SyntheticMediaSource source = new(new SyntheticMediaScript());
        List<IComponentCreator<IVideoDecoder>> creators = [.. resolver.ResolveDecoders(source, source.Info.VideoStreams[0], gpuDevice: null, bufferedFrames: 4)];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(creators, Has.Count.EqualTo(2), "the same registration asked both ways");
            Assert.That(creators[0].Signature, Does.StartWith("hardware:"), "the requirement is tried first");
            Assert.That(creators[1].Signature, Does.StartWith("software:"), "and the fallback is the same factory without it");
        }
    }

    /// <summary>
    /// A chain resolves only what its own kind can be built from: the subtitle resolver is asked with a subtitle
    /// stream and a cue format, and answers with subtitle renderers.
    /// </summary>
    [Test]
    public void ASubtitleResolverOffersRenderersForTheFormatItIsAskedAbout()
    {
        SubtitleComponentResolver resolver = new();
        resolver.Register(new NullSubtitleRendererFactory("null:subtitles"));

        SubtitleStreamInfo stream = new() { Index = 0, CodecId = "subrip" };
        Assert.That(Signatures(resolver.ResolveRenderers(stream, SubtitleCueFormat.Ass)), Is.EqualTo(new[] { "null:subtitles" }));
    }

    private static IEnumerable<string> Signatures<TComponent>(IEnumerable<IComponentCreator<TComponent>> creators) where TComponent : class
    {
        return creators.Select(creator => creator.Signature);
    }

    /// <summary>A factory that cannot serve whatever it is asked about, and says so by offering nothing.</summary>
    private sealed class DecliningVideoRendererFactory(string name) : IVideoRendererFactory
    {
        public override string ToString()
        {
            return name;
        }

        public OperationResult<IComponentCreator<IVideoRenderer>> Create()
        {
            return new OperationResultCreator<IComponentCreator<IVideoRenderer>>().Failure($"{name} cannot serve this machine.");
        }
    }

    /// <summary>A factory that offers a creator for each way it is asked, so a sequence shows what was asked of it.</summary>
    private sealed class RecordingVideoDecoderFactory : IVideoDecoderFactory
    {
        public OperationResult<IComponentCreator<IVideoDecoder>> Create(IMediaSource source, VideoStreamInfo stream, DecoderOptions options)
        {
            IComponentCreator<IVideoDecoder> creator = new FixedCreator<IVideoDecoder>(options.HardwareDecoding ? "hardware:decoder" : "software:decoder", () => null);
            return new OperationResultCreator<IComponentCreator<IVideoDecoder>>().Success(creator);
        }
    }
}
