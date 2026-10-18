// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayer.Core.Playback.Abstractions.Filter;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Selection;

/// <summary>
/// Turns registered components plus facts about the media into ordered candidate lists. The pipeline
/// always walks the list and stops at the first candidate it can actually create, which is what makes
/// fallback — hardware to software decoding, for example — a property of selection rather than a
/// special case inside the pipeline.
/// <para>
/// Every method returns an empty list when nothing matches; deciding that this is an error belongs to
/// the caller, which knows whether the stream could have been played at all.
/// </para>
/// </summary>
public sealed class ComponentResolver
{
    private readonly IComponentRegistry _registry;

    public ComponentResolver(IComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <summary>
    /// Capabilities of every component registered for a role, in registration order. This is what a
    /// settings surface lists when the user chooses a decoder or a renderer.
    /// </summary>
    public IReadOnlyList<ComponentCapabilities> GetCapabilities(ExtensionPoint extensionPoint)
    {
        List<ComponentCapabilities> result = [];
        foreach (IComponentFactory factory in _registry.GetFactories(extensionPoint))
        {
            result.Add(factory.Capabilities);
        }

        return result;
    }

    public IReadOnlyList<ISourceProviderFactory> ResolveSourceProviders(MediaLocator locator)
    {
        List<ISourceProviderFactory> candidates = [];
        foreach (ISourceProviderFactory factory in GetTypedFactories<ISourceProviderFactory>(ExtensionPoint.SourceProvider))
        {
            if (!SupportsScheme(factory.Capabilities, locator.Scheme))
            {
                continue;
            }

            candidates.Add(factory);
        }

        return OrderByPriority(candidates, ComponentSelectionOptions.Default);
    }

    public IReadOnlyList<IVideoDecoderFactory> ResolveVideoDecoders(VideoStreamInfo stream, ComponentSelectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);

        List<IVideoDecoderFactory> candidates = [];
        foreach (IVideoDecoderFactory factory in GetTypedFactories<IVideoDecoderFactory>(ExtensionPoint.VideoDecoder))
        {
            if (!SupportsCodec(factory.Capabilities, stream.CodecId))
            {
                continue;
            }

            candidates.Add(factory);
        }

        return OrderByPreference(candidates, options);
    }

    public IReadOnlyList<IAudioDecoderFactory> ResolveAudioDecoders(AudioStreamInfo stream, ComponentSelectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);

        List<IAudioDecoderFactory> candidates = [];
        foreach (IAudioDecoderFactory factory in GetTypedFactories<IAudioDecoderFactory>(ExtensionPoint.AudioDecoder))
        {
            if (!SupportsCodec(factory.Capabilities, stream.CodecId))
            {
                continue;
            }

            candidates.Add(factory);
        }

        return OrderByPreference(candidates, options);
    }

    public IReadOnlyList<ISubtitleDecoderFactory> ResolveSubtitleDecoders(SubtitleStreamInfo stream, ComponentSelectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);

        List<ISubtitleDecoderFactory> candidates = [];
        foreach (ISubtitleDecoderFactory factory in GetTypedFactories<ISubtitleDecoderFactory>(ExtensionPoint.SubtitleDecoder))
        {
            if (!SupportsCodec(factory.Capabilities, stream.CodecId))
            {
                continue;
            }

            candidates.Add(factory);
        }

        return OrderByPriority(candidates, options);
    }

    public IReadOnlyList<IComponentFactory<IVideoRenderer>> ResolveVideoRenderers(ComponentSelectionOptions options)
    {
        return OrderByPriority(GetTypedFactories<IComponentFactory<IVideoRenderer>>(ExtensionPoint.VideoRenderer), options);
    }

    public IReadOnlyList<IComponentFactory<IAudioRenderer>> ResolveAudioRenderers(ComponentSelectionOptions options)
    {
        return OrderByPriority(GetTypedFactories<IComponentFactory<IAudioRenderer>>(ExtensionPoint.AudioRenderer), options);
    }

    /// <summary>
    /// Video filters available for a stream. Filter order inside the chain is the caller's decision, so
    /// this list is ordered by preference only, for enumeration.
    /// </summary>
    public IReadOnlyList<IComponentFactory<IVideoFilter>> ResolveVideoFilters(ComponentSelectionOptions options)
    {
        return OrderByPriority(GetTypedFactories<IComponentFactory<IVideoFilter>>(ExtensionPoint.VideoFilter), options);
    }

    /// <summary>
    /// Audio filters available for a stream. See <see cref="ResolveVideoFilters"/> for the ordering
    /// caveat.
    /// </summary>
    public IReadOnlyList<IComponentFactory<IAudioFilter>> ResolveAudioFilters(ComponentSelectionOptions options)
    {
        return OrderByPriority(GetTypedFactories<IComponentFactory<IAudioFilter>>(ExtensionPoint.AudioFilter), options);
    }

    private List<TFactory> GetTypedFactories<TFactory>(ExtensionPoint extensionPoint)
    {
        List<TFactory> result = [];
        foreach (IComponentFactory factory in _registry.GetFactories(extensionPoint))
        {
            if (factory is TFactory typed)
            {
                result.Add(typed);
            }
        }

        return result;
    }

    /// <summary>
    /// Orders candidates that have no hardware or software distinction: the caller's named choice
    /// first, then by declared priority, with registration order breaking remaining ties.
    /// </summary>
    private static IReadOnlyList<TFactory> OrderByPriority<TFactory>(List<TFactory> candidates, ComponentSelectionOptions options) where TFactory : IComponentFactory
    {
        IEnumerable<TFactory> ordered = candidates
            .OrderByDescending(factory => IsPreferredName(factory, options.PreferredName))
            .ThenByDescending(factory => factory.Capabilities.Priority);

        return [.. ordered];
    }

    /// <summary>
    /// Orders decoder candidates: the caller's named choice first, then the preferred kind, then by
    /// declared priority. When fallback is disallowed the non-preferred kind is removed entirely.
    /// </summary>
    private static IReadOnlyList<TFactory> OrderByPreference<TFactory>(List<TFactory> candidates, ComponentSelectionOptions options) where TFactory : IComponentFactory
    {
        if (!options.AllowFallback)
        {
            candidates.RemoveAll(factory => factory.Capabilities.IsHardwareAccelerated != options.PreferHardwareAcceleration);
        }

        IEnumerable<TFactory> ordered = candidates
            .OrderByDescending(factory => IsPreferredName(factory, options.PreferredName))
            .ThenByDescending(factory => factory.Capabilities.IsHardwareAccelerated == options.PreferHardwareAcceleration)
            .ThenByDescending(factory => factory.Capabilities.Priority);

        return [.. ordered];
    }

    private static bool IsPreferredName(IComponentFactory factory, string? preferredName)
    {
        return preferredName is not null && string.Equals(factory.Capabilities.Name, preferredName, StringComparison.Ordinal);
    }

    private static bool SupportsCodec(ComponentCapabilities capabilities, string codecId)
    {
        return capabilities.CodecIds is null || capabilities.CodecIds.Contains(codecId);
    }

    private static bool SupportsScheme(ComponentCapabilities capabilities, string scheme)
    {
        return capabilities.Schemes is null || capabilities.Schemes.Contains(scheme);
    }
}
