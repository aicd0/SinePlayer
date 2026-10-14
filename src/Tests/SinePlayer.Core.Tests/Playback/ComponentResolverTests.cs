// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Internal.Registry;
using SinePlayer.Core.Playback.Internal.Selection;

namespace SinePlayer.Core.Tests.Playback;

[TestFixture]
public class ComponentResolverTests
{
    private const string CODEC_H264 = "h264";
    private const string CODEC_HEVC = "hevc";

    [Test]
    public void DecoderSelectionKeepsOnlyMatchingCodecs()
    {
        ComponentRegistry registry = new();
        registry.Register(new FakeVideoDecoderFactory("h264Decoder", false, 0, CODEC_H264));
        registry.Register(new FakeVideoDecoderFactory("hevcDecoder", false, 0, CODEC_HEVC));

        ComponentResolver resolver = new(registry);

        IReadOnlyList<IVideoDecoderFactory> candidates = resolver.ResolveVideoDecoders(CreateVideoStream(CODEC_H264), ComponentSelectionOptions.Default);

        Assert.That(NamesOf(candidates), Is.EqualTo(new[] { "h264Decoder" }));
    }

    [Test]
    public void DecoderSelectionAcceptsFactoryWithoutCodecRestriction()
    {
        ComponentRegistry registry = new();
        registry.Register(new FakeVideoDecoderFactory("universalDecoder", false, 0));

        ComponentResolver resolver = new(registry);

        IReadOnlyList<IVideoDecoderFactory> candidates = resolver.ResolveVideoDecoders(CreateVideoStream(CODEC_HEVC), ComponentSelectionOptions.Default);

        Assert.That(NamesOf(candidates), Is.EqualTo(new[] { "universalDecoder" }));
    }

    [Test]
    public void DecoderSelectionReturnsEmptyWhenNothingMatches()
    {
        ComponentRegistry registry = new();
        registry.Register(new FakeVideoDecoderFactory("h264Decoder", false, 0, CODEC_H264));

        ComponentResolver resolver = new(registry);

        IReadOnlyList<IVideoDecoderFactory> candidates = resolver.ResolveVideoDecoders(CreateVideoStream(CODEC_HEVC), ComponentSelectionOptions.Default);

        Assert.That(candidates, Is.Empty);
    }

    [Test]
    public void HardwareIsPreferredBeforeSoftware()
    {
        ComponentRegistry registry = new();
        registry.Register(new FakeVideoDecoderFactory("softwareDecoder", false, 100, CODEC_H264));
        registry.Register(new FakeVideoDecoderFactory("hardwareDecoder", true, 0, CODEC_H264));

        ComponentResolver resolver = new(registry);

        IReadOnlyList<IVideoDecoderFactory> candidates = resolver.ResolveVideoDecoders(
            CreateVideoStream(CODEC_H264),
            new ComponentSelectionOptions { PreferHardwareAcceleration = true });

        Assert.That(NamesOf(candidates), Is.EqualTo(new[] { "hardwareDecoder", "softwareDecoder" }));
    }

    [Test]
    public void SoftwareIsPreferredBeforeHardwareWhenRequested()
    {
        ComponentRegistry registry = new();
        registry.Register(new FakeVideoDecoderFactory("hardwareDecoder", true, 100, CODEC_H264));
        registry.Register(new FakeVideoDecoderFactory("softwareDecoder", false, 0, CODEC_H264));

        ComponentResolver resolver = new(registry);

        IReadOnlyList<IVideoDecoderFactory> candidates = resolver.ResolveVideoDecoders(
            CreateVideoStream(CODEC_H264),
            new ComponentSelectionOptions { PreferHardwareAcceleration = false });

        Assert.That(NamesOf(candidates), Is.EqualTo(new[] { "softwareDecoder", "hardwareDecoder" }));
    }

    [Test]
    public void DisallowingFallbackRemovesNonPreferredKind()
    {
        ComponentRegistry registry = new();
        registry.Register(new FakeVideoDecoderFactory("softwareDecoder", false, 100, CODEC_H264));
        registry.Register(new FakeVideoDecoderFactory("hardwareDecoder", true, 0, CODEC_H264));

        ComponentResolver resolver = new(registry);

        IReadOnlyList<IVideoDecoderFactory> candidates = resolver.ResolveVideoDecoders(
            CreateVideoStream(CODEC_H264),
            new ComponentSelectionOptions { PreferHardwareAcceleration = true, AllowFallback = false });

        Assert.That(NamesOf(candidates), Is.EqualTo(new[] { "hardwareDecoder" }));
    }

    [Test]
    public void PriorityBreaksTiesWithinSameKind()
    {
        ComponentRegistry registry = new();
        registry.Register(new FakeVideoDecoderFactory("lowPriority", false, 1, CODEC_H264));
        registry.Register(new FakeVideoDecoderFactory("highPriority", false, 2, CODEC_H264));

        ComponentResolver resolver = new(registry);

        IReadOnlyList<IVideoDecoderFactory> candidates = resolver.ResolveVideoDecoders(CreateVideoStream(CODEC_H264), ComponentSelectionOptions.Default);

        Assert.That(NamesOf(candidates), Is.EqualTo(new[] { "highPriority", "lowPriority" }));
    }

    [Test]
    public void RegistrationOrderBreaksRemainingTies()
    {
        ComponentRegistry registry = new();
        registry.Register(new FakeVideoDecoderFactory("first", false, 5, CODEC_H264));
        registry.Register(new FakeVideoDecoderFactory("second", false, 5, CODEC_H264));

        ComponentResolver resolver = new(registry);

        IReadOnlyList<IVideoDecoderFactory> candidates = resolver.ResolveVideoDecoders(CreateVideoStream(CODEC_H264), ComponentSelectionOptions.Default);

        Assert.That(NamesOf(candidates), Is.EqualTo(new[] { "first", "second" }));
    }

    [Test]
    public void PreferredNameOverridesAutomaticOrdering()
    {
        ComponentRegistry registry = new();
        registry.Register(new FakeVideoDecoderFactory("hardwareDecoder", true, 100, CODEC_H264));
        registry.Register(new FakeVideoDecoderFactory("softwareDecoder", false, 0, CODEC_H264));

        ComponentResolver resolver = new(registry);

        IReadOnlyList<IVideoDecoderFactory> candidates = resolver.ResolveVideoDecoders(
            CreateVideoStream(CODEC_H264),
            new ComponentSelectionOptions { PreferHardwareAcceleration = true, PreferredName = "softwareDecoder" });

        Assert.That(NamesOf(candidates), Is.EqualTo(new[] { "softwareDecoder", "hardwareDecoder" }));
    }

    [Test]
    public void SourceProvidersAreFilteredByScheme()
    {
        ComponentRegistry registry = new();
        registry.Register(new FakeSourceProviderFactory("fileProvider", MediaLocator.SchemeFile));
        registry.Register(new FakeSourceProviderFactory("networkProvider", "http", "https"));

        ComponentResolver resolver = new(registry);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                NamesOf(resolver.ResolveSourceProviders(MediaLocator.Parse(@"C:\movie.mkv"))),
                Is.EqualTo(new[] { "fileProvider" }));
            Assert.That(
                NamesOf(resolver.ResolveSourceProviders(MediaLocator.Parse("https://example.com/movie.mkv"))),
                Is.EqualTo(new[] { "networkProvider" }));
        }
    }

    [Test]
    public void RendersWithoutHardwareDistinctionAreOrderedByPriority()
    {
        ComponentRegistry registry = new();
        registry.Register(new FakeVideoRendererFactory("swapChainRenderer", 1));
        registry.Register(new FakeVideoRendererFactory("softwareRenderer", 2));

        ComponentResolver resolver = new(registry);

        IReadOnlyList<IComponentFactory<IVideoRenderer>> candidates = resolver.ResolveVideoRenderers(ComponentSelectionOptions.Default);

        Assert.That(NamesOf(candidates), Is.EqualTo(new[] { "softwareRenderer", "swapChainRenderer" }));
    }

    [Test]
    public void CapabilitiesListReflectsRegistrationOrder()
    {
        ComponentRegistry registry = new();
        registry.Register(new FakeVideoDecoderFactory("first", false, 0, CODEC_H264));
        registry.Register(new FakeVideoDecoderFactory("second", true, 0, CODEC_HEVC));

        ComponentResolver resolver = new(registry);

        IReadOnlyList<ComponentCapabilities> capabilities = resolver.GetCapabilities(ExtensionPoint.VideoDecoder);

        Assert.That(capabilities.Select(item => item.Name), Is.EqualTo(new[] { "first", "second" }));
    }

    private static VideoStreamInfo CreateVideoStream(string codecId)
    {
        return new VideoStreamInfo
        {
            Index = 0,
            CodecId = codecId,
            Width = 1920,
            Height = 1080,
            FrameRate = 30.0,
            PixelFormat = PixelFormat.Yuv420P,
        };
    }

    private static string[] NamesOf<TFactory>(IReadOnlyList<TFactory> factories) where TFactory : IComponentFactory
    {
        return [.. factories.Select(factory => factory.Capabilities.Name)];
    }

    private sealed class FakeVideoDecoderFactory : IVideoDecoderFactory
    {
        public FakeVideoDecoderFactory(string name, bool isHardwareAccelerated, int priority, params string[] codecIds)
        {
            Capabilities = new ComponentCapabilities
            {
                Name = name,
                Priority = priority,
                IsHardwareAccelerated = isHardwareAccelerated,
                CodecIds = codecIds.Length > 0 ? codecIds : null,
            };
        }

        public ComponentCapabilities Capabilities { get; }

        public PlaybackResult TryCreate(IMediaSource source, VideoStreamInfo stream, DecoderOptions options, out IVideoDecoder? decoder)
        {
            decoder = null;
            return PlaybackResult.Failure("Test factory does not create components.");
        }
    }

    private sealed class FakeSourceProviderFactory : ISourceProviderFactory
    {
        public FakeSourceProviderFactory(string name, params string[] schemes)
        {
            Capabilities = new ComponentCapabilities
            {
                Name = name,
                Schemes = schemes.Length > 0 ? schemes : null,
            };
        }

        public ComponentCapabilities Capabilities { get; }

        public PlaybackResult TryOpen(MediaLocator locator, out IMediaSource? source)
        {
            source = null;
            return PlaybackResult.Failure("Test factory does not create components.");
        }
    }

    private sealed class FakeVideoRendererFactory : IComponentFactory<IVideoRenderer>
    {
        public FakeVideoRendererFactory(string name, int priority)
        {
            Capabilities = new ComponentCapabilities
            {
                Name = name,
                Priority = priority,
            };
        }

        public ComponentCapabilities Capabilities { get; }

        public PlaybackResult TryCreate(out IVideoRenderer? component)
        {
            component = null;
            return PlaybackResult.Failure("Test factory does not create components.");
        }
    }
}
