// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Registry;

namespace SinePlayer.Core.Playback.Registry;

/// <summary>
/// Default registry. Registration is expected to happen while the engine is being built; reads may
/// happen from the pipeline at any time, hence the lock.
/// </summary>
public sealed class ComponentRegistry : IComponentRegistry
{
    private readonly object _lock = new();

    private readonly Dictionary<ExtensionPoint, List<IComponentFactory>> _factories = [];

    public void Register(ExtensionPoint extensionPoint, IComponentFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        lock (_lock)
        {
            if (!_factories.TryGetValue(extensionPoint, out List<IComponentFactory>? factories))
            {
                factories = [];
                _factories[extensionPoint] = factories;
            }

            factories.Add(factory);
        }
    }

    public IReadOnlyList<IComponentFactory> GetFactories(ExtensionPoint extensionPoint)
    {
        lock (_lock)
        {
            if (_factories.TryGetValue(extensionPoint, out List<IComponentFactory>? factories))
            {
                return [.. factories];
            }
        }

        return [];
    }
}
