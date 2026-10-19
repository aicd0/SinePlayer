// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.SDK.Plugins.Property;

public interface IVirtualProperty<A>
{
    string Name { get; }

    string DisplayName { get; }

    IVirtualPropertySorter<A> CreateSorter<T>(IEnumerable<T> items) where T : A;
}
