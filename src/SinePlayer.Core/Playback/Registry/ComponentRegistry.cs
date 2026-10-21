// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Registry;

namespace SinePlayer.Core.Playback.Registry;

public sealed class ComponentRegistry : IComponentRegistry
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Type, List<IComponentFactory>> _components = [];

    public IComponentRegistry Clone()
    {
        lock (_lock)
        {
            // A copy of the registrations, not of the factories: a factory is the identity a registration is
            // addressed by, so copying one would make the copy unable to answer for the original's entries.
            ComponentRegistry clone = new();
            foreach (KeyValuePair<Type, List<IComponentFactory>> entry in _components)
            {
                clone._components[entry.Key] = [.. entry.Value];
            }

            return clone;
        }
    }

    public IReadOnlyList<TFactory> GetComponents<TFactory>() where TFactory : class, IComponentFactory
    {
        lock (_lock)
        {
            if (!_components.TryGetValue(typeof(TFactory), out List<IComponentFactory>? components))
            {
                return [];
            }

            var copy = new TFactory[components.Count];
            for (int index = 0; index < components.Count; index++)
            {
                copy[index] = (TFactory)components[index];
            }

            return copy;
        }
    }

    public void Register(IComponentFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        Type role = RoleOf(factory);

        lock (_lock)
        {
            List<IComponentFactory> components = GetOrCreate(role);
            if (components.Contains(factory))
            {
                throw new InvalidOperationException($"{role.Name} already has this instance. Remove it or clear the role before registering it again.");
            }

            components.Add(factory);
        }
    }

    public bool Remove(IComponentFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        Type role = RoleOf(factory);

        lock (_lock)
        {
            if (!_components.TryGetValue(role, out List<IComponentFactory>? components))
            {
                return false;
            }

            int index = components.IndexOf(factory);
            if (index < 0)
            {
                return false;
            }

            components.RemoveAt(index);
            return true;
        }
    }

    public int Clear<TFactory>() where TFactory : class, IComponentFactory
    {
        lock (_lock)
        {
            if (!_components.Remove(typeof(TFactory), out List<IComponentFactory>? removed))
            {
                return 0;
            }

            return removed.Count;
        }
    }

    private static Type RoleOf(IComponentFactory factory)
    {
        Type factoryType = factory.GetType();
        Type[] interfaces = factoryType.GetInterfaces();

        Type? role = null;
        foreach (Type candidate in interfaces)
        {
            if (!IsRole(candidate))
            {
                continue;
            }

            if (role is not null && role != candidate)
            {
                IEnumerable<string> roles = interfaces.Where(IsRole).Select(type => type.Name);
                throw new ArgumentException($"{factoryType.Name} implements more than one role ({string.Join(", ", roles)}), so there is no way to tell which list it belongs in.");
            }

            role = candidate;
        }

        return role ?? throw new ArgumentException($"{factoryType.Name} implements no component role, so it cannot be registered.");
    }

    private static bool IsRole(Type candidate)
    {
        return candidate != typeof(IComponentFactory) && typeof(IComponentFactory).IsAssignableFrom(candidate);
    }

    private List<IComponentFactory> GetOrCreate(Type role)
    {
        if (!_components.TryGetValue(role, out List<IComponentFactory>? components))
        {
            components = [];
            _components[role] = components;
        }

        return components;
    }
}
