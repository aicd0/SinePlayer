// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using SinePlayer.Common.Actions;
using SinePlayer.Common.Constants;
using SinePlayer.Core.Common.Algorithm;
using SinePlayer.Core.Common.Lifecycle;
using SinePlayer.Core.Common.Threading;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Database;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Helpers.MenuFlyoutHelpers;
using SinePlayer.Helpers.Misc;
using SinePlayer.Helpers.Navigation;
using SinePlayer.Helpers.Search;
using SinePlayer.ViewModels;

namespace SinePlayer.Views.Pages.Sidebar.FilterPresets;

internal partial class FilterPresetsPageViewModel : INotifyPropertyChanged
{
    private const int SEARCH_DELAY = 200;

    public event PropertyChangedEventHandler? PropertyChanged;

    public readonly MutableLiveData<DropDownButtonModel> FilterPresetDropDownLiveData = new();

    public ObservableCollection<SimpleTreeViewNodeModel> DataSource { get; set; } = [];

    public bool _selectionMode = false;
    public bool SelectionMode
    {
        get => _selectionMode;
        set
        {
            _selectionMode = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionMode)));
        }
    }

    private bool _noMediaVisible = false;
    public bool NoMediaVisible
    {
        get => _noMediaVisible;
        set
        {
            if (_noMediaVisible != value)
            {
                _noMediaVisible = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NoMediaVisible)));
            }
        }
    }

    private ActionHandler _actionHandler = ActionHandler.Dummy;
    private readonly MediaSearchEngine _searchEngine = new();
    private MediaFilterModel.ExternalFilterModel? _selectedFilter;
    private bool _searchSubmitted = false;

    private readonly ITaskDispatcher _sharedDispatcher = TaskDispatcher.Factory.NewQueue("FilterPresetsPageQueue");
    private int _updateMediaSubmitted = 0;

    public void Initialize(ActionHandler actionHandler)
    {
        _actionHandler = actionHandler;
        _searchEngine.ExcludeInCollectionMedia = true;
        _searchEngine.SetResultCallback(OnMediaSearchResult);
    }

    public void UpdateMedia()
    {
        ScheduleUpdateMedia();
    }

    public List<MediaModel> GetMediaSnapshot()
    {
        List<MediaModel> mediaItems = [];
        foreach (SimpleTreeViewNodeModel node in DataSource)
        {
            foreach (MediaModel media in node.CollectDataContext<MediaModel>())
            {
                mediaItems.Add(media);
            }
        }

        return mediaItems;
    }

    public void SetSearchText(string searchText)
    {
        searchText = searchText.Trim();
        if (searchText == _searchEngine.SearchText)
        {
            return;
        }

        _searchEngine.SearchText = searchText;
        if (_searchSubmitted)
        {
            return;
        }

        _searchSubmitted = true;
        CoroutineUtils.Run(async () =>
        {
            await Task.Delay(SEARCH_DELAY);
            _searchSubmitted = false;
            _searchEngine.Update();
        });
    }

    public void ExpandAllGroups()
    {
        foreach (SimpleTreeViewNodeModel node in DataSource)
        {
            node.ExpandAll();
        }
    }

    public void CollapseAllGroups()
    {
        foreach (SimpleTreeViewNodeModel node in DataSource)
        {
            node.CollapseAll();
        }
    }

    private void SetFilter(MediaFilterModel.ExternalFilterModel filter)
    {
        if (_selectedFilter is not null && _selectedFilter.Name == filter.Name)
        {
            return;
        }

        _selectedFilter = filter;
        ScheduleUpdateMedia();
    }

    private void ScheduleUpdateMedia()
    {
        if (Interlocked.CompareExchange(ref _updateMediaSubmitted, 1, 0) == 1)
        {
            return;
        }

        _sharedDispatcher.Submit(() =>
        {
            Interlocked.Exchange(ref _updateMediaSubmitted, 0);
            UpdateMediaNoLock();
        });
    }

    private void UpdateMediaNoLock()
    {
        MediaFilterModel.ExternalModel filterModel = MediaFilterModel.Instance.GetModel() ?? new();
        List<MediaFilterModel.ExternalFilterModel> filters = filterModel.Filters;
        if (filters == null || filters.Count == 0)
        {
            filters = [MediaFilterModel.ExternalFilterModel.FromDefault()];
            filterModel.Filters = filters;
        }

        filters.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        MediaFilterModel.ExternalFilterModel? selectedFilter = null;
        if (_selectedFilter is not null)
        {
            selectedFilter = filters.Find(x => x.Name == _selectedFilter.Name);
        }

        if (selectedFilter is null)
        {
            string? lastFilterName = AppDB.MainRegistry.CreateKey(RegistryNames.STATE).GetValue<string>(RegistryNames.StateKey.SIDE_PANE_LAST_FILTER_PRESET);
            if (!string.IsNullOrEmpty(lastFilterName))
            {
                selectedFilter = filters.Find(x => x.Name == lastFilterName);
            }
        }

        selectedFilter ??= filters[0];
        _selectedFilter = selectedFilter;
        AppDB.MainRegistry.CreateKey(RegistryNames.STATE).Set(RegistryNames.StateKey.SIDE_PANE_LAST_FILTER_PRESET, selectedFilter.Name);

        DropDownButtonModel filterPresetDropdown = new()
        {
            Name = selectedFilter.Name,
            Items = filters.ConvertAll(x => new ToggleMenuFlyoutItemModel()
            {
                Text = x.Name,
                IsChecked = x.Name == selectedFilter.Name,
                Click = () =>
                {
                    SetFilter(x);
                }
            })
        };
        FilterPresetDropDownLiveData.Emit(filterPresetDropdown);

        _searchEngine.Expression = selectedFilter.Expression;
        _searchEngine.IncludeHidden = selectedFilter.IncludeHiddenItems;
        _searchEngine.Update();
    }

    private void OnMediaSearchResult(IReadOnlyList<MediaModel> items)
    {
        _sharedDispatcher.Submit(() =>
        {
            MediaFilterModel.ExternalFilterModel filter = _selectedFilter
                ?? MediaFilterModel.ExternalFilterModel.FromDefault();
            MediaPropertyModel sortBy = filter.SortBy;
            MediaPropertyModel? groupBy = filter.GroupBy;

            List<SimpleTreeViewNodeModel> dataSource = [];
            SimpleTreeViewNodeModel MediaToNode(MediaModel media, PlaylistModel.Builder playlist)
            {
                return new()
                {
                    DataContext = media,
                    Glyph = "\uE8B9",
                    Title = media.Title,
                    CanExpand = false,
                    Clicked = item =>
                    {
                        Route route = OpenMediaHelper.GetMediaRoute(media, playlist: playlist);
                        OpenMediaHelper.OpenMedia(_actionHandler, route);
                    },
                    RequestContextMenuItemsAsync = (primary, selection) =>
                    {
                        IEnumerable<MediaModel>? selectedMedia = !_selectionMode ? null : selection
                            .Where(x => x.DataContext is MediaModel)
                            .Select(x => (MediaModel)x.DataContext!);
                        return MenuFlyoutItemsCreator.CreateMediaMenuItems(
                            _actionHandler,
                            media,
                            playlist: playlist,
                            selectedItems: selectedMedia,
                            canSelect: !SelectionMode);
                    },
                };
            }

            if (groupBy != null)
            {
                List<MediaPropertyModel.GroupItem<MediaModel>> groupItems = groupBy.GroupMedia(items, x => x,
                    filter.GroupOrderMethod,
                    filter.GroupSortingFunction,
                    filter.GroupSortingProperty,
                    filter.MergeSingleItemGroups);
                foreach (MediaPropertyModel.GroupItem<MediaModel> item in groupItems)
                {
                    SimpleTreeViewNodeModel groupNode = new()
                    {
                        Title = item.Name,
                        CanExpand = true,
                        IsExpanded = false,
                        Description = item.Description,
                        RequestContextMenuItemsAsync = (primary, selection) =>
                        {
                            return CreateGroupMenuItems(primary);
                        },
                    };

                    List<SimpleTreeViewNodeModel> nodeChildren = [];
                    List<MediaModel> sorted = sortBy.SortMedia(item.Items, x => x, filter.MediaOrderMethod);
                    PlaylistModel.Builder playlist = new PlaylistModel.Builder().AddMedia(sorted);
                    foreach (MediaModel media in sorted)
                    {
                        groupNode.Children.Add(MediaToNode(media, playlist));
                    }

                    dataSource.Add(groupNode);
                }
            }
            else
            {
                List<MediaModel> sorted = sortBy.SortMedia(items, x => x, filter.MediaOrderMethod);
                PlaylistModel.Builder playlist = new PlaylistModel.Builder().AddMedia(sorted);
                foreach (MediaModel media in sorted)
                {
                    dataSource.Add(MediaToNode(media, playlist));
                }
            }

            CoroutineUtils.RunInMainThread(() =>
            {
                void UpdateItem(SimpleTreeViewNodeModel from, SimpleTreeViewNodeModel to)
                {
                    from.Glyph = to.Glyph;
                    from.Description = to.Description;
                    from.CanExpand = to.CanExpand;
                    from.Clicked = to.Clicked;
                    from.RequestContextMenuItemsAsync = to.RequestContextMenuItemsAsync;
                    DiffUtils.UpdateCollection(from.Children, to.Children, (a, b) => a.Title == b.Title, UpdateItem);
                }

                DiffUtils.UpdateCollection(DataSource, dataSource, (x, y) => x.Title == y.Title, UpdateItem);
                NoMediaVisible = DataSource.Count == 0;
            });
        });
    }

    private Task<List<BaseMenuFlyoutItemModel>> CreateGroupMenuItems(SimpleTreeViewNodeModel node)
    {
        List<MediaModel> mediaItems = [.. node.CollectDataContext<MediaModel>()];
        return MenuFlyoutItemsCreator.CreateMediaGroupMenuItems(
            _actionHandler, mediaItems,
            node.ExpandAll, node.CollapseAll);
    }

    public class DropDownButtonModel
    {
        public string Name { get; set; } = string.Empty;
        public IEnumerable<BaseMenuFlyoutItemModel> Items { get; set; } = [];
    }
}
