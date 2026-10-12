// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using SinePlayerUWP.Common.Actions;
using SinePlayerUWP.Common.Localization;
using SinePlayerUWP.Core.Common.DebugTools;
using SinePlayerUWP.Core.Common.Lifecycle;
using SinePlayerUWP.Core.Common.Threading;
using SinePlayerUWP.Core.Common.Utils;
using SinePlayerUWP.Data.Models.Media;
using SinePlayerUWP.Data.Models.Misc;
using SinePlayerUWP.Helpers.MenuFlyoutHelpers;
using SinePlayerUWP.Helpers.Misc;
using SinePlayerUWP.Helpers.Navigation;
using SinePlayerUWP.Helpers.Search;
using SinePlayerUWP.UserControls.MediaSelection;
using SinePlayerUWP.ViewModels;

namespace SinePlayerUWP.Views.Pages.Home;

internal partial class HomePageViewModel : INotifyPropertyChanged
{
    private const string TAG = nameof(HomePageViewModel);

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _libraryEmptyVisible = false;
    public bool LibraryEmptyVisible
    {
        get => _libraryEmptyVisible;
        set
        {
            _libraryEmptyVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LibraryEmptyVisible)));
        }
    }

    public readonly MutableLiveData<string> UrlLiveData = new();
    public readonly MutableLiveData<FilterModel> FilterLiveData = new();
    public readonly MutableLiveData<bool> GroupingEnabledLiveData = new();
    public readonly MutableLiveData<MediaFilterModel.ViewTypeEnum> ViewTypeLiveData = new();

    public List<MediaItemViewModel> UngroupedMediaItems { get; private set; } = [];
    public List<MediaGroupViewModel> GroupedMediaItems { get; private set; } = [];
    public MediaSelectionViewModel MediaSelection { get; }

    private ActionHandler _actionHandler = ActionHandler.Dummy;
    private readonly MediaSearchEngine _searchEngine = new();
    private MediaFilterModel.ExternalModel? _filterSettingsModel;
    private MediaFilterModel.ExternalFilterModel? _filterModel;
    private IReadOnlyList<MediaModel> _mediaItems = [];
    private long _lastSearchTime = 0;

    private readonly ITaskDispatcher _sharedDispatcher = TaskDispatcher.Factory.NewQueue("HomePageQueue");
    private bool _filterInvalidated = true;
    private bool _mediaInvalidated = true;
    private int _updateFilterSubmitted = 0;
    private int _updateMediaSubmitted = 0;

    public HomePageViewModel()
    {
        MediaSelection = new(() => _mediaItems.Count);
    }

    public void Initialize(ActionHandler actionHandler, string? filterJson)
    {
        _actionHandler = actionHandler;
        _searchEngine.ExcludeInCollectionMedia = true;
        _searchEngine.SetResultCallback(OnMediaSearchResult);
        _filterSettingsModel = MediaFilterModel.Instance.GetModel() ?? new();

        MediaFilterModel.FilterModel? jsonModel = null;
        if (!string.IsNullOrEmpty(filterJson))
        {
            try
            {
                jsonModel = JsonSerializer.Deserialize<MediaFilterModel.FilterModel>(filterJson);
            }
            catch (JsonException ex)
            {
                Logger.E(TAG, ex);
            }
        }

        if (jsonModel is not null)
        {
            _filterModel = MediaFilterModel.ExternalFilterModel.From(jsonModel);
        }

        Refresh(filters: true, library: true);
    }

    public void Refresh(bool clearFilter = false, bool filters = false, bool library = false)
    {
        if (clearFilter)
        {
            _filterModel = null;
            _filterInvalidated = true;
        }

        if (filters)
        {
            _filterInvalidated = true;
        }

        if (library)
        {
            _mediaInvalidated = true;
        }

        if (_filterInvalidated)
        {
            ScheduleUpdateFilters(true);
            return;
        }

        if (_mediaInvalidated)
        {
            ScheduleUpdateMedia();
            return;
        }
    }

    public void SetSearchText(string searchText)
    {
        searchText = searchText.Trim();
        if (searchText == _searchEngine.SearchText)
        {
            return;
        }

        _searchEngine.SearchText = searchText;

        long tick = GetTick();
        int timeRemain = 200 - (int)(tick - _lastSearchTime);
        if (timeRemain <= 0)
        {
            _lastSearchTime = tick;
            ScheduleUpdateMedia();
        }
        else
        {
            CoroutineUtils.Run(async () =>
            {
                await Task.Delay(timeRemain);
                _lastSearchTime = GetTick();
                ScheduleUpdateMedia();
            });
        }
    }

    public Task<MediaFilterModel.ExternalFilterModel> GetFilter()
    {
        return _sharedDispatcher.Submit(() =>
        {
            return _filterModel?.Clone() ?? MediaFilterModel.ExternalFilterModel.FromDefault();
        });
    }

    public IReadOnlyList<MediaModel> GetMedia()
    {
        return _mediaItems;
    }

    public void CollapseOrExpandGroup(MediaGroupViewModel groupModel)
    {
        bool collapsed = !groupModel.Collapsed;
        groupModel.Collapsed = collapsed;

        MediaFilterModel.ExternalFilterModel? filter = _filterModel;
        if (filter is not null)
        {
            var collapsedGroups = filter.CollapsedGroups.ToHashSet();
            if (collapsed)
            {
                collapsedGroups.Add(groupModel.GroupName);
            }
            else
            {
                collapsedGroups.Remove(groupModel.GroupName);
            }

            filter.CollapsedGroups = collapsedGroups;
            filter.Modified |= filter.SaveSortingAndGroupingSettings;
            UpdateLastFilter(filter);
        }
    }

    public void CollapseAllGroups()
    {
        MediaFilterModel.ExternalFilterModel? filter = _filterModel;
        HashSet<string> collapsedGroups = filter?.CollapsedGroups.ToHashSet() ?? [];
        bool changed = false;
        foreach (MediaGroupViewModel group in GroupedMediaItems)
        {
            changed |= collapsedGroups.Add(group.GroupName);
        }

        if (!changed)
        {
            return;
        }

        if (filter is not null)
        {
            filter.CollapsedGroups = collapsedGroups;
            filter.Modified |= filter.SaveSortingAndGroupingSettings;
            UpdateLastFilter(filter);
        }

        ScheduleDisplayMedia();
    }

    public void ExpandAllGroups()
    {
        MediaFilterModel.ExternalFilterModel? filter = _filterModel;
        HashSet<string> collapsedGroups = filter?.CollapsedGroups.ToHashSet() ?? [];
        bool changed = false;
        foreach (MediaGroupViewModel group in GroupedMediaItems)
        {
            changed |= collapsedGroups.Remove(group.GroupName);
        }

        if (!changed)
        {
            return;
        }

        if (filter is not null)
        {
            filter.CollapsedGroups = collapsedGroups;
            filter.Modified |= filter.SaveSortingAndGroupingSettings;
            UpdateLastFilter(filter);
        }

        ScheduleDisplayMedia();
    }

    //
    // Filters
    //

    public void SelectViewType(MediaFilterModel.ViewTypeEnum viewType)
    {
        _sharedDispatcher.Submit(() =>
        {
            MediaFilterModel.ExternalFilterModel filter = _filterModel ?? MediaFilterModel.ExternalFilterModel.FromDefault();
            if (filter.ViewType != viewType)
            {
                filter.ViewType = viewType;
                filter.Modified |= filter.SaveViewSettings;
            }

            UpdateLastFilter(filter);
            ScheduleUpdateFilters(false);
        });
    }

    private void SelectSortOrGroup(Func<MediaFilterModel.ExternalFilterModel, bool> handler)
    {
        _sharedDispatcher.Submit(() =>
        {
            MediaFilterModel.ExternalFilterModel filter = _filterModel ?? MediaFilterModel.ExternalFilterModel.FromDefault();
            bool changed = handler(filter);
            filter.Modified |= changed && filter.SaveSortingAndGroupingSettings;
            UpdateLastFilter(filter);
            ScheduleUpdateFilters(false);
        });
    }

    private void UpdateLastFilter(MediaFilterModel.ExternalFilterModel filter)
    {
        MediaFilterModel.ExternalModel? filterSettings = _filterSettingsModel;
        if (filterSettings is not null)
        {
            filterSettings.LastFilter = filter.Clone();
            MediaFilterModel.Instance.UpdateModel(filterSettings);
        }

        UpdateUrl();
    }

    private void SelectFilterPreset(string? name)
    {
        name ??= "";
        _sharedDispatcher.Submit(() =>
        {
            MediaFilterModel.ExternalModel? filterSettings = _filterSettingsModel;
            if (filterSettings is null)
            {
                return;
            }

            MediaFilterModel.ExternalFilterModel? filter = MergeFilterFromDatabase(name, filterSettings);
            if (filter is null)
            {
                return;
            }

            filterSettings.LastFilter = filter;
            MediaFilterModel.Instance.UpdateModel(filterSettings);
            _filterModel = null;
            ScheduleUpdateFilters(false);
        });
    }

    private MediaFilterModel.ExternalFilterModel? MergeFilterFromDatabase(string name, MediaFilterModel.ExternalModel filterSettings)
    {
        MediaFilterModel.ExternalFilterModel? filter = filterSettings.Filters.Find(x => x.Name == name);
        if (filter is null)
        {
            return null;
        }

        filter = filter.Clone();
        MediaFilterModel.ExternalFilterModel? lastFilter = _filterModel;
        if (lastFilter is not null)
        {
            if (!filter.SaveViewSettings)
            {
                filter.ViewType = lastFilter.ViewType;
            }

            if (!filter.SaveSortingAndGroupingSettings)
            {
                filter.SortBy = lastFilter.SortBy;
                filter.MediaOrderMethod = lastFilter.MediaOrderMethod;
                filter.GroupBy = lastFilter.GroupBy;
                filter.GroupOrderMethod = lastFilter.GroupOrderMethod;
                filter.GroupSortingFunction = lastFilter.GroupSortingFunction;
                filter.GroupSortingProperty = lastFilter.GroupSortingProperty;
                filter.CollapsedGroups = lastFilter.CollapsedGroups;
                filter.MergeSingleItemGroups = lastFilter.MergeSingleItemGroups;
            }
        }

        filter.Modified = false;
        return filter;
    }

    //
    // Task Scheduler
    //

    private void ScheduleUpdateFilters(bool reloadFromDatabase)
    {
        if (Interlocked.CompareExchange(ref _updateFilterSubmitted, 1, 0) == 1)
        {
            return;
        }

        _filterInvalidated = false;
        _sharedDispatcher.SubmitAsync(async () =>
        {
            Interlocked.Exchange(ref _updateFilterSubmitted, 0);
            await UpdateFiltersNoLock(reloadFromDatabase);
        });
    }

    private void ScheduleUpdateMedia()
    {
        if (_filterInvalidated)
        {
            ScheduleUpdateFilters(true);
            return;
        }

        _mediaInvalidated = false;
        _searchEngine.Update();
    }

    private void ScheduleDisplayMedia()
    {
        if (_filterInvalidated)
        {
            ScheduleUpdateFilters(true);
            return;
        }

        if (_mediaInvalidated)
        {
            ScheduleUpdateMedia();
            return;
        }

        if (Interlocked.CompareExchange(ref _updateMediaSubmitted, 1, 0) == 1)
        {
            return;
        }

        _sharedDispatcher.SubmitAsync(async () =>
        {
            Interlocked.Exchange(ref _updateMediaSubmitted, 0);
            await DisplayMediaNoLock();
        });
    }

    //
    // Unsorted
    //

    private void OnMediaSearchResult(IReadOnlyList<MediaModel> mediaItems)
    {
        _mediaItems = mediaItems;
        ScheduleDisplayMedia();
    }

    private async Task UpdateFiltersNoLock(bool reloadFromDatabase)
    {
        Logger.I(TAG, "UpdateFiltersNoLock");

        // Validate and save filter settings
        List<MediaFilterModel.ExternalFilterModel> filters;
        MediaFilterModel.ExternalFilterModel? filter = _filterModel;
        {
            bool settingsChanged = false;
            MediaFilterModel.ExternalModel? filterSettings = _filterSettingsModel;
            if (reloadFromDatabase || filterSettings is null)
            {
                filterSettings = MediaFilterModel.Instance.GetModel() ?? new();
                _filterSettingsModel = filterSettings;
            }

            filters = filterSettings.Filters;
            if (filters == null || filters.Count == 0)
            {
                filters = [MediaFilterModel.ExternalFilterModel.FromDefault()];
                filterSettings.Filters = filters;
                settingsChanged = true;
            }

            filters.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

            MediaFilterModel.ExternalFilterModel? lastFilter = filterSettings.LastFilter;
            if (lastFilter is null)
            {
                lastFilter = filters[0].Clone();
                filterSettings.LastFilter = lastFilter;
                settingsChanged = true;
            }

            if (settingsChanged)
            {
                MediaFilterModel.Instance.UpdateModel(filterSettings);
            }

            if (filter is not null && reloadFromDatabase && !filter.Modified)
            {
                filter = MergeFilterFromDatabase(filter.Name, filterSettings);
                _filterModel = filter;
            }

            if (filter is null)
            {
                filter = lastFilter;
                _filterModel = lastFilter;
                UpdateUrl();
            }
        }

        // Update UI
        {
            List<MediaPropertyModel> properties = await MediaPropertyModel.GetProperties();
            var sortByDropDown = new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Sort,
                Items = CreateSortByMenuItems(properties, filter.SortBy, filter.MediaOrderMethod),
            };
            var groupByDropDown = new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Group,
                Items = CreateGroupByMenuItems(properties, filter),
            };
            var sortAndGroupDropDown = new DropDownButtonModel
            {
                Name = StringResourceProvider.Instance.Sort + " & " + StringResourceProvider.Instance.Group,
                Items = [sortByDropDown, groupByDropDown],
            };

            string lastFilterName = filter.Name ?? "";
            if (filter.Modified)
            {
                lastFilterName += " *";
            }

            var filterPresetDropDown = new DropDownButtonModel
            {
                Name = lastFilterName,
                Items = filters.ConvertAll(x => new SimpleMenuFlyoutItemModel()
                {
                    Text = x.Name,
                    Click = () => SelectFilterPreset(x.Name),
                }),
            };

            var uiModel = new FilterModel
            {
                SortAndGroupDropDown = sortAndGroupDropDown,
                FilterPresetDropDown = filterPresetDropDown,
            };
            FilterLiveData.Emit(uiModel);
            ViewTypeLiveData.Emit(filter.ViewType);
        }

        // Update mediaItems
        if (_searchEngine.Expression == filter.Expression && _searchEngine.IncludeHidden == filter.IncludeHiddenItems)
        {
            ScheduleDisplayMedia();
        }
        else
        {
            _searchEngine.Expression = filter.Expression;
            _searchEngine.IncludeHidden = filter.IncludeHiddenItems;
            ScheduleUpdateMedia();
        }
    }

    private async Task DisplayMediaNoLock()
    {
        Logger.I(TAG, "DisplayMediaNoLock");

        MediaItemViewModel MediaToViewModel(MediaModel media, PlaylistModel.Builder playlist)
        {
            var item = new MediaItemViewModel(media)
            {
                OnClick = model =>
                {
                    if (MediaSelection.IsSelectMode)
                    {
                        return;
                    }

                    Route route = OpenMediaHelper.GetMediaRoute(media, playlist: playlist);
                    OpenMediaHelper.OpenMedia(_actionHandler, route);
                },
                OnRequestContextFlyoutAsync = model =>
                {
                    IReadOnlyList<MediaModel>? selectedMedia = MediaSelection.IsSelectMode ? MediaSelection.GetSelectedMedia() : null;
                    return MenuFlyoutItemsCreator.CreateMediaMenuItems(
                        _actionHandler,
                        media,
                        playlist: playlist,
                        selectedItems: selectedMedia,
                        canSelect: true);
                },
            };
            item.UpdateProgress(true);
            return item;
        }

        IReadOnlyList<MediaModel> mediaItems = _mediaItems;
        bool isEmpty = mediaItems.Count == 0;
        List<MediaItemViewModel>? ungroupedMedia = null;
        List<MediaGroupViewModel>? groupedMedia = null;

        if (isEmpty)
        {
            ungroupedMedia = [];
        }
        else
        {
            MediaFilterModel.ExternalFilterModel filter = _filterModel ?? MediaFilterModel.ExternalFilterModel.FromDefault();
            MediaPropertyModel sortBy = filter.SortBy;
            MediaPropertyModel? groupBy = filter.GroupBy;

            if (groupBy is not null)
            {
                List<MediaPropertyModel.GroupItem<MediaModel>> groups = groupBy.GroupMedia(mediaItems, x => x,
                    filter.GroupOrderMethod,
                    filter.GroupSortingFunction,
                    filter.GroupSortingProperty,
                    filter.MergeSingleItemGroups);
                var playlist = new PlaylistModel.Builder();
                groupedMedia = [];
                foreach (MediaPropertyModel.GroupItem<MediaModel> group in groups)
                {
                    List<MediaModel> sorted = SortMediaByProerty(group.Items, sortBy, filter.MediaOrderMethod);
                    playlist.AddMedia(sorted);
                    List<MediaItemViewModel> items = [.. sorted.Select(x => MediaToViewModel(x, playlist))];
                    var groupViewModel = new MediaGroupViewModel(group.Name, items, filter.CollapsedGroups.Contains(group.Name))
                    {
                        Description = group.Description,
                    };
                    groupedMedia.Add(groupViewModel);
                }
            }
            else
            {
                List<MediaModel> sortedMedia = SortMediaByProerty(mediaItems, sortBy, filter.MediaOrderMethod);
                PlaylistModel.Builder playlist = new PlaylistModel.Builder().AddMedia(sortedMedia);
                ungroupedMedia = [.. sortedMedia.Select(x => MediaToViewModel(x, playlist))];
            }
        }

        await MainThreadUtils.RunInMainThread(() =>
        {
            if (groupedMedia != null)
            {
                GroupedMediaItems = groupedMedia;
                UngroupedMediaItems = [];
                GroupingEnabledLiveData.Emit(true);
                LibraryEmptyVisible = GroupedMediaItems.Count == 0;
            }
            else if (ungroupedMedia != null)
            {
                UngroupedMediaItems = ungroupedMedia;
                GroupedMediaItems = [];
                GroupingEnabledLiveData.Emit(false);
                LibraryEmptyVisible = UngroupedMediaItems.Count == 0;
            }
            else
            {
                Logger.F(TAG, "Shouldn't reach");
            }
        });
    }

    private void UpdateUrl()
    {
        MediaFilterModel.ExternalFilterModel? filter = _filterModel;
        if (filter is null)
        {
            return;
        }

        MediaFilterModel.FilterModel jsonModel = filter.To();
        string json = JsonSerializer.Serialize(jsonModel);
        Route route = Route.Create(RouterConstants.SCHEME_APP + RouterConstants.HOST_HOME)
            .WithParam(RouterConstants.ARG_FILTER_JSON, json);
        UrlLiveData.Emit(route.Url);
    }

    private static List<MediaModel> SortMediaByProerty(IReadOnlyList<MediaModel> mediaItems,
        MediaPropertyModel property, MediaFilterModel.OrderMethodEnum orderMethod)
    {
        return property.SortMedia(mediaItems, x => x, orderMethod);
    }

    private List<BaseMenuFlyoutItemModel> CreateSortByMenuItems(List<MediaPropertyModel> properties,
        MediaPropertyModel? selectedProperty, MediaFilterModel.OrderMethodEnum selectedOrderMethod)
    {
        List<BaseMenuFlyoutItemModel> items = [];

        items.AddRange(CreateSortByPropertyMenuItems(properties, selectedProperty, p =>
        {
            SelectSortOrGroup(filter =>
            {
                if (!p.Equals(filter.SortBy))
                {
                    filter.SortBy = p;
                    return true;
                }

                return false;
            });
        }));

        if (selectedProperty != null)
        {
            items.Add(new SeparatorMenuFlyoutItemModel());
            items.AddRange(CreateOrderMethodMenuItems(selectedOrderMethod, orderMethod =>
            {
                SelectSortOrGroup(filter =>
                {
                    if (orderMethod != filter.MediaOrderMethod)
                    {
                        filter.MediaOrderMethod = orderMethod;
                        return true;
                    }

                    return false;
                });
            }));
        }

        return items;
    }

    private List<BaseMenuFlyoutItemModel> CreateGroupByMenuItems(List<MediaPropertyModel> properties, MediaFilterModel.ExternalFilterModel filter)
    {
        MediaPropertyModel? selectedProperty = filter.GroupBy;

        List<BaseMenuFlyoutItemModel> items = [];

        items.Add(new ToggleMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.None,
            IsChecked = selectedProperty is null,
            Click = () =>
            {
                SelectSortOrGroup(filter =>
                {
                    if (filter.GroupBy is not null)
                    {
                        filter.GroupBy = null;
                        return true;
                    }

                    return false;
                });
            }
        });

        items.AddRange(CreateSortByPropertyMenuItems(properties, selectedProperty, p =>
        {
            SelectSortOrGroup(filter =>
            {
                if (!p.Equals(filter.GroupBy))
                {
                    filter.GroupBy = p;
                    return true;
                }

                return false;
            });
        }));

        if (selectedProperty != null)
        {
            items.Add(new SeparatorMenuFlyoutItemModel());

            items.AddRange(CreateOrderMethodMenuItems(filter.GroupOrderMethod, orderMethod =>
            {
                SelectSortOrGroup(filter =>
                {
                    if (orderMethod != filter.GroupOrderMethod)
                    {
                        filter.GroupOrderMethod = orderMethod;
                        return true;
                    }

                    return false;
                });
            }));

            items.Add(new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.SortingFunction,
                Items = CreateSortingFunctionMenuItems(properties, filter.GroupSortingFunction, filter.GroupSortingProperty, (f, p) =>
                {
                    SelectSortOrGroup(filter =>
                    {
                        bool modified = false;

                        if (filter.GroupSortingFunction != f)
                        {
                            modified = true;
                            filter.GroupSortingFunction = f;
                        }

                        if (p is not null && !p.Equals(filter.GroupSortingProperty))
                        {
                            modified = true;
                            filter.GroupSortingProperty = p;
                        }

                        return modified;
                    });
                }),
            });

            items.Add(new SeparatorMenuFlyoutItemModel());

            items.Add(new ToggleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.MergeSingleItemGroups,
                IsChecked = filter.MergeSingleItemGroups,
                Click = () =>
                {
                    SelectSortOrGroup(filter =>
                    {
                        filter.MergeSingleItemGroups = !filter.MergeSingleItemGroups;
                        return true;
                    });
                },
            });
        }

        return items;
    }

    private static List<BaseMenuFlyoutItemModel> CreateOrderMethodMenuItems(MediaFilterModel.OrderMethodEnum selectedMethod, Action<MediaFilterModel.OrderMethodEnum> clickHandler)
    {
        string GetOrderMethodDisplayName(MediaFilterModel.OrderMethodEnum method)
        {
            return method switch
            {
                MediaFilterModel.OrderMethodEnum.Ascending => StringResourceProvider.Instance.Ascending,
                MediaFilterModel.OrderMethodEnum.Descending => StringResourceProvider.Instance.Descending,
                MediaFilterModel.OrderMethodEnum.Shuffle => StringResourceProvider.Instance.Shuffle,
                MediaFilterModel.OrderMethodEnum.ShuffleStable => StringResourceProvider.Instance.ShuffleStable,
                _ => "???"
            };
        }

        List<MediaFilterModel.OrderMethodEnum> orderMethods = [
            MediaFilterModel.OrderMethodEnum.Ascending,
            MediaFilterModel.OrderMethodEnum.Descending,
            MediaFilterModel.OrderMethodEnum.Shuffle,
            MediaFilterModel.OrderMethodEnum.ShuffleStable,
        ];

        List<BaseMenuFlyoutItemModel> items = [];
        foreach (MediaFilterModel.OrderMethodEnum orderMethod in orderMethods)
        {
            items.Add(new ToggleMenuFlyoutItemModel()
            {
                Text = GetOrderMethodDisplayName(orderMethod),
                IsChecked = orderMethod == selectedMethod,
                Click = () =>
                {
                    clickHandler(orderMethod);
                },
            });
        }

        return items;
    }

    private static List<BaseMenuFlyoutItemModel> CreateSortingFunctionMenuItems(List<MediaPropertyModel> properties,
        MediaFilterModel.FunctionTypeEnum sortingFunction, MediaPropertyModel? sortingProperty,
        Action<MediaFilterModel.FunctionTypeEnum, MediaPropertyModel?> clickHandler)
    {
        string GetFunctionDisplayName(MediaFilterModel.FunctionTypeEnum function)
        {
            return function switch
            {
                MediaFilterModel.FunctionTypeEnum.None => StringResourceProvider.Instance.None,
                MediaFilterModel.FunctionTypeEnum.ItemCount => StringResourceProvider.Instance.FunctionItemCount,
                MediaFilterModel.FunctionTypeEnum.Average => StringResourceProvider.Instance.FunctionAverage,
                MediaFilterModel.FunctionTypeEnum.Sum => StringResourceProvider.Instance.FunctionSum,
                MediaFilterModel.FunctionTypeEnum.Max => StringResourceProvider.Instance.FunctionMax,
                MediaFilterModel.FunctionTypeEnum.Min => StringResourceProvider.Instance.FunctionMin,
                _ => "Unknown function"
            };
        }

        List<BaseMenuFlyoutItemModel> items = [];
        List<MediaFilterModel.FunctionTypeEnum> simpleFunctions = [
            MediaFilterModel.FunctionTypeEnum.None,
            MediaFilterModel.FunctionTypeEnum.ItemCount,
        ];
        List<MediaFilterModel.FunctionTypeEnum> propertyFunctions = [
            MediaFilterModel.FunctionTypeEnum.Average,
            MediaFilterModel.FunctionTypeEnum.Sum,
            MediaFilterModel.FunctionTypeEnum.Max,
            MediaFilterModel.FunctionTypeEnum.Min,
        ];

        foreach (MediaFilterModel.FunctionTypeEnum function in simpleFunctions)
        {
            items.Add(new ToggleMenuFlyoutItemModel()
            {
                Text = GetFunctionDisplayName(function),
                IsChecked = function == sortingFunction,
                Click = () =>
                {
                    clickHandler(function, null);
                }
            });
        }

        foreach (MediaFilterModel.FunctionTypeEnum function in propertyFunctions)
        {
            IEnumerable<BaseMenuFlyoutItemModel> subItems = CreateSortByPropertyMenuItems(properties, function == sortingFunction ? sortingProperty : null, p =>
            {
                clickHandler(function, p);
            });
            items.Add(new SubItemMenuFlyoutItemModel()
            {
                Text = GetFunctionDisplayName(function),
                Items = subItems,
            });
        }

        return items;
    }

    private static IEnumerable<BaseMenuFlyoutItemModel> CreateSortByPropertyMenuItems(List<MediaPropertyModel> properties,
        MediaPropertyModel? selectedProperty, Action<MediaPropertyModel> clickHandler)
    {
        Dictionary<string, List<MediaPropertyModel>> propertyGroupMap = [];
        foreach (MediaPropertyModel property in properties)
        {
            string groupName = property.DisplayGroupName;
            if (!propertyGroupMap.TryGetValue(groupName, out List<MediaPropertyModel>? value))
            {
                value = [];
                propertyGroupMap[groupName] = value;
            }

            value.Add(property);
        }

        List<MediaPropertyModel>? plainProperties = null;
        List<KeyValuePair<string, List<MediaPropertyModel>>> propertyGroupList = [];
        foreach (KeyValuePair<string, List<MediaPropertyModel>> kvp in propertyGroupMap)
        {
            if (kvp.Key == string.Empty)
            {
                plainProperties = kvp.Value;
                continue;
            }

            propertyGroupList.Add(kvp);
            kvp.Value.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName));
        }

        List<Tuple<string, BaseMenuFlyoutItemModel>> items = [];
        if (plainProperties is not null)
        {
            foreach (MediaPropertyModel p in plainProperties)
            {
                items.Add(new(p.DisplayName, new ToggleMenuFlyoutItemModel()
                {
                    Text = p.DisplayName,
                    IsChecked = p.Equals(selectedProperty),
                    Click = () =>
                    {
                        clickHandler(p);
                    }
                }));
            }
        }

        foreach (KeyValuePair<string, List<MediaPropertyModel>> kvp in propertyGroupList)
        {
            List<BaseMenuFlyoutItemModel> subItems = [];
            foreach (MediaPropertyModel p in kvp.Value)
            {
                subItems.Add(new ToggleMenuFlyoutItemModel()
                {
                    Text = p.DisplayName,
                    IsChecked = p.Equals(selectedProperty),
                    Click = () =>
                    {
                        clickHandler(p);
                    }
                });
            }

            items.Add(new(kvp.Key, new SubItemMenuFlyoutItemModel()
            {
                Text = kvp.Key,
                Items = subItems,
            }));
        }

        items.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.InvariantCultureIgnoreCase));
        return items.Select(x => x.Item2);
    }

    private static long GetTick()
    {
        return Environment.TickCount64;
    }

    public class FilterModel
    {
        public DropDownButtonModel SortAndGroupDropDown { get; set; } = new();
        public DropDownButtonModel FilterPresetDropDown { get; set; } = new();
    }

    public class DropDownButtonModel
    {
        public string Name { get; set; } = "";
        public IEnumerable<BaseMenuFlyoutItemModel> Items { get; set; } = [];
    }
}
