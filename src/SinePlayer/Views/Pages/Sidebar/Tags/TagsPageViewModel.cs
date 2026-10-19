// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.Actions;
using SinePlayer.Common.Localization;
using SinePlayer.Common.Misc;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.Algorithm;
using SinePlayer.Core.Common.Lifecycle;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Database.SqlHelpers;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Data.Models.TagInfo;
using SinePlayer.Data.Tables;
using SinePlayer.Helpers.MenuFlyoutHelpers;
using SinePlayer.Helpers.Misc;
using SinePlayer.Helpers.Navigation;
using SinePlayer.ViewModels;

namespace SinePlayer.Views.Pages.Sidebar.Tags;

internal partial class TagsPageViewModel : INotifyPropertyChanged
{
    private const int SEARCH_DELAY = 200;

    public event PropertyChangedEventHandler? PropertyChanged;

    public readonly MutableLiveData<string> EditTagCategoryLiveData = new();
    public readonly MutableLiveData<KeyValuePair<string, string>> EditTagLiveData = new();

    public ObservableCollection<SimpleTreeViewNodeModel> DataSource { get; set; } = [];

    private bool _noTagsVisible = false;
    public bool NoTagsVisible
    {
        get => _noTagsVisible;
        set
        {
            if (_noTagsVisible != value)
            {
                _noTagsVisible = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NoTagsVisible)));
            }
        }
    }

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

    private ActionHandler _actionHandler = ActionHandler.Dummy;
    private bool _updatingTags = false;
    private bool _updatingTagsInvalidated = false;
    private string _searchText = string.Empty;
    private bool _searchSubmitted = false;

    public void Initialize(ActionHandler actionHandler)
    {
        _actionHandler = actionHandler;
    }

    public void UpdateTags()
    {
        CoroutineUtils.Run(ScheduleUpdateTags);
    }

    public void SetSearchText(string searchText)
    {
        searchText = searchText.Trim();
        if (searchText == _searchText)
        {
            return;
        }

        _searchText = searchText;
        if (_searchSubmitted)
        {
            return;
        }

        _searchSubmitted = true;
        CoroutineUtils.Run(async () =>
        {
            await Task.Delay(SEARCH_DELAY);
            _searchSubmitted = false;
            await ScheduleUpdateTags();
        });
    }

    private async Task ScheduleUpdateTags()
    {
        if (_updatingTags)
        {
            _updatingTagsInvalidated = true;
            return;
        }

        _updatingTags = true;
        try
        {
            do
            {
                _updatingTagsInvalidated = false;
                await UpdateTagsInternal();
            }
            while (_updatingTagsInvalidated);
        }
        finally
        {
            _updatingTags = false;
        }
    }

    private async Task UpdateTagsInternal()
    {
        List<SimpleTreeViewNodeModel> dataSource = await GenerateNodeTree();

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
        NoTagsVisible = dataSource.Count == 0;
    }

    private async Task<List<SimpleTreeViewNodeModel>> GenerateNodeTree()
    {
        Dictionary<long, TagCateogryEntry> tagCategoryMapper = [];
        await MediaHandle.Enqueue(() =>
        {
            {
                var command = SelectCommand.Create(MediaTagCategoryTable.Instance);
                IReaderToken<long> idToken = command.PutQueryInt64(MediaTagCategoryTable.ColumnId);
                IReaderToken<string> nameToken = command.PutQueryString(MediaTagCategoryTable.ColumnName);
                IReaderToken<long> mediaIdToken = command.PutQueryInt64(MediaTagCategoryTable.ColumnMediaId);
                using SelectCommand.IReader reader = command.Execute();
                while (reader.Read())
                {
                    long id = idToken.GetValue();
                    string name = nameToken.GetValue();
                    long mediaId = mediaIdToken.GetValue();
                    tagCategoryMapper[id] = new(name, mediaId);
                }
            }

            {
                var command = SelectCommand.Create(MediaTagTable.Instance);
                IReaderToken<string> contentToken = command.PutQueryString(MediaTagTable.ColumnContent);
                IReaderToken<long> categoryIdToken = command.PutQueryInt64(MediaTagTable.ColumnTagCategoryId);
                using SelectCommand.IReader reader = command.Execute();
                while (reader.Read())
                {
                    string content = contentToken.GetValue();
                    long categoryId = categoryIdToken.GetValue();
                    if (tagCategoryMapper.TryGetValue(categoryId, out TagCateogryEntry? tagCategoryModel))
                    {
                        tagCategoryModel.Tags.Add(content);
                    }
                }
            }

            return true;
        });

        Dictionary<string, Dictionary<string, TagEntry>> tagCategoryMap = [];
        TagEntry PutTag(string tagCategory, string tag)
        {
            if (!tagCategoryMap.TryGetValue(tagCategory, out Dictionary<string, TagEntry>? tags))
            {
                tags = [];
                tagCategoryMap[tagCategory] = tags;
            }

            if (!tags.TryGetValue(tag, out TagEntry? tagModel))
            {
                tagModel = new();
                tags[tag] = tagModel;
            }

            return tagModel;
        }

        HashSet<long> requestingMediaIds = [];
        foreach (TagCateogryEntry tagCategoryModel in tagCategoryMapper.Values)
        {
            foreach (string tag in tagCategoryModel.Tags)
            {
                TagEntry tagModel = PutTag(tagCategoryModel.Name, tag);
                tagModel.MediaIds.Add(tagCategoryModel.MediaId);
                requestingMediaIds.Add(tagCategoryModel.MediaId);
            }
        }

        Dictionary<long, MediaModel> mediaMap = [];
        {
            List<MediaModel> requestedMedia = await MediaModel.BatchFromId(requestingMediaIds);
            foreach (MediaModel media in requestedMedia)
            {
                if (media.IsHidden)
                {
                    continue;
                }

                mediaMap[media.Id] = media;
            }
        }

        string[] keywords = string.IsNullOrEmpty(_searchText) ? [] : _searchText.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool MatchSearchText(string text)
        {
            return keywords.Length == 0 || StringUtils.FastMatch(keywords, text.ToLowerInvariant()) > 0;
        }

        SimpleTreeViewNodeModel MediaToNode(MediaModel media, PlayQueueModel.Builder playQueue)
        {
            return new()
            {
                DataContext = media,
                Glyph = MediaGlyphHelper.GetMediaGlyph(media),
                Title = media.Title,
                CanExpand = false,
                Clicked = item =>
                {
                    Route route = OpenMediaHelper.GetMediaRoute(media, playQueue: playQueue);
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
                        playQueue: playQueue,
                        selectedItems: selectedMedia,
                        canSelect: !SelectionMode);
                },
            };
        }

        List<SimpleTreeViewNodeModel> dataSource = [];
        IEnumerable<string> tagCategories = tagCategoryMap.Keys
            .OrderBy(StringUtils.SmartFileNameKeySelector, StringUtils.SmartFileNameComparer);
        foreach (string tagCategory in tagCategories)
        {
            Dictionary<string, TagEntry> tagMap = tagCategoryMap[tagCategory];
            IEnumerable<string> tags = tagMap.Keys
                .OrderBy(StringUtils.SmartFileNameKeySelector, StringUtils.SmartFileNameComparer);

            TagCateogryModel tagCategoryModel = new(tagCategory);
            SimpleTreeViewNodeModel tagCategoryNode = new()
            {
                DataContext = tagCategoryModel,
                Glyph = "\uE8EC",
                Title = tagCategory,
                CanExpand = true,
                IsExpanded = false,
                RequestContextMenuItemsAsync = CreateTagCategoryMenuItems,
            };

            foreach (string tag in tags)
            {
                TagEntry tagEntry = tagMap[tag];
                bool tagMatched = MatchSearchText(tag);

                TagModel tagModel = new(tagCategory, tag);
                SimpleTreeViewNodeModel tagNode = new()
                {
                    DataContext = tagModel,
                    Glyph = "\uE8EC",
                    Title = tag,
                    CanExpand = true,
                    IsExpanded = false,
                    RequestContextMenuItemsAsync = CreateTagMenuItems,
                };

                IEnumerable<MediaModel> sortedMedia = tagEntry.MediaIds
                    .Where(mediaMap.ContainsKey)
                    .Select(x => mediaMap[x])
                    .Where(x => tagMatched || MatchSearchText(x.Title))
                    .OrderBy(x => StringUtils.SmartFileNameKeySelector(x.Title), StringUtils.SmartFileNameComparer);
                PlayQueueModel.Builder playQueue = new PlayQueueModel.Builder().AddMedia(sortedMedia);
                IEnumerable<SimpleTreeViewNodeModel> tagChildren = sortedMedia
                    .Select(x => MediaToNode(x, playQueue));

                foreach (SimpleTreeViewNodeModel child in tagChildren)
                {
                    tagNode.Children.Add(child);
                }

                tagNode.Description = $"({tagNode.Children.Count})";

                if (tagNode.Children.Count > 0 || tagMatched)
                {
                    tagCategoryNode.Children.Add(tagNode);
                }
            }

            tagCategoryNode.Description = $"({tagCategoryNode.Children.Count})";

            if (tagCategoryNode.Children.Count > 0)
            {
                dataSource.Add(tagCategoryNode);
            }
        }

        return dataSource;
    }

    private async Task<List<BaseMenuFlyoutItemModel>> CreateTagCategoryMenuItems(SimpleTreeViewNodeModel primary, IEnumerable<SimpleTreeViewNodeModel> selection)
    {
        List<BaseMenuFlyoutItemModel> items = [];

        if (primary.DataContext is not TagCateogryModel primaryCategory)
        {
            return items;
        }

        IEnumerable<TagCateogryModel> selectedCategories = selection
            .SelectMany(x => x.CollectDataContext<TagCateogryModel>());
        if (!selectedCategories.Any(x => x.Name == primaryCategory.Name))
        {
            selectedCategories = [primaryCategory];
        }

        items.Add(new SimpleMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.Edit,
            Icon = new FontIconSource() { Glyph = "\uE70F" },
            Click = () =>
            {
                EditTagCategoryLiveData.Emit(primaryCategory.Name);
            },
        });

        items.Add(new SimpleMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.Delete,
            Icon = new FontIconSource() { Glyph = "\uE74D" },
            Click = () =>
            {
                CoroutineUtils.Run(() => BusyStateManager.WithBusyState(async () =>
                {
                    await Task.WhenAll(selectedCategories.Select(x => TagCategoryInfoModel.Delete(x.Name)));
                }));
            },
        });

        List<MediaModel> mediaItems = [.. primary.CollectDataContext<MediaModel>()];
        return await MenuFlyoutItemsCreator.CreateMediaGroupMenuItems(
            _actionHandler, mediaItems,
            primary.ExpandAll, primary.CollapseAll, customItems: items);
    }

    private async Task<List<BaseMenuFlyoutItemModel>> CreateTagMenuItems(SimpleTreeViewNodeModel primary, IEnumerable<SimpleTreeViewNodeModel> selection)
    {
        List<BaseMenuFlyoutItemModel> items = [];

        if (primary.DataContext is not TagModel primaryTag)
        {
            return items;
        }

        IEnumerable<TagModel> selectedTags = selection.SelectMany(x => x.CollectDataContext<TagModel>());
        if (!selectedTags.Any(x => x.Name == primaryTag.Name))
        {
            selectedTags = [primaryTag];
        }

        items.Add(new SubItemMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.Links,
            Icon = new FontIconSource() { Glyph = "\uE71B" },
            Items = await MenuFlyoutItemsCreator.CreateTagLinkMenuItems(primaryTag.CategoryName, primaryTag.Name, _actionHandler),
        });

        items.Add(new SimpleMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.Edit,
            Icon = new FontIconSource() { Glyph = "\uE70F" },
            Click = () =>
            {
                EditTagLiveData.Emit(new(primaryTag.CategoryName, primaryTag.Name));
            },
        });

        items.Add(new SimpleMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.Delete,
            Icon = new FontIconSource() { Glyph = "\uE74D" },
            Click = () =>
            {
                CoroutineUtils.Run(() => BusyStateManager.WithBusyState(async () =>
                {
                    await Task.WhenAll(selectedTags.Select(x => TagInfoModel.Delete(x.CategoryName, x.Name)));
                }));
            },
        });

        List<MediaModel> mediaItems = [.. primary.CollectDataContext<MediaModel>()];
        return await MenuFlyoutItemsCreator.CreateMediaGroupMenuItems(
            _actionHandler, mediaItems,
            primary.ExpandAll, primary.CollapseAll, customItems: items);
    }

    private class TagCateogryEntry(string name, long mediaId)
    {
        public string Name { get; } = name;
        public long MediaId { get; } = mediaId;
        public HashSet<string> Tags { get; } = [];
    }

    private class TagEntry
    {
        public HashSet<long> MediaIds { get; } = [];
    }

    private class TagCateogryModel(string name)
    {
        public string Name { get; } = name;
    }

    private class TagModel(string categoryName, string name)
    {
        public string CategoryName { get; } = categoryName;
        public string Name { get; } = name;
    }
}
