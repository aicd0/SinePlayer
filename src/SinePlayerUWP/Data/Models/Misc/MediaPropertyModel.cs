// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using SinePlayerUWP.Common.Localization;
using SinePlayerUWP.Common.Plugins;
using SinePlayerUWP.Common.Utils;
using SinePlayerUWP.Core.Common.AppEnvironment;
using SinePlayerUWP.Core.Common.DebugTools;
using SinePlayerUWP.Core.Common.Utils;
using SinePlayerUWP.Data.Models.Media;
using SinePlayerUWP.SDK.Plugins.Media;
using SinePlayerUWP.SDK.Plugins.Property;

namespace SinePlayerUWP.Data.Models.Misc;

internal class MediaPropertyModel
{
    private const string TAG = nameof(MediaPropertyModel);
    private const string PROP_TYPE_TITLE = "Title";
    private const string PROP_TYPE_PROGRESS = "Progress";
    private const string PROP_TYPE_TAG = "Tag";
    private const string PROP_TYPE_RATING = "Rating";
    private const string PROP_TYPE_COMPLETION_STATUS = "CompletionState";
    private const string PROP_TYPE_LAST_VISIT_TIME = "LastVisitTime";
    private const string PROP_TYPE_DURATION = "Duration";
    private const string PROP_TYPE_PLUGIN_VIRTUAL_PROPERTY = "PluginVirtualProperty";

    private static readonly List<PropertyTypeEnum> _properties = [
        PropertyTypeEnum.Title,
        PropertyTypeEnum.Progress,
        PropertyTypeEnum.Rating,
        PropertyTypeEnum.CompletionStatus,
        PropertyTypeEnum.LastVisitTime,
        PropertyTypeEnum.Duration,
    ];

    private PropertyTypeEnum Type { get; init; } = PropertyTypeEnum.Title;
    private string Name { get; init; } = string.Empty;

    public string DisplayGroupName => Type switch
    {
        PropertyTypeEnum.Tag => StringResourceProvider.Instance.Tag,
        _ => string.Empty,
    };

    private string _displayName = string.Empty;
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrEmpty(_displayName))
            {
                return _displayName;
            }

            return Type switch
            {
                PropertyTypeEnum.Title => StringResourceProvider.Instance.Title,
                PropertyTypeEnum.Progress => StringResourceProvider.Instance.Progress,
                PropertyTypeEnum.Tag => Name,
                PropertyTypeEnum.Rating => StringResourceProvider.Instance.Rating,
                PropertyTypeEnum.CompletionStatus => StringResourceProvider.Instance.CompletionStatus,
                PropertyTypeEnum.LastVisitTime => StringResourceProvider.Instance.LastVisitTime,
                PropertyTypeEnum.Duration => StringResourceProvider.Instance.Duration,
                _ => string.Empty,
            };
        }
        set => _displayName = value;
    }

    private bool _pluginPropertyInitialized = false;
    private IVirtualProperty<IMediaModel>? _pluginProperty;
    private IVirtualProperty<IMediaModel>? PluginProperty
    {
        get
        {
            if (_pluginPropertyInitialized)
            {
                return _pluginProperty;
            }

            _pluginPropertyInitialized = true;
            if (Type != PropertyTypeEnum.PluginVirtualProperty)
            {
                return null;
            }

            string[] pieces = Name.Split(':');
            if (pieces.Length != 2)
            {
                return null;
            }

            string pluginName = pieces[0];
            string propertyName = pieces[1];
            PluginContext? plugin = PluginManager.Instance.GetActivePlugin(pluginName);
            if (plugin is null)
            {
                return null;
            }

            _pluginProperty = plugin.GetAllMediaVirtualProperties().FirstOrDefault(x => x.Name == propertyName);
            return _pluginProperty;
        }
    }

    public override bool Equals(object? obj)
    {
        if (obj is MediaPropertyModel other)
        {
            return Type == other.Type && Name == other.Name;
        }

        return false;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Type, Name);
    }

    public JsonNode? ToJson()
    {
        var jsonModel = new JsonModel
        {
            Type = PropertyTypeToString(Type),
            Name = Name
        };
        return JsonSerializer.SerializeToNode(jsonModel);
    }

    public List<T> SortMedia<T>(IEnumerable<T> items, Func<T, MediaModel> selector, MediaFilterModel.OrderMethodEnum orderMethod)
    {
        IEnumerable<T> preSorted = items.OrderBy(x => StringUtils.SmartFileNameKeySelector(selector(x).Title ?? StringResourceProvider.Instance.Untitled), StringUtils.SmartFileNameComparer);
        IVirtualPropertySorter<IMediaModel>? pluginSorter = PluginProperty?.CreateSorter(preSorted.Select(selector));
        IItemSorter<MediaModel> sorter = GetMediaItemSorter(orderMethod, pluginSorter);
        return sorter.Sort(preSorted, selector);
    }

    public List<GroupItem<T>> GroupMedia<T>(
        IEnumerable<T> items,
        Func<T, MediaModel> selector,
        MediaFilterModel.OrderMethodEnum orderMethod,
        MediaFilterModel.FunctionTypeEnum sortingFunction,
        MediaPropertyModel? sortingProperty,
        bool mergeSingleItemGroups)
    {
        IEnumerable<T> preSorted = items.OrderBy(x => StringUtils.SmartFileNameKeySelector(selector(x).Title ?? StringResourceProvider.Instance.Untitled), StringUtils.SmartFileNameComparer);
        IVirtualPropertySorter<IMediaModel>? pluginSorter = PluginProperty?.CreateSorter(preSorted.Select(selector));
        IVirtualPropertySorter<IMediaModel>? sortingPropertyPluginSorter = sortingProperty?.PluginProperty?.CreateSorter(preSorted.Select(selector));
        MediaGrouper grouper = GetMediaGrouper(orderMethod, pluginSorter, sortingFunction, sortingProperty, sortingPropertyPluginSorter);
        return grouper.GroupMedia(preSorted, selector, mergeSingleItemGroups);
    }

    private IItemSorter<MediaModel> GetMediaItemSorter(MediaFilterModel.OrderMethodEnum orderMethod, IVirtualPropertySorter<IMediaModel>? pluginSorter)
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
            if (!media.Tags.TryGetValue(Name, out MediaTagCategory? category))
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

        return Type switch
        {
            PropertyTypeEnum.Title => CreateSorter(x => StringUtils.SmartFileNameKeySelector(x.Title ?? StringResourceProvider.Instance.Untitled), keyComparer: StringUtils.SmartFileNameComparer),
            PropertyTypeEnum.Progress => CreateSorter(x => x.Progress),
            PropertyTypeEnum.Tag => CreateSorter(x => StringUtils.SmartFileNameKeySelector(GetConcatenatedTag(x)), keyComparer: StringUtils.SmartFileNameComparer),
            PropertyTypeEnum.Rating => CreateSorter(x => x.Rating),
            PropertyTypeEnum.CompletionStatus => CreateSorter(x => CompletionStateToComparable(x.CompletionStatus)),
            PropertyTypeEnum.LastVisitTime => CreateSorter(x => x.LastVisit.Ticks),
            PropertyTypeEnum.Duration => CreateSorter(x => x.DurationMs),
            PropertyTypeEnum.PluginVirtualProperty => CreatePluginPropertySorter(),
            _ => CreateFallbackSorter(),
        };
    }

    private MediaGrouper GetMediaGrouper(MediaFilterModel.OrderMethodEnum orderMethod, IVirtualPropertySorter<IMediaModel>? pluginSorter,
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
            if (!media.Tags.TryGetValue(Name, out MediaTagCategory? category))
            {
                string name = StringResourceProvider.Instance.Ungrouped;
                return [name];
            }

            return [.. category.Tags];
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

        string GetLastReadTimeGroupName(MediaModel media)
        {
            DateTimeOffset lastReadTime = media.LastVisit;
            if (lastReadTime == DateTimeOffset.MinValue)
            {
                return StringResourceProvider.Instance.Ungrouped;
            }

            return lastReadTime.ToString("D", EnvironmentProvider.Instance.GetCurrentAppLanguageInfo());
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

            return Type switch
            {
                PropertyTypeEnum.Title => CreateSorter(x => StringUtils.SmartFileNameKeySelector(x.GroupName), keyComparer: StringUtils.SmartFileNameComparer),
                PropertyTypeEnum.Progress => CreateSorter(x => x.Items[0].Progress),
                PropertyTypeEnum.Tag => CreateSorter(x => StringUtils.SmartFileNameKeySelector(x.GroupName), keyComparer: StringUtils.SmartFileNameComparer),
                PropertyTypeEnum.Rating => CreateSorter(x => x.Items[0].Rating),
                PropertyTypeEnum.CompletionStatus => CreateSorter(x => GetCompletionStatusGroupSortingKey(x.Items[0])),
                PropertyTypeEnum.LastVisitTime => CreateSorter(x => x.Items[0].LastVisit.Ticks),
                PropertyTypeEnum.Duration => CreateSorter(x => x.Items[0].DurationMs),
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
            return CreateGrouper(x => [new(StringResourceProvider.Instance.Ungrouped)]);
        }

        MediaGrouper CreatePluginPropertyGrouper()
        {
            if (pluginSorter is null)
            {
                return CreateFallbackGrouper();
            }

            return CreateGrouper(pluginSorter.GetGroupNames);
        }

        return Type switch
        {
            PropertyTypeEnum.Title => CreateGrouper(x => [GetTitleGroupName(x)]),
            PropertyTypeEnum.Progress => CreateGrouper(x => [GetProgressGroupName(x)]),
            PropertyTypeEnum.Tag => CreateGrouper(GetTagGroupNames),
            PropertyTypeEnum.Rating => CreateGrouper(x => [GetRatingGroupName(x)]),
            PropertyTypeEnum.CompletionStatus => CreateGrouper(x => [GetCompletionStatusGroupName(x)]),
            PropertyTypeEnum.LastVisitTime => CreateGrouper(x => [GetLastReadTimeGroupName(x)]),
            PropertyTypeEnum.Duration => CreateGrouper(x => [GetDurationGroupName(x)]),
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

    public static MediaPropertyModel? FromJson(JsonNode? model)
    {
        if (model == null)
        {
            return null;
        }

        JsonModel? jsonModel = null;
        try
        {
            jsonModel = JsonSerializer.Deserialize<JsonModel>(model);
        }
        catch (JsonException e)
        {
            Logger.F(TAG, "FromJson", e);
        }
        if (jsonModel == null)
        {
            return null;
        }

        return new MediaPropertyModel
        {
            Type = StringToPropertyType(jsonModel.Type),
            Name = jsonModel.Name ?? "",
        };
    }

    public static async Task<List<MediaPropertyModel>> GetProperties()
    {
        var properties = new List<MediaPropertyModel>();
        foreach (PropertyTypeEnum propertyType in _properties)
        {
            properties.Add(new MediaPropertyModel
            {
                Type = propertyType,
            });
        }

        {
            List<string> tagCategories = await MediaModel.GetAllTagCategories();
            foreach (string tag in tagCategories)
            {
                properties.Add(new MediaPropertyModel
                {
                    Type = PropertyTypeEnum.Tag,
                    Name = tag,
                });
            }
        }

        IEnumerable<PluginContext> plugins = PluginManager.Instance.GetActivePlugins();
        foreach (PluginContext plugin in plugins)
        {
            string pluginName = plugin.Name;
            IEnumerable<IVirtualProperty<IMediaModel>> pluginProperties = plugin.GetAllMediaVirtualProperties();
            foreach (IVirtualProperty<IMediaModel> property in pluginProperties)
            {
                properties.Add(new MediaPropertyModel
                {
                    Type = PropertyTypeEnum.PluginVirtualProperty,
                    Name = $"{pluginName}:{property.Name}",
                    DisplayName = property.DisplayName,
                });
            }
        }

        return properties;
    }

    private static string PropertyTypeToString(PropertyTypeEnum value)
    {
        return value switch
        {
            PropertyTypeEnum.Title => PROP_TYPE_TITLE,
            PropertyTypeEnum.Progress => PROP_TYPE_PROGRESS,
            PropertyTypeEnum.Tag => PROP_TYPE_TAG,
            PropertyTypeEnum.Rating => PROP_TYPE_RATING,
            PropertyTypeEnum.CompletionStatus => PROP_TYPE_COMPLETION_STATUS,
            PropertyTypeEnum.LastVisitTime => PROP_TYPE_LAST_VISIT_TIME,
            PropertyTypeEnum.Duration => PROP_TYPE_DURATION,
            PropertyTypeEnum.PluginVirtualProperty => PROP_TYPE_PLUGIN_VIRTUAL_PROPERTY,
            _ => PROP_TYPE_TITLE,
        };
    }

    private static PropertyTypeEnum StringToPropertyType(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return PropertyTypeEnum.Title;
        }

        return value switch
        {
            PROP_TYPE_TITLE => PropertyTypeEnum.Title,
            PROP_TYPE_PROGRESS => PropertyTypeEnum.Progress,
            PROP_TYPE_TAG => PropertyTypeEnum.Tag,
            PROP_TYPE_RATING => PropertyTypeEnum.Rating,
            PROP_TYPE_COMPLETION_STATUS => PropertyTypeEnum.CompletionStatus,
            PROP_TYPE_LAST_VISIT_TIME => PropertyTypeEnum.LastVisitTime,
            PROP_TYPE_DURATION => PropertyTypeEnum.Duration,
            PROP_TYPE_PLUGIN_VIRTUAL_PROPERTY => PropertyTypeEnum.PluginVirtualProperty,
            _ => PropertyTypeEnum.Title,
        };
    }

    public class GroupItem<T>(List<T> items, string name)
    {
        public List<T> Items { get; } = items;
        public string Name { get; } = name;
        public string Description { get; set; } = string.Empty;
    }

    private enum PropertyTypeEnum
    {
        CompletionStatus,
        Progress,
        Rating,
        Tag,
        Title,
        LastVisitTime,
        Duration,
        PluginVirtualProperty,
    }

    private class JsonModel
    {
        [JsonPropertyName("Type")]
        public string? Type { get; set; }

        [JsonPropertyName("Name")]
        public string? Name { get; set; }
    }

    private class MediaGroup(string groupName, List<MediaModel> items) : IItemGroup<IMediaModel>
    {
        public string GroupName { get; } = groupName;
        public IReadOnlyList<MediaModel> Items { get; } = items;

        //
        // IItemGroup<IComicModel> implementation
        //

        string IItemGroup<IMediaModel>.Name => GroupName;

        IReadOnlyList<IMediaModel> IItemGroup<IMediaModel>.Items => Items;
    }

    private class MediaGrouper(IGroupSorter<MediaGroup, string> groupSorter, Func<MediaModel, IEnumerable<string>> groupNameSelector)
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

    private interface IItemSorter<K>
    {
        List<T> Sort<T>(IEnumerable<T> items, Func<T, K> selector);
    }

    private interface IGroupSorter<K, M>
    {
        List<T> Sort<T>(IEnumerable<T> items, Func<T, K> selector, Action<T, M> keyBinder);
    }

    private class RandomSorter<K>(MediaFilterModel.OrderMethodEnum orderMethod, Func<K, int> idSelector) : IItemSorter<K>, IGroupSorter<K, string>
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
                        var rng = new Random(AppSettingsModel.GetModel().MediaShuffleRandomSeed);
                        int salt = rng.Next();
                        return [.. items.OrderBy(x => IdSelector(selector(x)) ^ salt)];
                    }
                default:
                    throw new Exception("Invalid order method for RandomSorter");
            }
        }
    }

    private class BasicItemSorter<A, B>(MediaFilterModel.OrderMethodEnum orderMethod, Func<A, B> keySelector, IComparer<B>? keyComparer = null) : IItemSorter<A>
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

    private class BasicGroupSorter<A, B>(MediaFilterModel.OrderMethodEnum orderMethod, Func<A, B> keySelector, Func<B, string>? keyInfoConverter = null, IComparer<B>? keyComparer = null) : IGroupSorter<A, string>
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

    private class PluginPropertyItemSorter(IVirtualPropertySorter<IMediaModel> sorter, MediaFilterModel.OrderMethodEnum orderMethod) : IItemSorter<MediaModel>
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

    private class PluginPropertyGroupSorter(IVirtualPropertySorter<IMediaModel> sorter, MediaFilterModel.OrderMethodEnum orderMethod) : IGroupSorter<MediaGroup, string>
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
}
