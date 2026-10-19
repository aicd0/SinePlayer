// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Filter;
using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Core.Playback.Abstractions.Registry;

/// <summary>
/// Typed registration helpers. The registry itself is keyed by extension point; these overloads exist
/// so that a factory cannot be registered under the wrong role by accident.
/// </summary>
public static class ComponentRegistryExtensions
{
    public static void Register(this IComponentRegistry registry, ISourceProviderFactory factory)
    {
        registry.Register(ExtensionPoint.SourceProvider, factory);
    }

    public static void Register(this IComponentRegistry registry, IVideoDecoderFactory factory)
    {
        registry.Register(ExtensionPoint.VideoDecoder, factory);
    }

    public static void Register(this IComponentRegistry registry, IAudioDecoderFactory factory)
    {
        registry.Register(ExtensionPoint.AudioDecoder, factory);
    }

    public static void Register(this IComponentRegistry registry, ISubtitleDecoderFactory factory)
    {
        registry.Register(ExtensionPoint.SubtitleDecoder, factory);
    }

    public static void Register(this IComponentRegistry registry, IComponentFactory<IVideoRenderer> factory)
    {
        registry.Register(ExtensionPoint.VideoRenderer, factory);
    }

    public static void Register(this IComponentRegistry registry, IComponentFactory<IAudioRenderer> factory)
    {
        registry.Register(ExtensionPoint.AudioRenderer, factory);
    }

    public static void Register(this IComponentRegistry registry, IComponentFactory<ISubtitleRenderer> factory)
    {
        registry.Register(ExtensionPoint.SubtitleRenderer, factory);
    }

    public static void Register(this IComponentRegistry registry, IComponentFactory<IVideoFilter> factory)
    {
        registry.Register(ExtensionPoint.VideoFilter, factory);
    }

    public static void Register(this IComponentRegistry registry, IComponentFactory<IAudioFilter> factory)
    {
        registry.Register(ExtensionPoint.AudioFilter, factory);
    }
}
