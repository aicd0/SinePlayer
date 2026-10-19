// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Registry;

/// <summary>
/// What a component can do. A factory declares this once and the selection policy matches it against
/// the media being opened; only the fields relevant to the extension point are consulted:
/// <list type="bullet">
/// <item><description>source providers: <see cref="Schemes"/></description></item>
/// <item><description>decoders: <see cref="CodecIds"/> and <see cref="IsHardwareAccelerated"/></description></item>
/// <item><description>video renderers and filters: <see cref="PixelFormats"/></description></item>
/// <item><description>audio renderers and filters: <see cref="SampleFormats"/></description></item>
/// </list>
/// A null collection means the component places no restriction on that field.
/// </summary>
public sealed class ComponentCapabilities
{
    /// <summary>
    /// Stable identifier, unique within an extension point. This is what the user picks when choosing
    /// a decoder or a renderer, and what a profile refers to.
    /// </summary>
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Higher values win when two candidates are otherwise equal.</summary>
    public int Priority { get; init; }

    public bool IsHardwareAccelerated { get; init; }

    /// <summary>Codec identifiers this component handles, or null for any.</summary>
    public IReadOnlyCollection<string>? CodecIds { get; init; }

    /// <summary>Locator schemes this component handles, or null for any.</summary>
    public IReadOnlyCollection<string>? Schemes { get; init; }

    /// <summary>Pixel formats this component accepts, or null for any.</summary>
    public IReadOnlyCollection<PixelFormat>? PixelFormats { get; init; }

    /// <summary>Audio sample formats this component accepts, or null for any.</summary>
    public IReadOnlyCollection<AudioSampleFormat>? SampleFormats { get; init; }
}
