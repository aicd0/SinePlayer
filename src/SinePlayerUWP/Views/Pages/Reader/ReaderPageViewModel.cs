// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;

using SinePlayerUWP.Common.Actions;
using SinePlayerUWP.Common.Localization;
using SinePlayerUWP.Core.Common.Lifecycle;
using SinePlayerUWP.Core.Common.Utils;
using SinePlayerUWP.Data.Models.Comic;
using SinePlayerUWP.Data.Models.Misc;
using SinePlayerUWP.Data.Models.Playback;
using SinePlayerUWP.Helpers.MenuFlyoutHelpers;

namespace SinePlayerUWP.Views.Pages.Reader;

internal partial class ReaderPageViewModel : INotifyPropertyChanged
{
    private ActionHandler _actionHandler = ActionHandler.Dummy;

    // Comic Status
    private ComicModel? _comic;
    private LoadComicArgs? _pendingComic;
    private bool _isLoading = false;
    private bool? _isFavorite = null;

    public ReaderPageViewModel() { }

    public event PropertyChangedEventHandler? PropertyChanged;

    public readonly MutableLiveData<string> TitleLiveData = new();
    public readonly MutableLiveData<bool> PlaybackChangeLiveData = new();
    public readonly MutableLiveData<ReaderStatusInfo> ReaderStatusLiveData = new(new() { Status = ReaderStatusEnum.Loading });
    public readonly MutableLiveData<bool> ComicChangedLiveData = new();
    public readonly MutableLiveData<bool> IsExternalComicLiveData = new(true);
    public readonly MutableLiveData<bool> IsFavoriteLiveData = new();

    private FlowDirection _preferredFlowDirection = FlowDirection.LeftToRight;
    public FlowDirection PreferredFlowDirection
    {
        get => _preferredFlowDirection;
        set
        {
            _preferredFlowDirection = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreferredFlowDirection)));
        }
    }

    private bool _isFullscreen = false;
    public bool IsFullscreen
    {
        get => _isFullscreen;
        set
        {
            _isFullscreen = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFullscreen)));
        }
    }

    private string _fullscreenButtonGlyph = string.Empty;
    public string FullscreenButtonGlyph
    {
        get => _fullscreenButtonGlyph;
        set
        {
            _fullscreenButtonGlyph = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FullscreenButtonGlyph)));
        }
    }

    private string _fullscreenButtonText = string.Empty;
    public string FullscreenButtonText
    {
        get => _fullscreenButtonText;
        set
        {
            _fullscreenButtonText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FullscreenButtonText)));
        }
    }

    private bool _isPinned = false;
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            _isPinned = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPinned)));
        }
    }

    private string _pinButtonGlyph = string.Empty;
    public string PinButtonGlyph
    {
        get => _pinButtonGlyph;
        set
        {
            _pinButtonGlyph = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinButtonGlyph)));
        }
    }

    private string _pinButtonText = string.Empty;
    public string PinButtonText
    {
        get => _pinButtonText;
        set
        {
            _pinButtonText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinButtonText)));
        }
    }

    private string _zooming = string.Empty;
    public string Zooming
    {
        get => _zooming;
        set
        {
            _zooming = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Zooming)));
        }
    }

    private string _primaryPageIndicatorText = string.Empty;
    public string PrimaryPageIndicatorText
    {
        get => _primaryPageIndicatorText;
        set
        {
            _primaryPageIndicatorText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PrimaryPageIndicatorText)));
        }
    }

    private string _secondaryPageIndicatorText = string.Empty;
    public string SecondaryPageIndicatorText
    {
        get => _secondaryPageIndicatorText;
        set
        {
            _secondaryPageIndicatorText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SecondaryPageIndicatorText)));
        }
    }

    private bool _isAutoPlayEnabled = false;
    public bool IsAutoPlayEnabled
    {
        get => _isAutoPlayEnabled;
        set
        {
            _isAutoPlayEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAutoPlayEnabled)));
        }
    }

    private bool _isAutoPlaying = false;
    public bool IsAutoPlaying
    {
        get => _isAutoPlaying;
        set
        {
            _isAutoPlaying = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAutoPlaying)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayOrPauseButtonTooltip)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayOrPauseButtonGlyph)));
        }
    }

    public string PlayOrPauseButtonTooltip
    {
        get => _isAutoPlaying ? StringResourceProvider.Instance.Pause : StringResourceProvider.Instance.Play;
    }

    public string PlayOrPauseButtonGlyph
    {
        get => _isAutoPlaying ? "\uE769" : "\uE768";
    }

    private bool _isPlaybackNextEnabled = false;
    public bool IsPlaybackNextEnabled
    {
        get => _isPlaybackNextEnabled;
        set
        {
            _isPlaybackNextEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaybackNextEnabled)));
        }
    }

    private bool _isPlaybackPreviousEnabled = false;
    public bool IsPlaybackPreviousEnabled
    {
        get => _isPlaybackPreviousEnabled;
        set
        {
            _isPlaybackPreviousEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaybackPreviousEnabled)));
        }
    }

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

    public PlaylistModel Playlist { get; private set; } = PlaylistModel.CreateEmpty();
    public PlaybackModel Playback { get; } = new();
    public ReaderStatusEnum ReaderStatus => ReaderStatusLiveData.Value.Status;
    public ComicModel? Comic => _comic;

    public void Initialize(ActionHandler actionHandler)
    {
        _actionHandler = actionHandler;
        Playback.PlaybackStateChanged += Playback_PlaybackStateChanged;
    }

    public void Destory()
    {
        Playback.PlaybackStateChanged -= Playback_PlaybackStateChanged;
    }

    public void LoadPlaylist(PlaylistModel playlist, string? serializedPlayback, double initialPage)
    {
        Playlist = playlist;
        Playback.LoadState(playlist, serializedPlayback, initialPage);
    }

    public void SetIsFavorite(bool isFavorite, bool writeDatabase)
    {
        if (_isFavorite == isFavorite)
        {
            return;
        }

        _isFavorite = isFavorite;
        IsFavoriteLiveData.Emit(isFavorite);

        ComicModel? comic = _comic;
        if (writeDatabase && comic != null && !comic.IsExternal)
        {
            if (isFavorite)
            {
                FavoriteModel.Instance.Add(comic.Id, comic.Title1, true);
            }
            else
            {
                FavoriteModel.Instance.RemoveWithId(comic.Id, true);
            }
        }
    }

    public void SetFullscreen(bool isFullscreen)
    {
        IsFullscreen = isFullscreen;
        if (isFullscreen)
        {
            FullscreenButtonGlyph = "\uE73F";
            FullscreenButtonText = StringResourceProvider.Instance.ExitFullscreen;
        }
        else
        {
            FullscreenButtonGlyph = "\uE740";
            FullscreenButtonText = StringResourceProvider.Instance.EnterFullscreen;
        }
    }

    public void SetPinned(bool pinned)
    {
        IsPinned = pinned;
        if (pinned)
        {
            PinButtonGlyph = "\uE77A";
            PinButtonText = StringResourceProvider.Instance.Unpin;
        }
        else
        {
            PinButtonGlyph = "\uE718";
            PinButtonText = StringResourceProvider.Instance.Pin;
        }
    }

    public void SetZooming(int zooming)
    {
        Zooming = $"{zooming}%";
    }

    public void UpdateFavoriteStatus()
    {
        ComicModel? comic = _comic;
        if (comic is not null)
        {
            UpdateFavoriteStatusInternal(comic);
        }
    }

    public void UpdateComicMetadata()
    {
        ComicModel? comic = _comic;
        if (comic is null)
        {
            return;
        }

        BackgroundImageUri = comic.GetExt(ComicExt.BACKGROUND_IMAGE);
    }

    public async Task<IReadOnlyList<BaseMenuFlyoutItemModel>> CreateComicContextMenuItems()
    {
        ComicModel? comic = Comic;
        if (comic is null)
        {
            return [];
        }

        return await MenuFlyoutItemsCreator.CreateComicMenuItems(
            _actionHandler,
            comic,
            playlist: Playlist.ToBuilder(),
            playback: Playback.ToBuilder());
    }

    private void Playback_PlaybackStateChanged(PlaybackStateChangedEventArgs args)
    {
        IsPlaybackNextEnabled = Playback.CanGoNext;
        IsPlaybackPreviousEnabled = Playback.CanGoPrevious;

        PlaylistModel.PlaylistItem? playlistItem = Playback.CurrentItem;
        if (playlistItem is null)
        {
            TitleLiveData.Emit(StringResourceProvider.Instance.Error);
            ReaderStatusLiveData.Emit(new()
            {
                Status = ReaderStatusEnum.Error,
                Description = "The reading list is empty.",
            });
        }
        else if (playlistItem.Comic != _comic || args.Reason == PlaybackStateChangeReason.Refresh)
        {
            TitleLiveData.Emit(playlistItem.Comic.Title);
            ReaderStatusLiveData.Emit(new() { Status = ReaderStatusEnum.Loading });
            LoadComic(new()
            {
                Comic = playlistItem.Comic,
                PlaybackArgs = args,
            });
        }

        PlaybackChangeLiveData.Emit(true);
    }

    private void LoadComic(LoadComicArgs args)
    {
        CoroutineUtils.Run(async () =>
        {
            if (_isLoading)
            {
                _pendingComic = args;
                return;
            }

            _isLoading = true;
            try
            {
                LoadComicArgs? loadingComic = args;
                while (loadingComic != null)
                {
                    await LoadComicInternal(loadingComic);
                    loadingComic = _pendingComic;
                    _pendingComic = null;
                }
            }
            finally
            {
                _isLoading = false;
            }

            ComicChangedLiveData.Emit(true);
        });
    }

    private async Task LoadComicInternal(LoadComicArgs args)
    {
        ComicModel? comic = args.Comic;

        // Clear previous media
        _comic = null;
        BackgroundImageUri = null;

        // Load new media
        if (comic is null)
        {
            ReaderStatusLiveData.Emit(new()
            {
                Status = ReaderStatusEnum.Error,
                Description = "No media is selected.",
            });
            return;
        }

        _comic = comic;
        UpdateComicMetadata();

        // Save to history
        if (!comic.IsExternal)
        {
            await comic.SetAsVisited();
            if (AppSettingsModel.SaveBrowsingHistory)
            {
                await MediaHistoryItemModel.AddAsync(comic.Id, comic.Title1);
            }
        }

        IsExternalComicLiveData.Emit(comic.IsExternal);
        UpdateFavoriteStatusInternal(comic);

        // Media playback will be provided by the media engine in a later migration phase;
        // it will load the media file from comic.Location and resume from the stored position.
        ReaderStatusLiveData.Emit(new()
        {
            Status = ReaderStatusEnum.Error,
            Description = "Media playback is not implemented yet.",
        });
    }

    private void UpdateFavoriteStatusInternal(ComicModel comic)
    {
        bool isFavorite = !comic.IsExternal && FavoriteModel.Instance.FromId(comic.Id) != null;
        SetIsFavorite(isFavorite, false);
    }

    //
    // Types
    //

    public enum ReaderStatusEnum
    {
        Loading,
        Error,
        Working,
    }

    public class ReaderStatusInfo
    {
        public required ReaderStatusEnum Status { get; init; }
        public string Description { get; init; } = string.Empty;
        public int Progress { get; init; } = -1;
    }

    private class LoadComicArgs
    {
        public required ComicModel? Comic { get; init; }
        public required PlaybackStateChangedEventArgs PlaybackArgs { get; init; }
    }
}
