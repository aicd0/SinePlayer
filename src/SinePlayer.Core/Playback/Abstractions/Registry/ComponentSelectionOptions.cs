// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Core.Playback.Abstractions.Registry;

/// <summary>
/// Preferences for component selection. Resolution always produces an ordered list of candidates, so
/// these options shape that order instead of picking a single winner up front.
/// </summary>
public sealed class ComponentSelectionOptions
{
    public static ComponentSelectionOptions Default { get; } = new();

    /// <summary>Prefer hardware over software when both can handle the input.</summary>
    public bool PreferHardwareAcceleration { get; init; } = true;

    /// <summary>
    /// Whether candidates of the non-preferred kind are still returned, after the preferred ones.
    /// When false only the preferred kind is returned, which is how a caller demands "hardware only".
    /// </summary>
    public bool AllowFallback { get; init; } = true;

    /// <summary>
    /// Name of the component to place first when it is among the candidates. This is how an explicit
    /// user choice overrides the automatic ordering.
    /// </summary>
    public string? PreferredName { get; init; }
}
