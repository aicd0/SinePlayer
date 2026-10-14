// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace SinePlayer.Core.Playback.Abstractions.Registry;

/// <summary>
/// Where components are declared. Registration happens once while the engine is being built up; the
/// pipeline only ever asks for candidates, so a component can be added or removed without it
/// changing.
/// </summary>
public interface IComponentRegistry
{
    void Register(ExtensionPoint extensionPoint, IComponentFactory factory);

    /// <summary>
    /// Factories registered for a role, in registration order. Selection is what imposes an order
    /// based on the media and the caller's preferences.
    /// </summary>
    IReadOnlyList<IComponentFactory> GetFactories(ExtensionPoint extensionPoint);
}
