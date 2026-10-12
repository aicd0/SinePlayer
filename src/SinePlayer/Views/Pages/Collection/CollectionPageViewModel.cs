// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

using SinePlayer.Common.Actions;
using SinePlayer.Common.Localization;
using SinePlayer.Core.Common.DebugTools;
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

namespace SinePlayer.Views.Pages.Collection;

internal partial class CollectionPageViewModel : INotifyPropertyChanged
{
    private const string TAG = nameof(CollectionPageViewModel);

    public event PropertyChangedEventHandler? PropertyChanged;

    //
    // Properties
    //

    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        }
    }

    private string _description = string.Empty;
    public string Description
    {
        get => _description;
        set
        {
            _description = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDescriptionVisible)));
        }
    }

    public bool IsDescriptionVisible => Description.Length > 0;

    private double _rating = -1.0;
    public double Rating
    {
        get => _rating;
        set
        {
            _rating = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rating)));
        }
    }

    private string? _coverImageUri = null;
    public string? CoverImageUri
    {
        get => _coverImageUri;
        set
        {
            _coverImageUri = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImageUri)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCoverImageVisible)));
        }
    }

    public bool IsCoverImageVisible => !string.IsNullOrEmpty(CoverImageUri);

    private string? _backgroundImageUri = null;
    public string? BackgroundImageUri
    {
        get => _backgroundImageUri;
        set
        {
            _backgroundImageUri = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BackgroundImageUri)));
        }
    }

    private bool _isEditable = false;
    public bool IsEditable
    {
        get => _isEditable;
        set
        {
            _isEditable = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditable)));
        }
    }

    private bool _isFavorite = false;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            _isFavorite = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FavoriteButtonText)));
        }
    }

    public string FavoriteButtonText => IsFavorite
        ? StringResourceProvider.Instance.RemoveFromFavorites
        : StringResourceProvider.Instance.AddToFavorites;

    private int _itemCount = 0;
    public int ItemCount
    {
        get => _itemCount;
        set
        {
            if (_itemCount == value)
            {
                return;
            }

            _itemCount = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemCountText)));
        }
    }

    public string ItemCountText => StringResourceProvider.Instance.NItems.Replace("$n", ItemCount.ToString());

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

    public MediaModel? Collection => _collection;
    public ObservableCollection<TagCollectionViewModel> MediaTags { get; } = [];
    public MediaSelectionViewModel MediaSelection { get; }

    public readonly MutableLiveData<IReadOnlyList<MediaItemViewModel>> ResultsLiveData = new();
    public readonly MutableLiveData<string> TitleLiveData = new();

    //
    // Fields
    //

    private readonly MediaSearchEngine _searchEngine = new();
    private readonly ITaskDispatcher _sharedDispatcher = TaskDispatcher.Factory.NewQueue("CollectionPageQueue");

    private ActionHandler _actionHandler = ActionHandler.Dummy;
    private long _collectionId = -1;
    private MediaModel? _collection;
    private List<MediaModel> _mediaItems = [];
    private long _lastSearchTime = 0;
    private int _reloadSubmitted = 0;
    private int _reloadInvalidated = 0;

    public CollectionPageViewModel()
    {
        MediaSelection = new(() => _mediaItems.Count);
    }

    //
    // Lifecycle
    //

    public void Initialize(ActionHandler actionHandler, long collectionId)
    {
        _actionHandler = actionHandler;
        _collectionId = collectionId;

        if (collectionId >= 0)
        {
            _searchEngine.CustomCondition = CollectionModel.CreateMemberCondition(collectionId);
        }

        _searchEngine.SetResultCallback(OnSearchResult);

        ViewType = MediaFilterModel.Instance.LastViewType;

        Refresh();
    }

    public void Refresh()
    {
        Interlocked.Exchange(ref _reloadInvalidated, 1);
        if (Interlocked.CompareExchange(ref _reloadSubmitted, 1, 0) == 1)
        {
            return;
        }

        _sharedDispatcher.SubmitAsync(async () =>
        {
            Interlocked.Exchange(ref _reloadSubmitted, 0);
            while (Interlocked.Exchange(ref _reloadInvalidated, 0) == 1)
            {
                await ReloadNoLock();
            }
        });
    }

    //
    // Search
    //

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
            _searchEngine.Update();
        }
        else
        {
            CoroutineUtils.Run(async () =>
            {
                await Task.Delay(timeRemain);
                _lastSearchTime = GetTick();
                _searchEngine.Update();
            });
        }
    }

    private void OnSearchResult(IReadOnlyList<MediaModel> mediaItems)
    {
        List<MediaModel> filtered = [.. mediaItems];
        filtered.Sort(CompareByTitle);

        List<MediaItemViewModel> items = BuildItems(filtered);
        CoroutineUtils.RunInMainThread(() => ApplyResults(filtered, items));
    }

    //
    // Actions
    //

    public void SetRating(double value)
    {
        MediaModel? collection = _collection;
        if (collection is null)
        {
            return;
        }

        if (value < 1.0)
        {
            if (collection.Rating < 0)
            {
                return;
            }

            CoroutineUtils.Run(() => collection.SetRating(-1));
            return;
        }

        int rating = (int)Math.Round(value * 20.0, MidpointRounding.AwayFromZero);
        if (rating == collection.Rating)
        {
            return;
        }

        CoroutineUtils.Run(() => collection.SetRating(rating));
    }

    public void ToggleFavorite()
    {
        MediaModel? collection = _collection;
        if (collection is null)
        {
            return;
        }

        bool isFavorite = IsFavorite;
        _sharedDispatcher.Submit(() =>
        {
            if (isFavorite)
            {
                FavoriteModel.Instance.RemoveWithId(collection.Id, true);
            }
            else
            {
                FavoriteModel.Instance.Add(collection.Id, collection.Title, true);
            }
        });
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

    //
    // Loading
    //

    private async Task ReloadNoLock()
    {
        Logger.I(TAG, "ReloadNoLock");

        MediaModel? collection = await MediaModel.FromId(_collectionId);
        if (collection is null || !collection.IsCollection)
        {
            return;
        }

        bool isFavorite = FavoriteModel.Instance.FromId(collection.Id) != null;
        _collection = collection;

        await MainThreadUtils.RunInMainThread(() =>
        {
            ApplyInfo(collection, isFavorite);
            _searchEngine.Update();
        });
    }

    private void ApplyInfo(MediaModel collection, bool isFavorite)
    {
        Title = GetTitle(collection);
        Description = collection.Description;
        int rating = collection.Rating;
        Rating = rating >= 0 ? rating * 0.05F : -1.0;
        CoverImageUri = MediaExt.GetCoverImageUri(collection);
        BackgroundImageUri = collection.GetExt(MediaExt.BACKGROUND_IMAGE);
        IsEditable = collection.IsEditable;
        IsFavorite = isFavorite;
        TagCollectionViewModel.Update(MediaTags, collection, _actionHandler);
        TitleLiveData.Emit(collection.Title);
    }

    private void ApplyResults(List<MediaModel> mediaItems, List<MediaItemViewModel> items)
    {
        _mediaItems = mediaItems;
        ItemCount = mediaItems.Count;
        ResultsLiveData.Emit(items);
    }

    private List<MediaItemViewModel> BuildItems(List<MediaModel> mediaItems)
    {
        PlayQueueModel.Builder playQueue = new PlayQueueModel.Builder().AddMedia(mediaItems);
        List<MediaItemViewModel> items = [];
        foreach (MediaModel media in mediaItems)
        {
            var item = new MediaItemViewModel(media)
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
                    IEnumerable<MediaModel>? selectedMedia = MediaSelection.IsSelectMode ? MediaSelection.GetSelectedMedia() : null;
                    return MenuFlyoutItemsCreator.CreateMediaMenuItems(
                        _actionHandler,
                        media,
                        playQueue: playQueue,
                        selectedItems: selectedMedia,
                        canSelect: true);
                },
            };
            item.UpdateProgress(true);
            items.Add(item);
        }

        return items;
    }

    //
    // Utilities
    //

    private static string GetTitle(MediaModel collection)
    {
        return collection.Title1.Length > 0 && collection.Title2.Length > 0
            ? collection.Title1 + "\n" + collection.Title2
            : collection.Title;
    }

    private static int CompareByTitle(MediaModel a, MediaModel b)
    {
        int result = string.Compare(a.Title, b.Title, ignoreCase: true);
        return result != 0 ? result : a.Id.CompareTo(b.Id);
    }

    private static long GetTick()
    {
        return Environment.TickCount64;
    }
};
