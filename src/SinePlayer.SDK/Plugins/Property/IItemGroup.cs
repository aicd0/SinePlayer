// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.SDK.Plugins.Property;

public interface IItemGroup<A>
{
    string Name { get; }

    IReadOnlyList<A> Items { get; }
}
