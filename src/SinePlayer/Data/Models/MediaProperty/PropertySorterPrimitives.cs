// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Linq;

using SinePlayer.Data.Models.Misc;

namespace SinePlayer.Data.Models.MediaProperty;

internal interface IItemSorter<K>
{
    List<T> Sort<T>(IEnumerable<T> items, Func<T, K> selector);
}

internal interface IGroupSorter<K, M>
{
    List<T> Sort<T>(IEnumerable<T> items, Func<T, K> selector, Action<T, M> keyBinder);
}

internal class RandomSorter<K>(MediaFilterModel.OrderMethodEnum orderMethod, Func<K, int> idSelector) : IItemSorter<K>, IGroupSorter<K, string>
{
    protected Func<K, int> IdSelector { get; } = idSelector;

    public List<T> Sort<T>(IEnumerable<T> items, Func<T, K> selector, Action<T, string> keyBinder)
    {
        return Sort(items, selector);
    }

    public List<T> Sort<T>(IEnumerable<T> items, Func<T, K> selector)
    {
        switch (orderMethod)
        {
            case MediaFilterModel.OrderMethodEnum.Shuffle:
                {
                    int salt = Random.Shared.Next();
                    return [.. items.OrderBy(x => IdSelector(selector(x)) ^ salt)];
                }
            case MediaFilterModel.OrderMethodEnum.ShuffleStable:
                {
                    var rng = new Random(AppSettingsModel.MediaShuffleRandomSeed);
                    int salt = rng.Next();
                    return [.. items.OrderBy(x => IdSelector(selector(x)) ^ salt)];
                }
            default:
                throw new Exception("Invalid order method for RandomSorter");
        }
    }
}

internal class BasicItemSorter<A, B>(MediaFilterModel.OrderMethodEnum orderMethod, Func<A, B> keySelector, IComparer<B>? keyComparer = null) : IItemSorter<A>
{
    protected IComparer<B> KeyComparer { get; } = keyComparer ?? Comparer<B>.Default;
    protected Func<A, B> KeySelector { get; } = keySelector;

    public List<T> Sort<T>(IEnumerable<T> items, Func<T, A> selector)
    {
        return orderMethod switch
        {
            MediaFilterModel.OrderMethodEnum.Ascending => [.. items.OrderBy(x => KeySelector(selector(x)), KeyComparer)],
            MediaFilterModel.OrderMethodEnum.Descending => [.. items.OrderByDescending(x => KeySelector(selector(x)), KeyComparer)],
            _ => throw new Exception("Invalid order method for BasicItemSorter"),
        };
    }
}

internal class BasicGroupSorter<A, B>(MediaFilterModel.OrderMethodEnum orderMethod, Func<A, B> keySelector, Func<B, string>? keyInfoConverter = null, IComparer<B>? keyComparer = null) : IGroupSorter<A, string>
{
    protected Func<A, B> KeySelector { get; } = keySelector;
    private Func<B, string> KeyInfoConverter { get; } = keyInfoConverter ?? (_ => string.Empty);
    protected IComparer<B> KeyComparer { get; } = keyComparer ?? Comparer<B>.Default;

    public List<T> Sort<T>(IEnumerable<T> items, Func<T, A> selector, Action<T, string> keyBinder)
    {
        IEnumerable<Tuple<B, T>> pairs = items.Select(item =>
        {
            B key = KeySelector(selector(item));
            keyBinder(item, KeyInfoConverter(key));
            return new Tuple<B, T>(key, item);
        });

        pairs = orderMethod switch
        {
            MediaFilterModel.OrderMethodEnum.Ascending => pairs.OrderBy(p => p.Item1, KeyComparer),
            MediaFilterModel.OrderMethodEnum.Descending => pairs.OrderByDescending(p => p.Item1, KeyComparer),
            _ => throw new Exception("Invalid order method for BasicGroupSorter"),
        };

        return [.. pairs.Select(p => p.Item2)];
    }
}
