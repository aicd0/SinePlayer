// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayerUWP.SDK.Plugins.Property;

public interface IItemGroup<A>
{
    string Name { get; }

    IReadOnlyList<A> Items { get; }
}
