// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using SinePlayer.Common.Localization;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.AppEnvironment;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.SDK.Plugins.Media;
using SinePlayer.SDK.Plugins.Property;

using PropertyTypeEnum = SinePlayer.Data.Models.MediaProperty.MediaPropertyModel.PropertyTypeEnum;

namespace SinePlayer.Data.Models.MediaProperty;

internal static class MediaPropertyGrouper
{
    private const string TAG = nameof(MediaPropertyGrouper);

    public static List<GroupItem<T>> GroupMedia<T>(this MediaPropertyModel property,
        IEnumerable<T> items,
        Func<T, MediaModel> selector,
        MediaFilterModel.OrderMethodEnum orderMethod,
        MediaFilterModel.FunctionTypeEnum sortingFunction,
        MediaPropertyModel? sortingProperty,
        bool mergeSingleItemGroups)
    {
        IEnumerable<T> preSorted = items.OrderBy(x => StringUtils.SmartFileNameKeySelector(selector(x).Title ?? StringResourceProvider.Instance.Untitled), StringUtils.SmartFileNameComparer);
        IVirtualPropertySorter<IMediaModel>? pluginSorter = property.PluginProperty?.CreateSorter(preSorted.Select(selector));
        IVirtualPropertySorter<IMediaModel>? sortingPropertyPluginSorter = sortingProperty?.PluginProperty?.CreateSorter(preSorted.Select(selector));
        MediaGrouper grouper = GetMediaGrouper(property, orderMethod, pluginSorter, sortingFunction, sortingProperty, sortingPropertyPluginSorter);
        return grouper.GroupMedia(preSorted, selector, mergeSingleItemGroups);
    }

    private static MediaGrouper GetMediaGrouper(MediaPropertyModel property, MediaFilterModel.OrderMethodEnum orderMethod, IVirtualPropertySorter<IMediaModel>? pluginSorter,
        MediaFilterModel.FunctionTypeEnum sortingFunction, MediaPropertyModel? sortingProperty, IVirtualPropertySorter<IMediaModel>? sortingPropertyPluginSorter)
    {
        string GetTitleGroupName(MediaModel media)
        {
            string title = media.Title.TrimStart();
            string name;
            if (string.IsNullOrEmpty(title))
            {
                name = StringResourceProvider.Instance.Untitled;
            }
            else
            {
                name = title[0].ToString().ToUpper();
            }

            return name;
        }

        string GetProgressGroupName(MediaModel media)
        {
            int progress = media.Progress;
            if (progress < 10)
            {
                return "<10%";
            }

            if (progress >= 100)
            {
                return "100%";
            }

            return $"{progress / 10 * 10}%";
        }

        IEnumerable<string> GetTagGroupNames(MediaModel media)
        {
            if (!media.Tags.TryGetValue(property.Name, out MediaTagCategory? category))
            {
                string name = StringResourceProvider.Instance.Ungrouped;
                return (List<string>)[name];
            }

            return (List<string>)[.. category.Tags];
        }

        string GetRatingGroupName(MediaModel media)
        {
            int rating = media.Rating;
            if (rating < 0)
            {
                return StringResourceProvider.Instance.NoRating;
            }

            int level = Math.Min(rating, 99) / 10;
            if (level <= 0)
            {
                return "<0.5";
            }

            return (level * 0.5F).ToString("0.#");
        }

        string GetDurationGroupName(MediaModel media)
        {
            double minutes = media.DurationMs / 60000.0;
            if (minutes <= 0)
            {
                return StringResourceProvider.Instance.Ungrouped;
            }

            if (minutes < 10)
            {
                return "<10m";
            }

            if (minutes < 60)
            {
                return $"{(int)(minutes / 10) * 10}m";
            }

            int hours = (int)(minutes / 60);
            if (hours < 10)
            {
                return $"{hours}h";
            }

            return "10h+";
        }

        string GetCompletionStatusGroupName(MediaModel media)
        {
            return CompletionStatusService.EnumToString(media.CompletionStatus, fallback: StringResourceProvider.Instance.Ungrouped);
        }

        int GetCompletionStatusGroupSortingKey(MediaModel media)
        {
            return CompletionStatusService.EnumToComparable(media.CompletionStatus);
        }

        string GetReleaseDateVariantGroupName(MediaModel media)
        {
            if (media.ReleaseDate is not { } releaseDate)
            {
                return StringResourceProvider.Instance.Ungrouped;
            }

            return GetDateGroupName(releaseDate, property.Type);
        }

        string GetLastReadTimeVariantGroupName(MediaModel media)
        {
            DateTimeOffset lastReadTime = media.LastVisit;
            if (lastReadTime == DateTimeOffset.MinValue)
            {
                return StringResourceProvider.Instance.Ungrouped;
            }

            return GetDateGroupName(lastReadTime.DateTime, property.Type);
        }

        long GetReleaseDateVariantGroupSortingKey(MediaModel media)
        {
            return media.ReleaseDate is { } releaseDate ? GetPeriodStart(releaseDate, property.Type).Ticks : long.MinValue;
        }

        long GetLastReadTimeVariantGroupSortingKey(MediaModel media)
        {
            DateTimeOffset lastReadTime = media.LastVisit;
            return lastReadTime != DateTimeOffset.MinValue ? GetPeriodStart(lastReadTime.DateTime, property.Type).Ticks : long.MinValue;
        }

        IGroupSorter<MediaGroup, string> CreateSorter<T>(Func<MediaGroup, T> keySelector, Func<T, string>? keyInfoConverter = null, IComparer<T>? keyComparer = null)
        {
            return new BasicGroupSorter<MediaGroup, T>(orderMethod, keySelector, keyInfoConverter, keyComparer);
        }

        IGroupSorter<MediaGroup, string> CreateFallbackSorter()
        {
            Logger.F(TAG, "Using fallback sorter");
            return CreateSorter(x => 0);
        }

        IGroupSorter<MediaGroup, string> CreatePluginPropertySorter()
        {
            if (pluginSorter is null)
            {
                return CreateFallbackSorter();
            }

            return new PluginPropertyGroupSorter(pluginSorter, orderMethod);
        }

        IGroupSorter<MediaGroup, string> CreateDefaultSorter()
        {
            switch (orderMethod)
            {
                case MediaFilterModel.OrderMethodEnum.Shuffle:
                case MediaFilterModel.OrderMethodEnum.ShuffleStable:
                    int IdSelector(MediaGroup x) => HashUtils.GetXxHash64Int(x.GroupName);
                    return new RandomSorter<MediaGroup>(orderMethod, IdSelector);
                default:
                    break;
            }

            return property.Type switch
            {
                PropertyTypeEnum.Title => CreateSorter(x => StringUtils.SmartFileNameKeySelector(x.GroupName), keyComparer: StringUtils.SmartFileNameComparer),
                PropertyTypeEnum.Progress => CreateSorter(x => x.Items[0].Progress),
                PropertyTypeEnum.Tag => CreateSorter(x => StringUtils.SmartFileNameKeySelector(x.GroupName), keyComparer: StringUtils.SmartFileNameComparer),
                PropertyTypeEnum.Rating => CreateSorter(x => x.Items[0].Rating),
                PropertyTypeEnum.CompletionStatus => CreateSorter(x => GetCompletionStatusGroupSortingKey(x.Items[0])),
                PropertyTypeEnum.LastVisitTimeByYear or PropertyTypeEnum.LastVisitTimeBySeason or PropertyTypeEnum.LastVisitTimeByMonth => CreateSorter(x => GetLastReadTimeVariantGroupSortingKey(x.Items[0])),
                PropertyTypeEnum.Duration => CreateSorter(x => x.Items[0].DurationMs),
                PropertyTypeEnum.ReleaseDateByYear or PropertyTypeEnum.ReleaseDateBySeason or PropertyTypeEnum.ReleaseDateByMonth => CreateSorter(x => GetReleaseDateVariantGroupSortingKey(x.Items[0])),
                PropertyTypeEnum.PluginVirtualProperty => CreatePluginPropertySorter(),
                _ => CreateFallbackSorter(),
            };
        }

        IGroupSorter<MediaGroup, string> defaultSorter = CreateDefaultSorter();

        MediaGrouper CreateGrouper(Func<MediaModel, IEnumerable<string>> groupNameSelector)
        {
            IGroupSorter<MediaGroup, string> groupSorter = GetGroupSorter(orderMethod, sortingFunction, sortingProperty, sortingPropertyPluginSorter, defaultSorter);
            return new(groupSorter, groupNameSelector);
        }

        MediaGrouper CreateFallbackGrouper()
        {
            Logger.F(TAG, "Using fallback grouper");
            return CreateGrouper(x => (List<string>)[new(StringResourceProvider.Instance.Ungrouped)]);
        }

        MediaGrouper CreatePluginPropertyGrouper()
        {
            if (pluginSorter is null)
            {
                return CreateFallbackGrouper();
            }

            return CreateGrouper(pluginSorter.GetGroupNames);
        }

        return property.Type switch
        {
            PropertyTypeEnum.Title => CreateGrouper(x => (List<string>)[GetTitleGroupName(x)]),
            PropertyTypeEnum.Progress => CreateGrouper(x => (List<string>)[GetProgressGroupName(x)]),
            PropertyTypeEnum.Tag => CreateGrouper(GetTagGroupNames),
            PropertyTypeEnum.Rating => CreateGrouper(x => (List<string>)[GetRatingGroupName(x)]),
            PropertyTypeEnum.CompletionStatus => CreateGrouper(x => (List<string>)[GetCompletionStatusGroupName(x)]),
            PropertyTypeEnum.LastVisitTimeByYear or PropertyTypeEnum.LastVisitTimeBySeason or PropertyTypeEnum.LastVisitTimeByMonth => CreateGrouper(x => (List<string>)[GetLastReadTimeVariantGroupName(x)]),
            PropertyTypeEnum.Duration => CreateGrouper(x => (List<string>)[GetDurationGroupName(x)]),
            PropertyTypeEnum.ReleaseDateByYear or PropertyTypeEnum.ReleaseDateBySeason or PropertyTypeEnum.ReleaseDateByMonth => CreateGrouper(x => (List<string>)[GetReleaseDateVariantGroupName(x)]),
            PropertyTypeEnum.PluginVirtualProperty => CreatePluginPropertyGrouper(),
            _ => CreateFallbackGrouper(),
        };
    }

    private static IGroupSorter<MediaGroup, string> GetGroupSorter(MediaFilterModel.OrderMethodEnum orderMethod,
        MediaFilterModel.FunctionTypeEnum sortingFunction, MediaPropertyModel? sortingProperty,
        IVirtualPropertySorter<IMediaModel>? sortingPropertyPluginSorter, IGroupSorter<MediaGroup, string> defaultSorter)
    {
        switch (orderMethod)
        {
            case MediaFilterModel.OrderMethodEnum.Shuffle:
            case MediaFilterModel.OrderMethodEnum.ShuffleStable:
                int IdSelector(MediaGroup x) => HashUtils.GetXxHash64Int(x.GroupName);
                return new RandomSorter<MediaGroup>(orderMethod, IdSelector);
            default:
                break;
        }

        double? PropertyToNumber(MediaModel media)
        {
            return sortingProperty?.Type switch
            {
                PropertyTypeEnum.Title => media.Title.Length,
                PropertyTypeEnum.Tag => sortingProperty.Name.Length,
                PropertyTypeEnum.Progress => Math.Max(media.Progress, 0),
                PropertyTypeEnum.Rating => media.Rating >= 0 ? media.Rating * 0.05 : null,
                PropertyTypeEnum.CompletionStatus => (int)media.CompletionStatus,
                PropertyTypeEnum.LastVisitTime => media.LastVisit != DateTimeOffset.MinValue ? media.LastVisit.ToUnixTimeMilliseconds() : null,
                PropertyTypeEnum.Duration => media.DurationMs > 0 ? media.DurationMs : null,
                PropertyTypeEnum.ReleaseDate => media.ReleaseDate is { } releaseDate ? (releaseDate - DateTime.UnixEpoch).TotalMilliseconds : null,
                PropertyTypeEnum.PluginVirtualProperty => sortingPropertyPluginSorter?.AsNumber(media),
                _ => null,
            };
        }

        string NumberToString(double value)
        {
            return Math.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.##");
        }

        IGroupSorter<MediaGroup, string> CreateSorter<T>(Func<MediaGroup, T> keySelector, Func<T, string>? keyInfoConverter = null, IComparer<T>? keyComparer = null)
        {
            return new BasicGroupSorter<MediaGroup, T>(orderMethod, keySelector, keyInfoConverter, keyComparer);
        }

        return sortingFunction switch
        {
            MediaFilterModel.FunctionTypeEnum.None => defaultSorter,
            MediaFilterModel.FunctionTypeEnum.ItemCount => CreateSorter(x => x.Items.Count, keyInfoConverter: x => x.ToString()),
            MediaFilterModel.FunctionTypeEnum.Max => CreateSorter(x => x.Items.Max(PropertyToNumber) ?? 0, keyInfoConverter: NumberToString),
            MediaFilterModel.FunctionTypeEnum.Min => CreateSorter(x => x.Items.Min(PropertyToNumber) ?? 0, keyInfoConverter: NumberToString),
            MediaFilterModel.FunctionTypeEnum.Sum => CreateSorter(x => x.Items.Sum(PropertyToNumber) ?? 0, keyInfoConverter: NumberToString),
            MediaFilterModel.FunctionTypeEnum.Average => CreateSorter(x => x.Items.Average(PropertyToNumber) ?? 0, keyInfoConverter: NumberToString),
            _ => throw new ArgumentOutOfRangeException(nameof(sortingFunction)),
        };
    }

    private static DateTime GetPeriodStart(DateTime value, PropertyTypeEnum type)
    {
        return type switch
        {
            PropertyTypeEnum.LastVisitTimeBySeason or PropertyTypeEnum.ReleaseDateBySeason => new DateTime(value.Year, (value.Month - 1) / 3 * 3 + 1, 1),
            PropertyTypeEnum.LastVisitTimeByMonth or PropertyTypeEnum.ReleaseDateByMonth => new DateTime(value.Year, value.Month, 1),
            _ => new DateTime(value.Year, 1, 1),
        };
    }

    private static string GetDateGroupName(DateTime value, PropertyTypeEnum type)
    {
        DateTime periodStart = GetPeriodStart(value, type);
        if (type is PropertyTypeEnum.LastVisitTimeBySeason or PropertyTypeEnum.ReleaseDateBySeason)
        {
            int quarter = (periodStart.Month - 1) / 3 + 1;
            return string.Format(CultureInfo.InvariantCulture, "{0} Q{1}", periodStart.Year, quarter);
        }

        if (type is PropertyTypeEnum.LastVisitTimeByMonth or PropertyTypeEnum.ReleaseDateByMonth)
        {
            return periodStart.ToString("Y", EnvironmentProvider.Instance.GetCurrentAppLanguageInfo());
        }

        return periodStart.Year.ToString(CultureInfo.InvariantCulture);
    }
}

internal class GroupItem<T>(List<T> items, string name)
{
    public List<T> Items { get; } = items;
    public string Name { get; } = name;
    public string Description { get; set; } = string.Empty;
}

internal class MediaGroup(string groupName, List<MediaModel> items) : IItemGroup<IMediaModel>
{
    public string GroupName { get; } = groupName;
    public IReadOnlyList<MediaModel> Items { get; } = items;

    //
    // IItemGroup<IMediaModel> implementation
    //

    string IItemGroup<IMediaModel>.Name => GroupName;

    IReadOnlyList<IMediaModel> IItemGroup<IMediaModel>.Items => Items;
}

internal class MediaGrouper(IGroupSorter<MediaGroup, string> groupSorter, Func<MediaModel, IEnumerable<string>> groupNameSelector)
{
    private readonly IGroupSorter<MediaGroup, string> _groupSorter = groupSorter;
    private readonly Func<MediaModel, IEnumerable<string>> _groupNameSelector = groupNameSelector;

    public List<GroupItem<T>> GroupMedia<T>(IEnumerable<T> items, Func<T, MediaModel> selector, bool mergeSingleItemGroups)
    {
        Dictionary<string, List<T>> groupMap = [];
        foreach (T item in items)
        {
            IEnumerable<string> groupNames = _groupNameSelector(selector(item));
            foreach (string name in groupNames)
            {
                if (!groupMap.TryGetValue(name, out List<T>? group))
                {
                    group = [];
                    groupMap[name] = group;
                }

                group.Add(item);
            }
        }

        List<GroupItem<T>> mediaGroups = [];
        foreach (KeyValuePair<string, List<T>> p in groupMap)
        {
            mediaGroups.Add(new(p.Value, p.Key));
        }

        if (mergeSingleItemGroups)
        {
            var singleItemGroups = mediaGroups.Where(x => x.Items.Count == 1).ToList();
            if (singleItemGroups.Count >= 2)
            {
                var mergedGroup = singleItemGroups.SelectMany(x => x.Items).ToList();
                mediaGroups.RemoveAll(x => x.Items.Count == 1);
                mediaGroups.Add(new GroupItem<T>(mergedGroup, StringResourceProvider.Instance.Miscellaneous));
            }
        }

        List<GroupItem<T>> sorted = _groupSorter.Sort(mediaGroups, x => new(x.Name, x.Items.ConvertAll(y => selector(y))), (m, t) =>
        {
            if (!string.IsNullOrEmpty(t))
            {
                m.Description = $"({t})";
            }
        });

        if (sorted.All(x => string.IsNullOrEmpty(x.Description)))
        {
            sorted.ForEach(x => x.Description = $"({x.Items.Count})");
        }

        return sorted;
    }
}

internal class PluginPropertyGroupSorter(IVirtualPropertySorter<IMediaModel> sorter, MediaFilterModel.OrderMethodEnum orderMethod) : IGroupSorter<MediaGroup, string>
{
    public List<T> Sort<T>(IEnumerable<T> items, Func<T, MediaGroup> selector, Action<T, string> keyBinder)
    {
        ILookup<string, T> lookup = items.ToLookup(t => selector(t).GroupName);
        IEnumerable<T> sorted = sorter
            .SortGroups(items.Select(x => selector(x)))
            .SelectMany(t => lookup[t.GroupName]);
        return orderMethod switch
        {
            MediaFilterModel.OrderMethodEnum.Ascending => [.. sorted],
            MediaFilterModel.OrderMethodEnum.Descending => [.. sorted.Reverse()],
            _ => throw new Exception("Invalid order method for PluginPropertyGroupSorter"),
        };
    }
}
