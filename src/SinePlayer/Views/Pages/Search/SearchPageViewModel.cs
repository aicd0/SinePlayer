// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

using SinePlayer.Common.Actions;
using SinePlayer.Core.Common.Lifecycle;
using SinePlayer.Core.Common.Threading;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Helpers.MenuFlyoutHelpers;
using SinePlayer.Helpers.Misc;
using SinePlayer.Helpers.Navigation;
using SinePlayer.Helpers.Search;
using SinePlayer.UserControls.MediaSelection;
using SinePlayer.ViewModels;

namespace SinePlayer.Views.Pages.Search;

internal partial class SearchPageViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _isLoadingRingVisible;
    public bool IsLoadingRingVisible
    {
        get => _isLoadingRingVisible;
        set
        {
            _isLoadingRingVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoadingRingVisible)));
        }
    }

    private string _title = "";
    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        }
    }

    private MediaFilterModel.ViewTypeEnum _viewType = MediaFilterModel.ViewTypeEnum.Medium;
    public MediaFilterModel.ViewTypeEnum ViewType
    {
        get => _viewType;
        set
        {
            if (_viewType == value)
            {
                return;
            }

            _viewType = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ViewType)));
        }
    }

    private bool _isResultGridVisible;
    public bool IsResultGridVisible
    {
        get => _isResultGridVisible;
        set
        {
            _isResultGridVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsResultGridVisible)));
        }
    }

    private string _noResultText = string.Empty;
    public string NoResultText
    {
        get => _noResultText;
        set
        {
            _noResultText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NoResultText)));
        }
    }

    private bool _isNoResultTextVisible;
    public bool IsNoResultTextVisible
    {
        get => _isNoResultTextVisible;
        set
        {
            _isNoResultTextVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNoResultTextVisible)));
        }
    }

    public readonly MutableLiveData<IReadOnlyList<MediaItemViewModel>> ResultsLiveData = new();

    public MediaSelectionViewModel MediaSelection { get; }
    public bool IsLoading;
    public bool IsResultEmpty => SearchResults.Count == 0;
    public List<MediaItemViewModel> SearchResults { get; private set; } = [];

    private readonly ITaskDispatcher _sharedDispatcher = TaskDispatcher.DefaultQueue;
    private ActionHandler _actionHandler = ActionHandler.Dummy;
    private readonly MediaSearchEngine _searchEngine = new();

    public SearchPageViewModel()
    {
        MediaSelection = new(() => SearchResults.Count);
    }

    public void Initialize(ActionHandler actionHandler, string searchText)
    {
        _actionHandler = actionHandler;
        _searchEngine.SetResultCallback(OnSearchResult);

        ViewType = MediaFilterModel.Instance.LastViewType;

        IsLoading = true;
        UpdateUI();
        MediaSelection.SetSelectMode(false);

        _searchEngine.SearchText = searchText;
        Refresh();
    }

    public void SelectViewType(MediaFilterModel.ViewTypeEnum viewType)
    {
        if (ViewType == viewType)
        {
            return;
        }

        ViewType = viewType;
        MediaFilterModel.Instance.LastViewType = viewType;
    }

    public void Refresh()
    {
        _searchEngine.Update();
    }

    public void UpdateUI()
    {
        IsLoadingRingVisible = IsLoading;
        IsResultGridVisible = !IsLoading && !IsResultEmpty;
        IsNoResultTextVisible = !IsLoading && IsResultEmpty;
    }

    private void OnSearchResult(IReadOnlyList<MediaModel> mediaItems)
    {
        _sharedDispatcher.Submit(() =>
        {
            IEnumerable<MediaModel> sortedMedia = mediaItems.OrderBy(x => x.Title);
            List<MediaItemViewModel> newItems = [];
            PlayQueueModel.Builder playQueue = new PlayQueueModel.Builder().AddMedia(sortedMedia);
            foreach (MediaModel media in sortedMedia)
            {
                MediaItemViewModel item = new(media)
                {
                    OnClick = model =>
                    {
                        if (MediaSelection.IsSelectMode)
                        {
                            return;
                        }

                        Route route = OpenMediaHelper.GetMediaRoute(media, playQueue: playQueue);
                        OpenMediaHelper.OpenMedia(_actionHandler, route);
                    },
                    OnRequestContextFlyoutAsync = model =>
                    {
                        IEnumerable<MediaModel>? selection = MediaSelection.IsSelectMode ? MediaSelection.GetSelectedMedia() : null;
                        return MenuFlyoutItemsCreator.CreateMediaMenuItems(
                            _actionHandler,
                            media,
                            playQueue: playQueue,
                            selectedItems: selection,
                            canSelect: true);
                    },
                };
                item.UpdateProgress(false);
                newItems.Add(item);
            }

            CoroutineUtils.RunInMainThread(() =>
            {
                IsLoading = false;
                SearchResults = newItems;
                ResultsLiveData.Emit(SearchResults);
                UpdateUI();
            });
        });
    }
}
