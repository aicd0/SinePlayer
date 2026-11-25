// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Linq;

using SinePlayer.Common.Localization;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.SDK.Plugins.Media;
using SinePlayer.SDK.Plugins.Property;

using PropertyTypeEnum = SinePlayer.Data.Models.MediaProperty.MediaPropertyModel.PropertyTypeEnum;

namespace SinePlayer.Data.Models.MediaProperty;

internal static class MediaPropertySorter
{
    private const string TAG = nameof(MediaPropertySorter);

    public static List<T> SortMedia<T>(this MediaPropertyModel property, IEnumerable<T> items, Func<T, MediaModel> selector, MediaFilterModel.OrderMethodEnum orderMethod)
    {
        IEnumerable<T> preSorted = items.OrderBy(x => StringUtils.SmartFileNameKeySelector(selector(x).Title ?? StringResourceProvider.Instance.Untitled), StringUtils.SmartFileNameComparer);
        IVirtualPropertySorter<IMediaModel>? pluginSorter = property.PluginProperty?.CreateSorter(preSorted.Select(selector));
        IItemSorter<MediaModel> sorter = GetMediaItemSorter(property, orderMethod, pluginSorter);
        return sorter.Sort(preSorted, selector);
    }

    private static IItemSorter<MediaModel> GetMediaItemSorter(MediaPropertyModel property, MediaFilterModel.OrderMethodEnum orderMethod, IVirtualPropertySorter<IMediaModel>? pluginSorter)
    {
        switch (orderMethod)
        {
            case MediaFilterModel.OrderMethodEnum.Shuffle:
            case MediaFilterModel.OrderMethodEnum.ShuffleStable:
                int IdSelector(MediaModel x) => HashUtils.GetXxHash64Int(x.Id);
                return new RandomSorter<MediaModel>(orderMethod, IdSelector);
            default:
                break;
        }

        string GetConcatenatedTag(MediaModel media)
        {
            if (!media.Tags.TryGetValue(property.Name, out MediaTagCategory? category))
            {
                return string.Empty;
            }

            List<string> tags = [.. category.Tags];
            tags.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(' ', tags);
        }

        int CompletionStateToComparable(Media.CompletionStatusEnum status)
        {
            return CompletionStatusService.EnumToComparable(status);
        }

        IItemSorter<MediaModel> CreateSorter<T>(Func<MediaModel, T> keySelector, IComparer<T>? keyComparer = null)
        {
            return new BasicItemSorter<MediaModel, T>(orderMethod, keySelector, keyComparer);
        }

        IItemSorter<MediaModel> CreateFallbackSorter()
        {
            Logger.F(TAG, "Using fallback sorter");
            return CreateSorter(x => x.Id);
        }

        IItemSorter<MediaModel> CreatePluginPropertySorter()
        {
            if (pluginSorter is null)
            {
                return CreateFallbackSorter();
            }

            return new PluginPropertyItemSorter(pluginSorter, orderMethod);
        }

        return property.Type switch
        {
            PropertyTypeEnum.Title => CreateSorter(x => StringUtils.SmartFileNameKeySelector(x.Title ?? StringResourceProvider.Instance.Untitled), keyComparer: StringUtils.SmartFileNameComparer),
            PropertyTypeEnum.Progress => CreateSorter(x => x.Progress),
            PropertyTypeEnum.Tag => CreateSorter(x => StringUtils.SmartFileNameKeySelector(GetConcatenatedTag(x)), keyComparer: StringUtils.SmartFileNameComparer),
            PropertyTypeEnum.Rating => CreateSorter(x => x.Rating),
            PropertyTypeEnum.CompletionStatus => CreateSorter(x => CompletionStateToComparable(x.CompletionStatus)),
            PropertyTypeEnum.LastVisitTime => CreateSorter(x => x.LastVisit.Ticks),
            PropertyTypeEnum.Duration => CreateSorter(x => x.DurationMs),
            PropertyTypeEnum.ReleaseDate => CreateSorter(x => x.ReleaseDate),
            PropertyTypeEnum.PluginVirtualProperty => CreatePluginPropertySorter(),
            _ => CreateFallbackSorter(),
        };
    }
}

internal class PluginPropertyItemSorter(IVirtualPropertySorter<IMediaModel> sorter, MediaFilterModel.OrderMethodEnum orderMethod) : IItemSorter<MediaModel>
{
    public List<T> Sort<T>(IEnumerable<T> items, Func<T, MediaModel> selector)
    {
        ILookup<MediaModel, T> lookup = items.ToLookup(t => selector(t));
        IEnumerable<T> sorted = sorter
            .SortItems(items.Select(x => selector(x)))
            .SelectMany(t => lookup[t]);
        return orderMethod switch
        {
            MediaFilterModel.OrderMethodEnum.Ascending => [.. sorted],
            MediaFilterModel.OrderMethodEnum.Descending => [.. sorted.Reverse()],
            _ => throw new Exception("Invalid order method for PluginPropertyItemSorter"),
        };
    }
}
