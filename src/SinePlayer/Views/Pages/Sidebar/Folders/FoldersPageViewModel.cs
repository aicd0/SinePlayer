// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using SinePlayer.Common.Actions;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.Algorithm;
using SinePlayer.Core.Common.Threading;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Helpers.MenuFlyoutHelpers;
using SinePlayer.Helpers.Misc;
using SinePlayer.Helpers.Navigation;
using SinePlayer.Helpers.Search;
using SinePlayer.ViewModels;

namespace SinePlayer.Views.Pages.Sidebar.Folders;

internal partial class FoldersPageViewModel : INotifyPropertyChanged
{
    private const int SEARCH_DELAY = 200;

    public event PropertyChangedEventHandler? PropertyChanged;

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
    private readonly ITaskDispatcher _sharedDispatcher = TaskDispatcher.Factory.NewQueue("FilterPresetsPageQueue");
    private readonly MediaSearchEngine _searchEngine = new();
    private int _searchSubmitted = 0;

    public void Initialize(ActionHandler actionHandler)
    {
        _actionHandler = actionHandler;
        _searchEngine.SetResultCallback(OnMediaSearchResult);
    }

    public void UpdateMedia()
    {
        ScheduleUpdateMedia();
    }

    public void SetSearchText(string searchText)
    {
        searchText = searchText.Trim();
        if (searchText == _searchEngine.SearchText)
        {
            return;
        }

        _searchEngine.SearchText = searchText;
        if (Interlocked.CompareExchange(ref _searchSubmitted, 1, 0) == 1)
        {
            return;
        }

        CoroutineUtils.Run(async () =>
        {
            await Task.Delay(SEARCH_DELAY);
            Interlocked.Exchange(ref _searchSubmitted, 0);
            _searchEngine.Update();
        });
    }

    private void ScheduleUpdateMedia()
    {
        _searchEngine.Update();
    }

    private void OnMediaSearchResult(IReadOnlyList<MediaModel> items)
    {
        CoroutineUtils.Run(async () =>
        {
            List<SimpleTreeViewNodeModel> dataSource = await GenerateNodeTree(items);

            void UpdateItem(SimpleTreeViewNodeModel from, SimpleTreeViewNodeModel to)
            {
                from.Glyph = to.Glyph;
                from.Description = to.Description;
                from.CanExpand = to.CanExpand;
                from.Clicked = to.Clicked;
                from.RequestContextMenuItemsAsync = to.RequestContextMenuItemsAsync;
                DiffUtils.UpdateCollection(from.Children, to.Children, (a, b) => a.Title == b.Title, UpdateItem);
            }

            CoroutineUtils.RunInMainThread(() =>
            {
                DiffUtils.UpdateCollection(DataSource, dataSource, (x, y) => x.Title == y.Title, UpdateItem);
                NoMediaVisible = DataSource.Count == 0;
            });
        });
    }

    private async Task<List<SimpleTreeViewNodeModel>> GenerateNodeTree(IReadOnlyList<MediaModel> mediaItems)
    {
        FolderNode rootNode = new() { Name = string.Empty };
        foreach (MediaModel media in mediaItems)
        {
            FolderNode currentNode = rootNode;
            IReadOnlyList<string> folders = media.FolderViewPath;
            foreach (string folder in folders)
            {
                if (!currentNode.Folders.TryGetValue(folder, out FolderNode? folderNode))
                {
                    folderNode = new() { Name = folder };
                    currentNode.Folders[folder] = folderNode;
                }

                currentNode = folderNode;
            }

            currentNode.Media.Add(media);
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

        List<SimpleTreeViewNodeModel> CreateNodes(FolderNode folderNode)
        {
            List<SimpleTreeViewNodeModel> nodes = [];

            IEnumerable<string> folderNames = folderNode.Folders.Keys
                .OrderBy(StringUtils.SmartFileNameKeySelector, StringUtils.SmartFileNameComparer);
            foreach (string folder in folderNames)
            {
                FolderNode subFolderNode = folderNode.Folders[folder];

                SimpleTreeViewNodeModel item = new()
                {
                    Glyph = "\uE8B7",
                    Title = subFolderNode.Name,
                    CanExpand = true,
                    RequestContextMenuItemsAsync = CreateFolderMenuItems,
                };

                List<SimpleTreeViewNodeModel> subNodes = CreateNodes(subFolderNode);
                foreach (SimpleTreeViewNodeModel node in subNodes)
                {
                    item.Children.Add(node);
                }

                nodes.Add(item);
            }

            IEnumerable<MediaModel> sortedMedia = folderNode.Media
                .OrderBy(x => StringUtils.SmartFileNameKeySelector(x.Title), StringUtils.SmartFileNameComparer);
            PlayQueueModel.Builder playQueue = new PlayQueueModel.Builder().AddMedia(sortedMedia);
            foreach (MediaModel media in sortedMedia)
            {
                nodes.Add(MediaToNode(media, playQueue));
            }

            if (nodes.Count == 1 && nodes[0].CanExpand)
            {
                nodes[0].IsExpanded = true;
            }

            return nodes;
        }

        return CreateNodes(rootNode);
    }

    private async Task<List<BaseMenuFlyoutItemModel>> CreateFolderMenuItems(SimpleTreeViewNodeModel primary, IEnumerable<SimpleTreeViewNodeModel> selection)
    {
        List<MediaModel> mediaItems = [.. primary.CollectDataContext<MediaModel>()];
        return await MenuFlyoutItemsCreator.CreateMediaGroupMenuItems(
            _actionHandler, mediaItems,
            primary.ExpandAll, primary.CollapseAll);
    }

    private class FolderNode
    {
        public required string Name { get; init; }
        public List<MediaModel> Media { get; } = [];
        public Dictionary<string, FolderNode> Folders { get; } = [];
    }
}
