// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.SDK.Plugins.Property;

public interface IVirtualPropertySorter<A>
{
    IEnumerable<T> SortItems<T>(IEnumerable<T> items) where T : A;

    IEnumerable<T> SortGroups<T>(IEnumerable<T> items) where T : IItemGroup<A>;

    IEnumerable<string> GetGroupNames(A item);

    double? AsNumber(A item);
}
